using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Hummingbird.Airlines.Backend.Storage;

namespace Hummingbird.Airlines.Middleware.Grpc;

/// <summary>
/// gRPC facade over the shared <see cref="BaggageClaimBoard"/> of the arrivals hall.
/// Exposes the three interaction shapes of the board:
///   * GetBoard / GetArrival - unary request/response reads,
///   * WatchBoard - a server stream that replays missed change events (OTAR style)
///     and then pushes every belt transition as it happens.
/// </summary>
public sealed class BaggageClaimGrpcService(BaggageClaimBoard board) : BaggageClaim.BaggageClaimBase
{
    /// <summary>How often the watch loop samples the (clock-derived) board.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// <summary>Full board snapshot: belts 1..5 and every arrival currently displayed.</summary>
    /// <example>grpcurl -plaintext -d '{"speed":30}' 127.0.0.1:5007 BaggageClaim/GetBoard</example>
    public override Task<BoardSnapshot> GetBoard(GetBoardRequest request, ServerCallContext context) =>
        Task.FromResult(ToProto(board.GetSnapshot(request.Speed)));

    /// <summary>Single arrival lookup by flight number, including the belt it is unloaded onto.</summary>
    /// <example>grpcurl -plaintext -d '{"flightNumber":"AF412"}' 127.0.0.1:5007 BaggageClaim/GetArrival</example>
    public override Task<FlightArrival> GetArrival(GetArrivalRequest request, ServerCallContext context)
    {
        var arrival = board.TryGetArrival(request.FlightNumber, request.Speed);
        if (arrival is null)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "ARRIVAL_NOT_FOUND"),
                $"No arrival '{request.FlightNumber}' on the baggage claim board right now.");
        }

        return Task.FromResult(ToProto(arrival));
    }

    /// <summary>
    /// Live board feed. Message 1 is a full snapshot (unless <c>include_snapshot</c> is false);
    /// from then on every change is pushed as a <see cref="BoardEvent"/> within a second.
    /// With <c>last_event_sequence &gt; 0</c> the stream first replays the missed events.
    /// </summary>
    public override async Task WatchBoard(
        WatchBoardRequest request,
        IServerStreamWriter<BoardUpdate> responseStream,
        ServerCallContext context)
    {
        var snapshot = board.GetSnapshot(request.Speed);
        var lastEmitted = Math.Max(request.LastEventSequence, 0);

        if (request.IncludeSnapshot)
        {
            await responseStream.WriteAsync(new BoardUpdate
            {
                Snapshot = ToProto(snapshot),
            }, context.CancellationToken);
        }

        // A replay request resumes strictly after the given board-time sequence; a fresh
        // stream only sees events after the snapshot (the snapshot covers the present).
        // Sequence numbers live on the board clock, so resuming is meaningful for the
        // same speed the original events were produced with.
        var fromMs = request.LastEventSequence > 0
            ? request.LastEventSequence
            : snapshot.BoardTimeUtc.ToUnixTimeMilliseconds();

        while (!context.CancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollInterval, context.CancellationToken);
            }
            catch (OperationCanceledException)
            {
                return; // client detached - normal end of a watch stream
            }

            var toMs = BaggageClaimBoard.BoardTime(request.Speed).ToUnixTimeMilliseconds();
            foreach (var change in board.GetEventsBetween(fromMs, toMs, request.Speed))
            {
                if (change.Sequence <= lastEmitted)
                {
                    continue;
                }

                await responseStream.WriteAsync(new BoardUpdate { Event = ToProto(change) }, context.CancellationToken);
                lastEmitted = change.Sequence;
            }

            fromMs = toMs;
        }
    }

    // -----------------------------------------------------------------------
    // Board model -> protobuf translation.
    // -----------------------------------------------------------------------

    private static BoardSnapshot ToProto(BagClaimSnapshot snapshot)
    {
        var proto = new BoardSnapshot
        {
            BoardId = snapshot.BoardId,
            AirportCode = snapshot.Airport.Code,
            AirportName = snapshot.Airport.Name,
            Terminal = snapshot.Airport.Terminal,
            GeneratedAt = Timestamp.FromDateTimeOffset(snapshot.GeneratedAtUtc),
            BoardTime = Timestamp.FromDateTimeOffset(snapshot.BoardTimeUtc),
            Speed = snapshot.Speed,
        };

        foreach (var belt in snapshot.Belts)
        {
            proto.Belts.Add(ToProto(belt));
        }

        foreach (var arrival in snapshot.Arrivals)
        {
            proto.Arrivals.Add(ToProto(arrival));
        }

        return proto;
    }

    private static BeltStatus ToProto(BagClaimBelt belt) => new()
    {
        Number = belt.Number,
        Idle = belt.Idle,
        FlightNumber = belt.FlightNumber ?? string.Empty,
    };

    private static FlightArrival ToProto(BagClaimArrival arrival) => new()
    {
        FlightNumber = arrival.FlightNumber,
        AirlineCode = arrival.Airline.ToString().ToLowerInvariant(),
        AirlineName = arrival.AirlineName,
        OriginCode = arrival.OriginCode,
        OriginName = arrival.OriginName,
        OriginCity = arrival.OriginCity,
        ScheduledArrival = Timestamp.FromDateTimeOffset(arrival.ScheduledArrivalUtc),
        Belt = arrival.Belt,
        State = ToProto(arrival.State),
    };

    private static BoardEvent ToProto(BagClaimEvent change) => new()
    {
        Sequence = change.Sequence,
        OccurredAt = Timestamp.FromDateTimeOffset(change.OccurredAtUtc),
        Kind = change.Kind switch
        {
            BagClaimEventKind.FlightInbound => ChangeKind.FlightInbound,
            BagClaimEventKind.FlightArrived => ChangeKind.FlightArrived,
            BagClaimEventKind.UnloadingStarted => ChangeKind.UnloadingStarted,
            BagClaimEventKind.LastBag => ChangeKind.LastBag,
            BagClaimEventKind.BeltReleased => ChangeKind.BeltReleased,
            BagClaimEventKind.FlightOffBoard => ChangeKind.FlightOffBoard,
            _ => ChangeKind.Unspecified,
        },
        FlightNumber = change.FlightNumber,
        State = ToProto(change.State),
        Belt = change.Belt,
    };

    private static ArrivalState ToProto(BagClaimFlightState state) => state switch
    {
        BagClaimFlightState.Inbound => ArrivalState.Inbound,
        BagClaimFlightState.Arrived => ArrivalState.Arrived,
        BagClaimFlightState.Unloading => ArrivalState.Unloading,
        BagClaimFlightState.LastBag => ArrivalState.LastBag,
        BagClaimFlightState.Released => ArrivalState.Released,
        _ => ArrivalState.Unspecified,
    };
}
