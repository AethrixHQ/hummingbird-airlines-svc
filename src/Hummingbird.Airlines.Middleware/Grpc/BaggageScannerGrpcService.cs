using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Hummingbird.Airlines.Backend.Storage;

namespace Hummingbird.Airlines.Middleware.Grpc;

/// <summary>
/// Bidirectional bag scanner session (make-up room). The scanner cart streams
/// <see cref="ScanRequest"/>s; each scan is validated deterministically and
/// acknowledged individually. Rules, in evaluation order:
///   0. <c>LIMIT_EXCEEDED</c> - more than 8 accepted bags in one session,
///   1. <c>MALFORMED</c>      - tag does not match the IATA barcode shape,
///   2. <c>UNKNOWN_ZONE</c>   - zone outside the make-up map (MU1..MU3),
///   3. <c>DUPLICATE</c>      - tag already accepted within this session,
///   4. <c>ACCEPTED</c>       - bag filed to a deterministic make-up belt.
/// When the client half-closes its stream, one final <see cref="ScanSummary"/>
/// is emitted and the server ends the call. No state survives the session.
/// </summary>
public sealed class BaggageScannerGrpcService : BaggageScanner.BaggageScannerBase
{
    /// <summary>Maximum number of bags a single scanner session may accept.</summary>
    public const int MaxAcceptedPerSession = BagScanRules.MaxAcceptedPerSession;

    public override async Task ScanBags(
        IAsyncStreamReader<ScanRequest> requestStream,
        IServerStreamWriter<ScannerUpdate> responseStream,
        ServerCallContext context)
    {
        var sessionId = $"scanner-session-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds():X}";
        var accepted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var byDisposition = new Dictionary<string, int>();
        var acceptedCount = 0;
        var rejectedCount = 0;

        while (await requestStream.MoveNext(context.CancellationToken))
        {
            var scan = requestStream.Current;
            var (disposition, message, belt, normalizedTag) = Evaluate(scan.TagNumber, scan.Zone, accepted, acceptedCount);

            if (disposition == Disposition.Accepted)
            {
                acceptedCount++;
                accepted.Add(normalizedTag);
            }
            else
            {
                rejectedCount++;
            }

            var key = ToKey(disposition);
            byDisposition[key] = byDisposition.GetValueOrDefault(key) + 1;

            await responseStream.WriteAsync(new ScannerUpdate
            {
                ScanId = scan.ScanId,
                Ack = new ScanAck
                {
                    Disposition = disposition,
                    Message = message,
                    AcceptedCount = acceptedCount,
                    RejectedCount = rejectedCount,
                    Belt = belt,
                },
            }, context.CancellationToken);
        }

        // Client half-closed its side: close the session with a summary.
        var summary = new ScanSummary
        {
            SessionId = sessionId,
            ClosedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            TotalScans = acceptedCount + rejectedCount,
            Accepted = acceptedCount,
            Rejected = rejectedCount,
        };
        foreach (var (key, count) in byDisposition)
        {
            summary.ByDisposition[key] = count;
        }

        await responseStream.WriteAsync(new ScannerUpdate
        {
            Summary = summary,
        }, context.CancellationToken);
    }

    private static (Disposition Disposition, string Message, int Belt, string NormalizedTag) Evaluate(
        string tagNumber,
        string zone,
        IReadOnlySet<string> accepted,
        int acceptedCount)
    {
        var (disposition, message, belt, normalizedTag) =
            BagScanRules.Evaluate(tagNumber, zone, accepted, acceptedCount);
        return (ToProto(disposition), message, belt, normalizedTag);
    }

    private static Disposition ToProto(ScanDisposition disposition) => disposition switch
    {
        ScanDisposition.Accepted => Disposition.Accepted,
        ScanDisposition.Duplicate => Disposition.Duplicate,
        ScanDisposition.Malformed => Disposition.Malformed,
        ScanDisposition.UnknownZone => Disposition.UnknownZone,
        ScanDisposition.LimitExceeded => Disposition.LimitExceeded,
        _ => Disposition.Unspecified,
    };

    private static string ToKey(Disposition disposition) => disposition switch
    {
        Disposition.Accepted => "accepted",
        Disposition.Malformed => "malformed",
        Disposition.Duplicate => "duplicate",
        Disposition.UnknownZone => "unknownZone",
        Disposition.LimitExceeded => "limitExceeded",
        _ => "unspecified",
    };
}
