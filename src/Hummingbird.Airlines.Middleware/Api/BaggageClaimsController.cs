using System.Globalization;
using System.Text;
using System.Text.Json;
using Hummingbird.Airlines.Backend.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Hummingbird.Airlines.Middleware.Api;

/// <summary>
/// HTTP/SSE facade (read-only) over the shared baggage claim board of the arrivals hall
/// (Paris CDG, Terminal 2E, belts "tapis" 1..5).
///
/// Endpoints:
///   * GET /api/v2/baggage-claim            - full board snapshot (JSON)
///   * GET /api/v2/baggage-claim/{flight}   - one arrival and its belt (404 ARRIVAL_NOT_FOUND)
///   * GET /api/v2/baggage-claim/stream     - live feed (text/event-stream, resumable via Last-Event-ID)
///
/// The stream starts with a "snapshot" event (id = board time in ms), then pushes one
/// event per belt transition using the same sequence numbers and JSON shapes as the
/// gRPC WatchBoard stream, so cross-protocol tests stay comparable.
///
/// <c>speed</c> (1..60) compresses the board clock; at 30 a 6-minute arrival cadence
/// yields a belt transition every ~12 wall seconds.
/// </summary>
[ApiController]
[Produces("application/json")]
[Route("api/v2/baggage-claim")]
public class BaggageClaimsController(
    BaggageClaimBoard board,
    IOptions<Microsoft.AspNetCore.Mvc.JsonOptions> jsonOptions) : ControllerBase
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SnapshotResyncInterval = TimeSpan.FromSeconds(20);

    private readonly JsonSerializerOptions json = jsonOptions.Value.JsonSerializerOptions;

    /// <summary>Full board snapshot: belts 1..5 plus every arrival currently displayed.</summary>
    /// <param name="speed">Board-clock compression factor (1..60); 1 = real time.</param>
    /// <returns>The board state for the current instant.</returns>
    /// <response code="200">Board rendered.</response>
    [HttpGet]
    [ProducesResponseType(typeof(BagClaimSnapshot), StatusCodes.Status200OK)]
    public ActionResult<BagClaimSnapshot> GetBoard([FromQuery] double? speed) =>
        Ok(board.GetSnapshot(speed ?? 1));

    /// <summary>Locate one arrival flight and the belt it is unloaded onto.</summary>
    /// <param name="flightNumber">Flight number shown on the board, e.g. AF412 (case-insensitive).</param>
    /// <param name="speed">Board-clock compression factor used for the lookup (1..60).</param>
    /// <returns>The arrival, or a 404 problem when the flight is unknown or off-board.</returns>
    /// <response code="200">Arrival found.</response>
    /// <response code="404">No arrival carries this number right now (code ARRIVAL_NOT_FOUND).</response>
    [HttpGet("{flightNumber}")]
    [ProducesResponseType(typeof(BagClaimArrival), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public ActionResult<BagClaimArrival> GetArrival(string flightNumber, [FromQuery] double? speed)
    {
        var arrival = board.TryGetArrival(flightNumber, speed ?? 1);
        return arrival is not null
            ? Ok(arrival)
            : Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "ARRIVAL_NOT_FOUND",
                detail: $"No arrival '{flightNumber}' is displayed on the baggage claim board right now.");
    }

    /// <summary>
    /// Live baggage claim feed as server-sent events.
    /// Framing: event id = board time in ms; event names are "snapshot" or the change kind
    /// (flightInbound, flightArrived, unloadingStarted, lastBag, beltReleased, flightOffBoard);
    /// data = the same JSON shapes as the REST endpoints. ": keepalive" comments are emitted
    /// every 15 s, a fresh snapshot every 20 s. Watches support resumption replay via the
    /// Last-Event-ID header or the lastEventId query parameter.
    /// Keep the connection open; closing the client terminates the stream without side effects.
    /// </summary>
    /// <param name="cancellation">Client disconnect signal.</param>
    /// <param name="speed">Board-clock compression factor (1..60); 1 = real time.</param>
    /// <param name="lastEventId">Resumption point (event id to replay from, exclusive).</param>
    /// <response code="200">An endless text/event-stream response body.</response>
    [HttpGet("stream")]
    [Produces("text/event-stream")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task Stream(
        CancellationToken cancellation,
        [FromQuery] double? speed,
        [FromQuery] long? lastEventId)
    {
        var effectiveSpeed = BaggageClaimBoard.ClampSpeed(speed);

        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "text/event-stream; charset=utf-8";
        Response.Headers.CacheControl = "no-cache, no-store";
        Response.Headers["X-Accel-Buffering"] = "no";

        long lastDelivered = lastEventId ?? ParseLastEventIdHeader() ?? 0;

        try
        {
            await WriteRaw("retry: 3000\n\n", cancellation);

            var snapshot = board.GetSnapshot(effectiveSpeed);
            var boardNow = snapshot.BoardTimeUtc.ToUnixTimeMilliseconds();
            await WriteSse("snapshot", boardNow, snapshot, cancellation);
            if (lastDelivered == 0)
            {
                lastDelivered = boardNow;
            }

            // Replay the events that occurred while the client was disconnected. The
            // fresh snapshot above already reflects the present; replayed events may
            // repeat state the snapshot contains - that is idempotent for consumers.
            var fromMs = lastDelivered;
            if (lastEventId is { } resume || TryHeaderLastEventId(out resume))
            {
                foreach (var missed in board.GetEventsBetween(resume, boardNow, effectiveSpeed))
                {
                    if (missed.Sequence <= lastDelivered)
                    {
                        continue;
                    }

                    await WriteSse(EventName(missed.Kind), missed.Sequence, missed, cancellation);
                    lastDelivered = missed.Sequence;
                }

                fromMs = Math.Max(resume, boardNow);
            }

            var nextKeepAlive = DateTimeOffset.UtcNow + KeepAliveInterval;
            var nextSnapshotResync = DateTimeOffset.UtcNow + SnapshotResyncInterval;

            while (!cancellation.IsCancellationRequested)
            {
                var nowUtc = DateTimeOffset.UtcNow;
                var wait = PollInterval;
                if (nextKeepAlive - nowUtc < wait)
                {
                    wait = nextKeepAlive - nowUtc;
                }

                if (nextSnapshotResync - nowUtc < wait)
                {
                    wait = nextSnapshotResync - nowUtc;
                }

                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, cancellation);
                }

                nowUtc = DateTimeOffset.UtcNow;
                if (nowUtc >= nextKeepAlive)
                {
                    await WriteRaw($": keepalive {nowUtc:O}\n\n", cancellation);
                    nextKeepAlive = nowUtc + KeepAliveInterval;
                }

                if (nowUtc >= nextSnapshotResync)
                {
                    var refresh = board.GetSnapshot(effectiveSpeed);
                    await WriteSse("snapshot", refresh.BoardTimeUtc.ToUnixTimeMilliseconds(), refresh, cancellation);
                    nextSnapshotResync = nowUtc + SnapshotResyncInterval;
                }

                var boardTimeMs = BaggageClaimBoard.BoardTime(effectiveSpeed).ToUnixTimeMilliseconds();
                foreach (var change in board.GetEventsBetween(fromMs, boardTimeMs, effectiveSpeed))
                {
                    if (change.Sequence <= lastDelivered)
                    {
                        continue;
                    }

                    await WriteSse(EventName(change.Kind), change.Sequence, change, cancellation);
                    lastDelivered = change.Sequence;
                }

                fromMs = boardTimeMs;
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected - the normal, clean way for an SSE stream to end.
        }
        catch (IOException)
        {
            // Peer reset the connection mid-write; nothing to compensate in a pure view.
        }
    }

    // -----------------------------------------------------------------------
    // SSE plumbing.
    // -----------------------------------------------------------------------

    private long? ParseLastEventIdHeader() =>
        TryHeaderLastEventId(out var parsed) ? parsed : null;

    private bool TryHeaderLastEventId(out long parsed)
    {
        parsed = 0;
        return Request.Headers.TryGetValue("Last-Event-ID", out var raw)
            && long.TryParse(raw.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed);
    }

    private static string EventName(BagClaimEventKind kind) => kind switch
    {
        BagClaimEventKind.FlightInbound => "flightInbound",
        BagClaimEventKind.FlightArrived => "flightArrived",
        BagClaimEventKind.UnloadingStarted => "unloadingStarted",
        BagClaimEventKind.LastBag => "lastBag",
        BagClaimEventKind.BeltReleased => "beltReleased",
        _ => "flightOffBoard",
    };

    private async ValueTask WriteSse(string eventName, long id, object payload, CancellationToken cancellation)
    {
        var frame = new StringBuilder(256)
            .Append("event: ").Append(eventName).Append('\n')
            .Append("id: ").Append(id.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append("data: ");
        await Response.Body.WriteAsync(Encoding.UTF8.GetBytes(frame.ToString()), cancellation);
        await Response.Body.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(payload, json), cancellation);
        await Response.Body.WriteAsync("\n\n"u8.ToArray(), cancellation);
        await Response.Body.FlushAsync(cancellation);
    }

    private async ValueTask WriteRaw(string text, CancellationToken cancellation)
    {
        await Response.Body.WriteAsync(Encoding.UTF8.GetBytes(text), cancellation);
        await Response.Body.FlushAsync(cancellation);
    }
}
