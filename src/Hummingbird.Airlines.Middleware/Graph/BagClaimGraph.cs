using System.Runtime.CompilerServices;
using Hummingbird.Airlines.Backend.Domain;
using Hummingbird.Airlines.Backend.Storage;
using HotChocolate;
using HotChocolate.Types;
using Microsoft.Extensions.DependencyInjection;

namespace Hummingbird.Airlines.Middleware.Graph;

/// <summary>
/// GraphQL surface for the baggage claim board ("tapis 1..5") - queries for board state, a
/// subscription that streams the same event sequence the SSE and gRPC transports publish, and
/// mutations that reuse the legacy luggage paths and the shared scanner rule engine
/// (see <see cref="Mutation"/> in BagClaimMutations.cs).
/// The root types are deliberately named Query/Mutation/Subscription (standard GraphQL names) so
/// that spec importers find a discoverable schema root from the SDL.
///
/// SDL for importers: GET /graphql/schema.graphql
/// </summary>
public sealed class Query
{
    /// <summary>The baggage claim board, rendered at the requested speed (1 = real time, up to 60x).</summary>
    public BagClaimSnapshot BagClaimBoard([Service] BaggageClaimBoard board, int speed = 1) =>
        board.GetSnapshot(speed);

    /// <summary>One arrival from the board by flight number (case-insensitive).</summary>
    public BagClaimArrival BagClaimArrival(
        string flightNumber,
        [Service] BaggageClaimBoard board,
        int speed = 1)
    {
        var arrival = board.TryGetArrival(flightNumber, speed);
        return arrival ?? throw new GraphQLException(
            ErrorBuilder.New()
                .SetMessage($"Arrival '{flightNumber}' is not on the board (or already off-board).")
                .SetCode("ARRIVAL_NOT_FOUND")
                .Build());
    }

    /// <summary>One belt ("tapis") of the arrivals hall with its current occupancy.</summary>
    public BagClaimBelt? BagClaimBelt(int number, [Service] BaggageClaimBoard board, int speed = 1) =>
        board.GetSnapshot(speed).Belts.FirstOrDefault(b => b.Number == number);
}

/// <summary>GraphQL subscriptions over WebSocket (graphql-transport-ws) on the same /graphql endpoint.</summary>
public sealed class Subscription
{
    /// <summary>
    /// Stream of baggage claim board changes. Points in board-time milliseconds; resuming is
    /// supported via sinceSeq (same semantics as the SSE Last-Event-ID: lower bound clamped
    /// to the clock origin, and replay history is only meaningful with the unchanged speed).
    /// </summary>
    // SubscribeAndResolve marks "the resolver returns the event stream itself" - exactly this
    // scenario (a clock-derived stream without topics). SubscribeAttribute alone expects the
    // topic/message pattern, which does not fit a pure function of a virtual clock.
    #pragma warning disable CS0618 // HotChocolate 15: SubscribeAndResolve is deprecated but still the stream-self API
    [SubscribeAndResolve]
    #pragma warning restore CS0618
    public async IAsyncEnumerable<BagClaimEvent> BagClaimEvents(
        [Service] BaggageClaimBoard board,
        int speed = 60,
        long sinceSeq = 0,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var wallSpeed = SimClock.ClampSpeed(speed);
        var nowMs = SimClock.Time(wallSpeed).ToUnixTimeMilliseconds();
        var lastMs = sinceSeq > 0 ? Math.Max(sinceSeq, SimClock.OriginUnixMs) : nowMs;

        // Replay the backlog first so subscribers start with a settled board,
        // then follow the live clock (500 ms wall poll is plenty at any speed).
        foreach (var change in board.GetEventsBetween(lastMs, nowMs, wallSpeed))
        {
            yield return change;
        }

        lastMs = Math.Max(lastMs, nowMs);
        while (true)
        {
            try
            {
                await Task.Delay(500, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }

            var toMs = SimClock.Time(wallSpeed).ToUnixTimeMilliseconds();
            foreach (var change in board.GetEventsBetween(lastMs, toMs, wallSpeed))
            {
                yield return change;
            }

            lastMs = Math.Max(lastMs, toMs);
        }
    }
}

/// <summary>Registration of the baggage claim graph (query + mutation + subscription roots).</summary>
public static class BagClaimGraphRegistration
{
    /// <summary>Add the baggage claim GraphQL server; map it with MapGraphQL("/graphql").</summary>
    public static IServiceCollection AddBagClaimGraph(this IServiceCollection services) =>
        services
            .AddGraphQLServer()
            .AddQueryType<Query>()
            .AddMutationType<Mutation>()
            .AddSubscriptionType<Subscription>()
            .Services
            .AddSingleton<BagScanSessionStore>();
}
