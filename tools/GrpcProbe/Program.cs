// Minimal console probe for the Hummingbird baggage claim gRPC services.
//
//   dotnet run --project tools/GrpcProbe -- [endpoint] table [speed]
//   dotnet run --project tools/GrpcProbe -- [endpoint] find <flightNumber> [speed]
//   dotnet run --project tools/GrpcProbe -- [endpoint] watch [seconds] [speed] [lastSeq]
//   dotnet run --project tools/GrpcProbe -- [endpoint] scan [TAG[@ZONE] ...]
//
// endpoint default: http://127.0.0.1:5007 (prior-knowledge cleartext HTTP/2)
// speed 60 compresses the 6-minute arrival cadence down to ~6 wall seconds.

using Grpc.Core;
using Grpc.Net.Client;
using Hummingbird.Airlines.Middleware.Grpc;

var exitCode = 0;

var endpoint = "http://127.0.0.1:5007";
var argList = args;
if (args.Length > 0 && args[0].StartsWith("http", StringComparison.OrdinalIgnoreCase))
{
    endpoint = args[0];
    argList = args[1..];
}

var commandIndex = Array.FindIndex(argList, a => a is "table" or "find" or "watch" or "scan");
var command = commandIndex >= 0 ? argList[commandIndex] : "table";
var rest = commandIndex >= 0 ? argList[(commandIndex + 1)..] : [];

using var channel = GrpcChannel.ForAddress(endpoint);
var client = new BaggageClaim.BaggageClaimClient(channel);

switch (command)
{
    case "find":
    {
        var flight = rest[0];
        var speed = rest.Length > 1 && int.TryParse(rest[1], out var lookupSpeed) ? lookupSpeed : 1;
        try
        {
            PrintArrival(await client.GetArrivalAsync(new GetArrivalRequest
            {
                FlightNumber = flight,
                Speed = speed,
            }));
        }
        catch (RpcException e) when (e.StatusCode == StatusCode.NotFound)
        {
            Console.Error.WriteLine($"NOT FOUND: {e.Status.Detail}");
            exitCode = 2;
        }

        break;
    }

    case "watch":
    {
        // watch [seconds] [speed] [lastSeq]; defaults: 30 s stream, real-time speed.
        var seconds = rest.Length > 0 && int.TryParse(rest[0], out var span) && span > 0 ? span : 30;
        var speed = rest.Length > 1 && int.TryParse(rest[1], out var watchSpeed) ? watchSpeed : 1;
        var lastSeq = rest.Length > 2 && long.TryParse(rest[2], out var seq) ? seq : 0L;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        await WatchAsync(client, speed, lastSeq, cancellation.Token);
        break;
    }

    case "scan":
    {
        // scan (no tags) -> deterministic demo hitting every disposition (accepted,
        // duplicate, malformed, unknownZone, limitExceeded) plus the half-close summary.
        // scan TAG[@ZONE] ... -> stream the given tags (empty zone = make-up default MU1).
        var specs = rest.Length > 0
            ? rest
            :
            [
                "HB248015", "AF118877", "hb248015", "X12", "HB774421@ZZ9",
                "LH550311", "CA330020", "EK170119", "BA215482", "HB144790",
                "EK880931", "CA020141", "HB990001", "HB990002",
            ];

        var scans = specs.Select((spec, index) => ParseScan(spec, index + 1)).ToList();
        Console.WriteLine($"scan mode: streaming {scans.Count} tag(s) into BaggageScanner/ScanBags...");
        await RunScanAsync(new BaggageScanner.BaggageScannerClient(channel), scans);
        break;
    }

    default:
    {
        var speed = rest.Length > 0 && int.TryParse(rest[0], out var boardSpeed) ? boardSpeed : 1;
        PrintBoard(await client.GetBoardAsync(new GetBoardRequest { Airport = "CDG", Speed = speed }));
        break;
    }
}

return exitCode;

static void PrintBoard(BoardSnapshot board)
{
    Console.WriteLine($"board   : {board.BoardId}  ({board.AirportName}, terminal {board.Terminal})");
    Console.WriteLine($"boardT  : {board.BoardTime.ToDateTime():HH:mm:ss}  (speed x{board.Speed})");
    Console.WriteLine("belts   :");
    foreach (var belt in board.Belts)
    {
        Console.WriteLine(belt.Idle
            ? $"  [{belt.Number}] idle"
            : $"  [{belt.Number}] {belt.FlightNumber}");
    }

    Console.WriteLine("arrivals:");
    foreach (var arrival in board.Arrivals)
    {
        PrintArrival(arrival);
    }
}

static void PrintArrival(FlightArrival arrival)
{
    Console.WriteLine(
        $"  {arrival.FlightNumber,-7} {arrival.AirlineCode,-3} from {arrival.OriginCode,-4} " +
        $"arr {arrival.ScheduledArrival.ToDateTime():HH:mm:ss} " +
        $"belt {arrival.Belt,-2} {arrival.State}");
}

static async Task WatchAsync(BaggageClaim.BaggageClaimClient client, int speed, long lastSeq, CancellationToken cancellation)
{
    Console.WriteLine($"watching BaggageClaim/WatchBoard (speed x{speed}, lastSeq {lastSeq})...");
    var updates = client.WatchBoard(new WatchBoardRequest
    {
        Airport = "CDG",
        IncludeSnapshot = true,
        LastEventSequence = lastSeq,
        Speed = speed,
    });

    var count = 0;
    var inconsistencies = 0;
    try
    {
        await foreach (var update in updates.ResponseStream.ReadAllAsync(cancellation))
        {
            count++;
            if (update.PayloadCase == BoardUpdate.PayloadOneofCase.Snapshot)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] snapshot {update.Snapshot.BoardId} belts=" +
                    string.Join(",", update.Snapshot.Belts.Select(b => b.Idle ? "-" : b.FlightNumber)));
            }
            else if (update.PayloadCase == BoardUpdate.PayloadOneofCase.Event)
            {
                var change = update.Event;
                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] seq={change.Sequence} {change.Kind,-23} {change.FlightNumber,-7} belt={change.Belt,-2} {change.State}");

                // Consistency probe: a belt hinted by an event must match the board view.
                if (change.Belt > 0)
                {
                    var board = await client.GetBoardAsync(new GetBoardRequest { Speed = speed });
                    var match = board.Arrivals.FirstOrDefault(a => a.FlightNumber == change.FlightNumber);
                    if (match is not null && match.Belt != change.Belt)
                    {
                        inconsistencies++;
                        Console.WriteLine(
                            $"    !! MISMATCH event belt={change.Belt} vs board belt={match.Belt} for {change.FlightNumber} (state {match.State})");
                    }
                }
            }
        }
    }
    catch (RpcException e) when (e.StatusCode == StatusCode.Cancelled)
    {
        // normal end: the client-side timeout / ctrl-c cancels the call
    }
    catch (OperationCanceledException)
    {
        // expected on timeout / ctrl-c
    }

    Console.WriteLine($"received {count} update(s), {inconsistencies} belt mismatch(es).");
}

static (string ScanId, string Tag, string Zone) ParseScan(string spec, int index)
{
    var at = spec.IndexOf('@');
    var tag = at >= 0 ? spec[..at] : spec;
    var zone = at >= 0 ? spec[(at + 1)..] : string.Empty;
    return ($"s-{index:00}", tag, zone);
}

static async Task RunScanAsync(BaggageScanner.BaggageScannerClient client, IReadOnlyList<(string ScanId, string Tag, string Zone)> scans)
{
    using var call = client.ScanBags();

    // Reader side of the bidi session: print every ack as it arrives, then the summary.
    var readTask = Task.Run(async () =>
    {
        try
        {
            await foreach (var update in call.ResponseStream.ReadAllAsync())
            {
                PrintScanUpdate(update);
            }
        }
        catch (RpcException e)
        {
            Console.Error.WriteLine($"scan stream ended: {e.StatusCode} {e.Status.Detail}");
        }
    });

    // Writer side: pace the cart hardware a little so acks interleave naturally.
    var index = 0;
    foreach (var (scanId, tag, zone) in scans)
    {
        await call.RequestStream.WriteAsync(new ScanRequest
        {
            ScanId = scanId,
            TagNumber = tag,
            Zone = zone,
        });
        index++;
        if (index < scans.Count)
        {
            await Task.Delay(50);
        }
    }

    // Scanner cart done: half-close so the server closes the session with a summary.
    await call.RequestStream.CompleteAsync();
    await readTask;
}

static void PrintScanUpdate(ScannerUpdate update)
{
    if (update.UpdateCase == ScannerUpdate.UpdateOneofCase.Ack)
    {
        var ack = update.Ack;
        Console.WriteLine(
            $"  {update.ScanId,-5} {ack.Disposition,-15} belt={ack.Belt,-2} acc={ack.AcceptedCount,-3} rej={ack.RejectedCount,-3}  {ack.Message}");
    }
    else if (update.UpdateCase == ScannerUpdate.UpdateOneofCase.Summary)
    {
        var summary = update.Summary;
        Console.WriteLine($"  summary {summary.SessionId}");
        Console.WriteLine(
            $"    closedAt={summary.ClosedAt.ToDateTimeOffset():HH:mm:ss} total={summary.TotalScans} accepted={summary.Accepted} rejected={summary.Rejected}");
        foreach (var pair in summary.ByDisposition)
        {
            Console.WriteLine($"    {pair.Key,-15} {pair.Value}");
        }
    }
}
