using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Hummingbird.Airlines.Backend.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Hummingbird.Airlines.Middleware.Api;

/// <summary>
/// Gate turnaround feed for Terminal 2E departures, exposed as a REST snapshot plus an
/// interactive WebSocket.
///
/// WebSocket message protocol (JSON, one object per text frame):
///   client -&gt; server:
///     {"type":"subscribe","speed":60,"sinceSeq":0}   start/refresh the stream (replay backlog)
///     {"type":"unsubscribe"}                         stop the stream, keep the socket
///     {"type":"ping"}                                liveness probe
///   server -&gt; client:
///     {"type":"snapshot", ...}        full gate board, sent right after subscribe
///     {"type":"gateEvent", ...}       lifecycle transition (sequence = feed-time ms)
///     {"type":"ping"}                 server keepalive (~15 s of write silence)
///     {"type":"pong"}                 answer to a client ping
///     {"type":"error","code":...}     MALFORMED_JSON | OVERSIZE_FRAME | UNRECOGNIZED_TYPE
///
/// Unknown "type" values and unparseable frames get an error message and the connection
/// stays open; the client decides when to disconnect (standard close handshake).
/// </summary>
public static class GateFeedEndpoints
{
    /// <summary>Frame size cap for inbound WebSocket messages (client commands are tiny).</summary>
    private const int MaxFrameBytes = 4096;

    private static readonly TimeSpan EventPollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan KeepaliveInterval = TimeSpan.FromSeconds(15);

    public static IEndpointRouteBuilder MapGateFeed(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/v2/gates", (HttpContext http, TurnaroundFeed feed, double? speed) =>
            Results.Json(feed.GetSnapshot(speed ?? 1), Json(http.RequestServices), statusCode: StatusCodes.Status200OK));

        routes.MapGet("/api/v2/gates/ws", async (HttpContext http, TurnaroundFeed feed) =>
        {
            var logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("GateFeed");
            if (!http.WebSockets.IsWebSocketRequest)
            {
                logger.LogWarning("gate feed requested without WebSocket upgrade (answering 426)");
                http.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
                http.Response.ContentType = "application/problem+json";
                await http.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    type = "https://hummingbird.test/errors/gate-feed-upgrade-required",
                    title = "WebSocket upgrade required",
                    status = StatusCodes.Status426UpgradeRequired,
                    detail = "The gate turnaround feed streams bidirectionally. Open it with a WebSocket upgrade, then send a JSON subscribe command.",
                }, Json(http.RequestServices)), http.RequestAborted);
                return;
            }

            var socket = await http.WebSockets.AcceptWebSocketAsync();
            logger.LogInformation("gate feed session opened");
            await RunSessionAsync(socket, feed, Json(http.RequestServices), http.RequestAborted, logger);
        });

        return routes;
    }

    private static JsonSerializerOptions Json(IServiceProvider services) =>
        services.GetRequiredService<IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>().Value.JsonSerializerOptions;

    private static async Task RunSessionAsync(
        WebSocket socket,
        TurnaroundFeed feed,
        JsonSerializerOptions json,
        CancellationToken requestAborted,
        ILogger logger)
    {
        var inbound = Channel.CreateUnbounded<InboundFrame>();

        // Reader task: pump inbound frames (accumulating fragments) into the channel,
        // completing the channel as soon as the socket dies or the client closes.
        var readerTask = Task.Run(async () =>
        {
            var buffer = new byte[MaxFrameBytes];
            using var message = new MemoryStream();
            while (true)
            {
                WebSocketReceiveResult frame;
                try
                {
                    frame = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), requestAborted);
                }
                catch (OperationCanceledException)
                {
                    logger.LogDebug("gate feed reader stopping (receive cancelled)");
                    inbound.Writer.TryComplete();
                    return;
                }
                catch (WebSocketException e)
                {
                    logger.LogWarning(e, "gate feed reader stopping (transport error)");
                    inbound.Writer.TryComplete();
                    return;
                }

                if (frame.MessageType == WebSocketMessageType.Close)
                {
                    try
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None);
                    }
                    catch (WebSocketException)
                    {
                        // peer already gone; nothing left to acknowledge
                    }

                    inbound.Writer.TryComplete();
                    return;
                }

                if (message.Length + frame.Count > MaxFrameBytes)
                {
                    message.SetLength(0);
                    inbound.Writer.TryWrite(new InboundFrame(true, string.Empty));
                }
                else
                {
                    message.Write(buffer, 0, frame.Count);
                    if (frame.EndOfMessage)
                    {
                        inbound.Writer.TryWrite(new InboundFrame(false, Encoding.UTF8.GetString(message.ToArray())));
                        message.SetLength(0);
                    }
                }
            }
        }, requestAborted);

        var subscribed = false;
        var speed = 1d;
        long lastSequenceMs = 0;
        long lastWriteTicks = Environment.TickCount64;

        try
        {
            while (!requestAborted.IsCancellationRequested)
            {
                await Task.Delay(EventPollInterval, requestAborted);
                if (readerTask.IsCompleted)
                {
                    logger.LogDebug("gate feed session ending: inbound channel closed");
                    break;
                }

                while (inbound.Reader.TryRead(out var frame))
                {
                    if (frame.Oversize)
                    {
                        await SendAsync(socket, Error("OVERSIZE_FRAME",
                            $"Frames are limited to {MaxFrameBytes} bytes; the command was dropped."), json, requestAborted);
                        continue;
                    }

                    JsonDocument document;
                    try
                    {
                        document = JsonDocument.Parse(frame.Json);
                    }
                    catch (JsonException)
                    {
                        await SendAsync(socket, Error("MALFORMED_JSON", "Frame is not a valid JSON object."), json, requestAborted);
                        continue;
                    }

                    using (document)
                    {
                        if (document.RootElement.ValueKind != JsonValueKind.Object ||
                            !document.RootElement.TryGetProperty("type", out var typeElement) ||
                            typeElement.ValueKind != JsonValueKind.String)
                        {
                            await SendAsync(socket, Error("MALFORMED_JSON", "Command must be a JSON object with a string 'type' property."), json, requestAborted);
                            continue;
                        }

                        var type = typeElement.GetString()!;
                        switch (type)
                        {
                            case "subscribe":
                            {
                                var requestedSpeed = document.RootElement.TryGetProperty("speed", out var speedElement)
                                    && speedElement.ValueKind == JsonValueKind.Number
                                    ? speedElement.GetDouble()
                                    : 1;
                                var sinceSeq = document.RootElement.TryGetProperty("sinceSeq", out var sinceElement)
                                    && sinceElement.ValueKind == JsonValueKind.Number
                                    && sinceElement.TryGetInt64(out var parsedSeq)
                                        ? parsedSeq
                                        : 0;

                                speed = SimClock.ClampSpeed(requestedSpeed);
                                subscribed = true;
                                lastSequenceMs = sinceSeq > 0
                                    ? Math.Max(sinceSeq, SimClock.OriginUnixMs)
                                    : SimClock.Time(speed).ToUnixTimeMilliseconds();
                                logger.LogInformation("gate feed subscribed (speed x{Speed}, sinceSeq {Since})", speed, sinceSeq);
                                await SendAsync(socket, SnapshotEnvelope(feed.GetSnapshot(speed)), json, requestAborted);
                                break;
                            }

                            case "unsubscribe":
                                subscribed = false;
                                break;

                            case "ping":
                                await SendAsync(socket, new { type = "pong" }, json, requestAborted);
                                break;

                            default:
                                await SendAsync(socket, Error("UNRECOGNIZED_TYPE",
                                    $"'{type}' is not a known command; expected subscribe, unsubscribe or ping."), json, requestAborted);
                                break;
                        }
                    }

                    lastWriteTicks = Environment.TickCount64;
                }

                if (subscribed)
                {
                    var toMs = SimClock.Time(speed).ToUnixTimeMilliseconds();
                    foreach (var change in feed.GetEventsBetween(lastSequenceMs, toMs, speed))
                    {
                        await SendAsync(socket, EventEnvelope(change), json, requestAborted);
                    }

                    lastSequenceMs = Math.Max(lastSequenceMs, toMs);
                }

                if (Environment.TickCount64 - lastWriteTicks >= (long)KeepaliveInterval.TotalMilliseconds)
                {
                    await SendAsync(socket, new { type = "ping" }, json, requestAborted);
                    lastWriteTicks = Environment.TickCount64;
                }
            }
        }
        catch (OperationCanceledException) when (requestAborted.IsCancellationRequested)
        {
            logger.LogDebug("gate feed session ended: client disconnected");
            // client went away - normal ending for a streaming endpoint
        }
        catch (WebSocketException e)
        {
            logger.LogWarning(e, "gate feed session ended: transport error mid-write");
            // transport died mid-write; drop the session quietly
        }
        finally
        {
            inbound.Writer.TryComplete();
        }
    }

    private static async Task SendAsync(WebSocket socket, object payload, JsonSerializerOptions json, CancellationToken cancellation)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, json);
        await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellation);
    }

    private static object SnapshotEnvelope(TurnaroundSnapshot snapshot) => new
    {
        type = "snapshot",
        feedId = snapshot.FeedId,
        generatedAtUtc = snapshot.GeneratedAtUtc,
        feedTimeUtc = snapshot.FeedTimeUtc,
        speed = snapshot.Speed,
        gates = snapshot.Gates,
    };

    private static object EventEnvelope(GateEvent change) => new
    {
        type = "gateEvent",
        sequence = change.Sequence,
        occurredAtUtc = change.OccurredAtUtc,
        kind = change.Kind,
        gate = change.Gate,
        flightNumber = change.FlightNumber,
        state = change.State,
        scheduledDepartureUtc = change.ScheduledDepartureUtc,
    };

    private static object Error(string code, string message) => new
    {
        type = "error",
        code,
        message,
    };

    /// <summary>One inbound frame: either an oversize marker (payload dropped) or the raw JSON text.</summary>
    private readonly record struct InboundFrame(bool Oversize, string Json);
}
