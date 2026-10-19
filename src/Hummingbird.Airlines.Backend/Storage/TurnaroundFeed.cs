using Hummingbird.Airlines.Backend.Domain;

namespace Hummingbird.Airlines.Backend.Storage;

/// <summary>Lifecycle phase of one gate turnaround displayed by the gate feed.</summary>
public enum GateState
{
    /// <summary>No aircraft is parked at the gate.</summary>
    Idle = 0,

    /// <summary>Aircraft parked, turnaround running, boarding not open yet.</summary>
    Occupied = 1,

    /// <summary>Boarding is open (gate open, passengers streaming).</summary>
    Boarding = 2,

    /// <summary>Final call issued; gate will close soon.</summary>
    FinalCall = 3,

    /// <summary>Gate closed, aircraft pushing back off stand.</summary>
    Pushback = 4,

    /// <summary>Aircraft off stand; the gate is free again (flight still shown briefly).</summary>
    Released = 5,
}

/// <summary>Kinds of change events emitted by the gate turnaround feed.</summary>
public enum GateEventKind
{
    /// <summary>Aircraft parked at the gate; turnaround clock starts.</summary>
    GateOccupied = 0,

    /// <summary>Boarding opened.</summary>
    BoardingOpened = 1,

    /// <summary>Final call issued.</summary>
    FinalCall = 2,

    /// <summary>Gate closed and the aircraft started pushing back.</summary>
    PushbackStarted = 3,

    /// <summary>Aircraft off stand; gate free for the next rotation.</summary>
    GateReleased = 4,

    /// <summary>Turnaround completed; flight leaves the feed.</summary>
    TurnaroundComplete = 5,
}

/// <summary>One gate ("porte") of Terminal 2E departures with its current occupancy.</summary>
/// <param name="Number">Gate number, 1..8.</param>
/// <param name="Idle">True when no aircraft is parked (before arrival or after release).</param>
/// <param name="FlightNumber">Hummingbird flight occupying the gate; null while idle.</param>
/// <param name="State">Current turnaround phase of the parked aircraft (Idle when free).</param>
/// <param name="ScheduledDepartureUtc">Push-back instant this turnaround aims at; null while idle.</param>
public record GateSlot(
    int Number,
    bool Idle,
    string? FlightNumber,
    GateState State,
    DateTimeOffset? ScheduledDepartureUtc);

/// <summary>Full state of the gate turnaround feed at a point in (possibly compressed) feed time.</summary>
/// <param name="FeedId">Immutable feed identity, e.g. CDG-T2E-GATES.</param>
/// <param name="Airport">Airport hosting the gates.</param>
/// <param name="GeneratedAtUtc">Real wall clock instant of generation.</param>
/// <param name="FeedTimeUtc">Feed-clock instant used for the state (equals <c>GeneratedAtUtc</c> at speed 1).</param>
/// <param name="Speed">Compression factor the feed was rendered with.</param>
/// <param name="Gates">The eight gates with their occupancy and phase.</param>
public record TurnaroundSnapshot(
    string FeedId,
    Airport Airport,
    DateTimeOffset GeneratedAtUtc,
    DateTimeOffset FeedTimeUtc,
    double Speed,
    IReadOnlyList<GateSlot> Gates);

/// <summary>A change event on the gate turnaround feed. <see cref="GateEvent.Sequence"/> is the
/// feed-clock occurrence time in milliseconds - the same clock (and numbering rules) the
/// baggage claim board uses, so sequence ids stay comparable across simulated feeds.</summary>
/// <param name="Sequence">Feed-clock occurrence time in milliseconds.</param>
/// <param name="OccurredAtUtc">Feed-clock occurrence instant.</param>
/// <param name="Kind">Kind of transition.</param>
/// <param name="Gate">Gate involved (1..8).</param>
/// <param name="FlightNumber">Affected Hummingbird turnaround flight.</param>
/// <param name="State">Phase the flight carries after the transition.</param>
/// <param name="ScheduledDepartureUtc">Push-back instant this turnaround aims at.</param>
public record GateEvent(
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    GateEventKind Kind,
    int Gate,
    string FlightNumber,
    GateState State,
    DateTimeOffset ScheduledDepartureUtc);

/// <summary>
/// Simulated gate turnaround feed ("Gates - Terminal 2E Departures") of Paris CDG for the
/// WebSocket transport. Each of the eight gates runs one Hummingbird turnaround rotation per
/// sixteen feed-minutes, staggered by gate index, with deterministic phases: parking, boarding
/// (+4), final call (+8), push-back (+10), gate release (+12) and completion (+16).
///
/// Like <see cref="BaggageClaimBoard"/>, everything is a pure function of the shared virtual
/// clock (<see cref="SimClock"/>) - no state is stored. Flight numbers are simulated rotations
/// and are deliberately disconnected from the booking schedule of FlightScheduleStore.
/// </summary>
public sealed class TurnaroundFeed
{
    /// <summary>Immutable identity of the feed (single simulated departure pier).</summary>
    public string FeedId => "CDG-T2E-GATES";

    /// <summary>Airport hosting the gates.</summary>
    public Airport Airport { get; } = FlightScheduleStore.Airports["CDG"];

    public const int GateCount = 8;

    /// <summary>Full turnaround cycle length per gate, in feed time.</summary>
    public const int CycleMinutes = 16;

    private static readonly TimeSpan BoardingOpen = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan FinalCallAt = TimeSpan.FromMinutes(8);
    private static readonly TimeSpan PushbackAt = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ReleaseAt = TimeSpan.FromMinutes(12);

    private static readonly (TimeSpan Delay, GateEventKind Kind)[] Transitions =
    [
        (TimeSpan.Zero, GateEventKind.GateOccupied),
        (BoardingOpen, GateEventKind.BoardingOpened),
        (FinalCallAt, GateEventKind.FinalCall),
        (PushbackAt, GateEventKind.PushbackStarted),
        (ReleaseAt, GateEventKind.GateReleased),
        (CycleSpan, GateEventKind.TurnaroundComplete),
    ];

    private static readonly TimeSpan CycleSpan = TimeSpan.FromMinutes(CycleMinutes);
    private const long CycleMs = CycleMinutes * 60 * 1000;

    /// <summary>Gate offsets stagger the rotations evenly: gate 1 turns "on the hour", gate 8
    /// starts 14 feed-minutes after it, so roughly half the gates are busy at any time.</summary>
    private static long GateOffsetMs(int gate) => (gate - 1) * 2 * 60 * 1000;

    /// <summary>Render the full gate board as of the current clock, compressed by <paramref name="speed"/>.</summary>
    public TurnaroundSnapshot GetSnapshot(double speed)
    {
        speed = SimClock.ClampSpeed(speed);
        var feedTime = SimClock.Time(speed);
        var gates = Enumerable.Range(1, GateCount)
            .Select(gate => SlotFor(gate, feedTime))
            .ToList();

        return new TurnaroundSnapshot(FeedId, Airport, DateTimeOffset.UtcNow, feedTime, speed, gates);
    }

    /// <summary>
    /// Change events whose feed-clock occurrence lies in (fromExclusiveMs, toInclusiveMs].
    /// Same replay/clamping rules as <see cref="BaggageClaimBoard.GetEventsBetween"/>: the
    /// lower bound is clamped to the clock origin so invented ids cannot rewind the feed.
    /// </summary>
    public IReadOnlyList<GateEvent> GetEventsBetween(long fromExclusiveMs, long toInclusiveMs, double speed)
    {
        if (toInclusiveMs <= fromExclusiveMs)
        {
            return [];
        }

        speed = SimClock.ClampSpeed(speed);
        var fromExclusive = DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(fromExclusiveMs, SimClock.OriginUnixMs));
        var toInclusive = DateTimeOffset.FromUnixTimeMilliseconds(toInclusiveMs);
        var events = new List<GateEvent>();

        for (var gate = 1; gate <= GateCount; gate++)
        {
            // Any cycle whose event span [start, start+16min) may overlap the window.
            var firstCycle = FloorDiv((fromExclusive - CycleSpan).ToUnixTimeMilliseconds() - GateOffsetMs(gate), CycleMs);
            var lastCycle = FloorDiv(toInclusive.ToUnixTimeMilliseconds() - GateOffsetMs(gate), CycleMs);

            for (var cycle = firstCycle; cycle <= lastCycle; cycle++)
            {
                var start = DateTimeOffset.FromUnixTimeMilliseconds(cycle * CycleMs + GateOffsetMs(gate));
                var flight = FlightFor(gate, cycle);
                var departure = start + PushbackAt;

                foreach (var (delay, kind) in Transitions)
                {
                    var occurred = start + delay;
                    if (occurred > fromExclusive && occurred <= toInclusive)
                    {
                        events.Add(new GateEvent(
                            occurred.ToUnixTimeMilliseconds(),
                            occurred,
                            kind,
                            gate,
                            flight,
                            StateAt(start, occurred),
                            departure));
                    }
                }
            }
        }

        return [.. events.OrderBy(e => e.Sequence).ThenBy(e => e.Gate)];
    }

    // -----------------------------------------------------------------------
    // Deterministic rotation: pure functions of the feed clock.
    // -----------------------------------------------------------------------

    private static GateSlot SlotFor(int gate, DateTimeOffset at)
    {
        var cycle = FloorDiv(at.ToUnixTimeMilliseconds() - GateOffsetMs(gate), CycleMs);
        var start = DateTimeOffset.FromUnixTimeMilliseconds(cycle * CycleMs + GateOffsetMs(gate));
        var elapsed = at - start;
        if (elapsed >= ReleaseAt)
        {
            return new GateSlot(gate, true, null, GateState.Idle, null);
        }

        return new GateSlot(gate, false, FlightFor(gate, cycle), StateAt(start, at), start + PushbackAt);
    }

    private static GateState StateAt(DateTimeOffset cycleStart, DateTimeOffset at)
    {
        var elapsed = at - cycleStart;
        if (elapsed < BoardingOpen)
        {
            return GateState.Occupied;
        }

        if (elapsed < FinalCallAt)
        {
            return GateState.Boarding;
        }

        if (elapsed < PushbackAt)
        {
            return GateState.FinalCall;
        }

        if (elapsed < ReleaseAt)
        {
            return GateState.Pushback;
        }

        return GateState.Released;
    }

    /// <summary>Flight number of one rotation: the eight Hummingbird numbers rotate per cycle,
    /// staggered by gate, so simultaneously occupied gates never show the same flight.</summary>
    private static string FlightFor(int gate, long cycle) =>
        $"HB{900 + (int)((cycle + gate - 1) % GateCount)}";

    private static long FloorDiv(long numerator, long denominator)
    {
        var quotient = numerator / denominator;
        if ((numerator % denominator != 0) && ((numerator < 0) != (denominator < 0)))
        {
            quotient--;
        }

        return quotient;
    }
}
