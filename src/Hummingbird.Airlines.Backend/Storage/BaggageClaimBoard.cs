using System.Text;
using Hummingbird.Airlines.Backend.Domain;

namespace Hummingbird.Airlines.Backend.Storage;

/// <summary>Operational state of an arrival on the baggage claim board.</summary>
public enum BagClaimFlightState
{
    /// <summary>Aircraft on approach; not on the carousel yet.</summary>
    Inbound = 0,

    /// <summary>Aircraft arrived, belt assigned, waiting for the first bag.</summary>
    Arrived = 1,

    /// <summary>Bags are flowing on the belt.</summary>
    Unloading = 2,

    /// <summary>Last bag has been delivered.</summary>
    LastBag = 3,

    /// <summary>Belt released by the handling agent; the flight briefly stays on the board.</summary>
    Released = 4,
}

/// <summary>Kinds of change events emitted by the baggage claim board.</summary>
public enum BagClaimEventKind
{
    /// <summary>A flight entered the inbound window of the board.</summary>
    FlightInbound = 0,

    /// <summary>Aircraft arrived and its belt was assigned.</summary>
    FlightArrived = 1,

    /// <summary>First bags started flowing on the belt.</summary>
    UnloadingStarted = 2,

    /// <summary>Last bag delivered.</summary>
    LastBag = 3,

    /// <summary>Belt released and available again.</summary>
    BeltReleased = 4,

    /// <summary>Flight left the board.</summary>
    FlightOffBoard = 5,
}

/// <summary>One of the baggage claim belts ("tapis" 1..5) of the arrivals hall.</summary>
public record BagClaimBelt(int Number, bool Idle, string? FlightNumber);

/// <summary>An arrival flight displayed on the baggage claim board.</summary>
/// <param name="FlightNumber">Flight number as shown on the board, e.g. AF412.</param>
/// <param name="Airline">Operating carrier (IATA-style code enum).</param>
/// <param name="AirlineName">Full airline name, e.g. Air France.</param>
/// <param name="OriginCode">IATA code of the departure airport, e.g. JFK.</param>
/// <param name="OriginName">Full origin airport name.</param>
/// <param name="OriginCity">Origin city, e.g. New York.</param>
/// <param name="ScheduledArrivalUtc">Board-clock scheduled arrival instant.</param>
/// <param name="Belt">Assigned belt (1..5); 0 while the flight is still inbound.</param>
/// <param name="State">Operational state of the arrival.</param>
public record BagClaimArrival(
    string FlightNumber,
    AirlineCode Airline,
    string AirlineName,
    string OriginCode,
    string OriginName,
    string OriginCity,
    DateTimeOffset ScheduledArrivalUtc,
    int Belt,
    BagClaimFlightState State);

/// <summary>Full state of the baggage claim board at a point in (possibly compressed) board time.</summary>
/// <param name="BoardId">Immutable board identity, e.g. CDG-T2E.</param>
/// <param name="Airport">Airport hosting the baggage claim belts.</param>
/// <param name="GeneratedAtUtc">Real wall clock instant of generation.</param>
/// <param name="BoardTimeUtc">Board-clock instant used for the state (equals <c>GeneratedAtUtc</c> at speed 1).</param>
/// <param name="Speed">Compression factor the board was rendered with.</param>
/// <param name="Belts">The five belts ("tapis") with their occupancy.</param>
/// <param name="Arrivals">Every arrival currently displayed, ordered by scheduled arrival.</param>
public record BagClaimSnapshot(
    string BoardId,
    Airport Airport,
    DateTimeOffset GeneratedAtUtc,
    DateTimeOffset BoardTimeUtc,
    double Speed,
    IReadOnlyList<BagClaimBelt> Belts,
    IReadOnlyList<BagClaimArrival> Arrivals);

/// <summary>A change event on the baggage claim board. <see cref="BagClaimEvent.Sequence"/> is the board-clock
/// occurrence time in milliseconds and doubles as the SSE event id, so the SSE and gRPC
/// transports share the exact same event numbering.</summary>
/// <param name="Sequence">Board-clock occurrence time in milliseconds (SSE event id).</param>
/// <param name="OccurredAtUtc">Board-clock occurrence instant.</param>
/// <param name="Kind">Kind of transition.</param>
/// <param name="FlightNumber">Affected flight.</param>
/// <param name="Belt">Belt involved; 0 for inbound / off-board events where no belt is involved.</param>
/// <param name="State">State the flight carries after the transition.</param>
public record BagClaimEvent(
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    BagClaimEventKind Kind,
    string FlightNumber,
    int Belt,
    BagClaimFlightState State);

/// <summary>
/// Simulated baggage claim board ("Arrivals - Terminal 2E") of Paris CDG, shared by the
/// REST/SSE and gRPC transports.
///
/// Everything is a PURE FUNCTION of the board clock - no state is stored, so the board
/// never needs a reset and any number of endpoints render identical snapshots. One
/// partner-carrier arrival lands every six board-minutes; participants are the transport
/// feed of arrivals into CDG. It is deliberately disconnected from the internal Hummingbird
/// booking schedule ("HB..." flights in FlightScheduleStore) - these are handling-agent
/// partner feed arrivals.
///
/// The board clock can be compressed with a speed factor: board time = real UTC x speed.
/// Speed 1 (the default everywhere) is plain real time; speed 30 makes belt transitions
/// observable within seconds - handy when a test tool expects an event stream to move.
/// </summary>
public sealed class BaggageClaimBoard
{
    /// <summary>Immutable identity of the board (single simulated hall).</summary>
    public string BoardId => "CDG-T2E";

    /// <summary>Airport hosting the belts.</summary>
    public Airport Airport { get; } = FlightScheduleStore.Airports["CDG"];

    /// <summary>Minimum allowed time compression factor (see <see cref="SimClock"/>).</summary>
    public const double MinSpeed = SimClock.MinSpeed;

    /// <summary>Maximum allowed time compression factor (see <see cref="SimClock"/>).</summary>
    public const double MaxSpeed = SimClock.MaxSpeed;

    private static readonly (AirlineCode Airline, string Name, string OriginCode)[] Feed =
    [
        (AirlineCode.Hb, "Hummingbird Airlines", "SIN"),
        (AirlineCode.Af, "Air France", "JFK"),
        (AirlineCode.Ba, "British Airways", "LHR"),
        (AirlineCode.Ca, "Air China", "PEK"),
        (AirlineCode.Lh, "Lufthansa", "FRA"),
        (AirlineCode.Ek, "Emirates", "DXB"),
    ];

    public const int BeltCount = 5;

    /// <summary>One fresh arrival every six board-minutes.</summary>
    private static readonly TimeSpan Cadence = TimeSpan.FromMinutes(6);
    /// <summary>A flight shows up on the board this long before landing.</summary>
    private static readonly TimeSpan InboundLead = TimeSpan.FromMinutes(12);
    /// <summary>Bags start flowing this long after landing.</summary>
    private static readonly TimeSpan UnloadingDelay = TimeSpan.FromMinutes(2);
    /// <summary>Last bag lands on the belt this long after landing.</summary>
    private static readonly TimeSpan LastBagDelay = TimeSpan.FromMinutes(12);
    /// <summary>Handling agent releases the belt this long after landing.</summary>
    private static readonly TimeSpan ReleaseDelay = TimeSpan.FromMinutes(14);
    /// <summary>The flight disappears from the board this long after landing.</summary>
    private static readonly TimeSpan OffBoardDelay = TimeSpan.FromMinutes(18);

    private static readonly (TimeSpan Delay, BagClaimEventKind Kind)[] Transitions =
    [
        (-InboundLead, BagClaimEventKind.FlightInbound),
        (TimeSpan.Zero, BagClaimEventKind.FlightArrived),
        (UnloadingDelay, BagClaimEventKind.UnloadingStarted),
        (LastBagDelay, BagClaimEventKind.LastBag),
        (ReleaseDelay, BagClaimEventKind.BeltReleased),
        (OffBoardDelay, BagClaimEventKind.FlightOffBoard),
    ];

    private static readonly long CadenceSeconds = (long)Cadence.TotalSeconds;

    /// <summary>Normalize a speed hint (misses/out-of-range are clamped; must not divide by zero).</summary>
    public static double ClampSpeed(double? speed) => SimClock.ClampSpeed(speed);

    /// <summary>Board time = process start + real elapsed UTC x speed. Deterministic and
    /// monotonic in speed; speed 1 is exactly real time; compressed speeds start "now".</summary>
    public static DateTimeOffset BoardTime(double speed) => SimClock.Time(speed);

    /// <summary>Render the full board as of the current clock, compressed by <paramref name="speed"/>.</summary>
    public BagClaimSnapshot GetSnapshot(double speed)
    {
        speed = ClampSpeed(speed);
        var boardTime = BoardTime(speed);
        var window = FlightsOnBoard(boardTime);
        var landed = window
            .Where(f => f.Landing <= boardTime)
            .OrderBy(f => f.Landing).ThenBy(f => f.FlightNumber, StringComparer.Ordinal)
            .ToList();

        // Belt assignment via the shared deterministic resolver (BeltForAt): the board
        // view, the single-flight lookup and the event stream all agree by construction,
        // and belts stay faithful to physical occupancy even for flights that left the
        // display but still hold their belt (release runs 4 board-minutes off-board).
        var beltByTick = new Dictionary<long, int>();
        foreach (var flight in landed)
        {
            beltByTick[flight.Tick] = BeltForAt(flight.Landing, flight.FlightNumber);
        }

        var arrivals = window
            .Select(f => ToArrival(
                f,
                f.Landing <= boardTime && beltByTick.TryGetValue(f.Tick, out var assigned) ? assigned : 0,
                boardTime))
            .OrderBy(a => a.ScheduledArrivalUtc)
            .ToList();

        var occupied = new Dictionary<int, string>();
        foreach (var flight in landed)
        {
            if (boardTime - flight.Landing < ReleaseDelay)
            {
                occupied[beltByTick[flight.Tick]] = flight.FlightNumber;
            }
        }

        var beltViews = Enumerable.Range(1, BeltCount)
            .Select(n => occupied.TryGetValue(n, out var flightNumber)
                ? new BagClaimBelt(n, false, flightNumber)
                : new BagClaimBelt(n, true, null))
            .ToList();

        return new BagClaimSnapshot(
            BoardId,
            Airport,
            DateTimeOffset.UtcNow,
            boardTime,
            speed,
            beltViews,
            arrivals);
    }

    /// <summary>Find one arrival by flight number (case-insensitive), or null when absent or off-board.</summary>
    public BagClaimArrival? TryGetArrival(string flightNumber, double speed)
    {
        speed = ClampSpeed(speed);
        var boardTime = BoardTime(speed);
        var window = FlightsOnBoard(boardTime);
        var match = window.FirstOrDefault(f =>
            string.Equals(f.FlightNumber, flightNumber, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return null;
        }

        var belt = match.Landing <= boardTime ? BeltForAt(match.Landing, match.FlightNumber) : 0;
        return ToArrival(match, belt, boardTime);
    }

    /// <summary>
    /// Change events whose board-clock occurrence lies in (fromExclusiveMs, toInclusiveMs].
    /// Replaying (resumeMs, boardNowMs] reconstructs everything that happened since then,
    /// which is how the SSE transport implements Last-Event-ID resumption.
    /// The lower bound is clamped to the board-clock origin: no event existed before
    /// process start, so stale or invented ids cannot rewind the feed into infinity.
    /// </summary>
    public IReadOnlyList<BagClaimEvent> GetEventsBetween(long fromExclusiveMs, long toInclusiveMs, double speed)
    {
        if (toInclusiveMs <= fromExclusiveMs)
        {
            return [];
        }

        speed = ClampSpeed(speed);
        var fromExclusive = DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(fromExclusiveMs, SimClock.OriginUnixMs));
        var toInclusive = DateTimeOffset.FromUnixTimeMilliseconds(toInclusiveMs);

        // Any flight whose lifecycle overlaps the requested interval must be considered.
        var firstTick = TickIndexForLanding(fromExclusive - OffBoardDelay);
        var lastTick = TickIndexForLanding(toInclusive + InboundLead);
        var events = new List<BagClaimEvent>();

        for (var tick = firstTick; tick <= lastTick; tick++)
        {
            var flight = FlightForTick(tick);
            var landing = LandingForTick(tick);

            foreach (var (delay, kind) in Transitions)
            {
                var occurred = landing + delay;
                if (occurred > fromExclusive && occurred <= toInclusive)
                {
                    var belt = kind is BagClaimEventKind.FlightInbound or BagClaimEventKind.FlightOffBoard
                        ? 0
                        : BeltForAt(landing, flight.FlightNumber);
                    events.Add(new BagClaimEvent(
                        occurred.ToUnixTimeMilliseconds(),
                        occurred,
                        kind,
                        flight.FlightNumber,
                        belt,
                        StateAt(landing, occurred)));
                }
            }
        }

        return [.. events.OrderBy(e => e.Sequence)];
    }

    // -----------------------------------------------------------------------
    // Deterministic feed: pure functions of the board clock.
    // -----------------------------------------------------------------------

    private record FeedFlight(long Tick, string FlightNumber, AirlineCode Airline, string AirlineName,
        string OriginCode, string OriginName, string OriginCity, DateTimeOffset Landing);

    private static long TickIndexForLanding(DateTimeOffset time) =>
        time.ToUnixTimeMilliseconds() / (CadenceSeconds * 1000);

    private static DateTimeOffset LandingForTick(long tick) =>
        DateTimeOffset.FromUnixTimeMilliseconds(tick * CadenceSeconds * 1000);

    private static FeedFlight FlightForTick(long tick)
    {
        var (airline, name, originCode) = Feed[(int)Math.Abs(tick % Feed.Length)];
        var origin = FlightScheduleStore.Airports[originCode];
        var numeration = 100 + (int)Math.Abs((tick * 37 + (int)airline * 11) % 880);
        return new FeedFlight(
            tick,
            $"{AirlineCodeToString(airline)}{numeration}",
            airline,
            name,
            origin.Code,
            origin.Name,
            origin.City,
            LandingForTick(tick));
    }

    /// <summary>Flights currently visible on the board (inbound through released). Ordered by landing time.</summary>
    private static FeedFlight[] FlightsOnBoard(DateTimeOffset boardTime)
    {
        // Landing window: [boardTime - OffBoardDelay + epsilon, boardTime + InboundLead].
        var firstTick = TickIndexForLanding(boardTime - OffBoardDelay + TimeSpan.FromSeconds(1));
        var lastTick = TickIndexForLanding(boardTime + InboundLead);
        return Enumerable.Range(0, (int)(lastTick - firstTick) + 1)
            .Select(offset => firstTick + offset)
            .Select(FlightForTick)
            .Where(f => boardTime >= f.Landing - InboundLead && boardTime < f.Landing + OffBoardDelay)
            .OrderBy(f => f.Landing).ThenBy(f => f.FlightNumber, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Belt a flight holds as of its landing instant. Deterministic: walks circularly
    /// from a stable hash of the flight number to the first belt not occupied by any earlier
    /// (already-assigned) flight with an overlapping occupancy window.</summary>
    private static int BeltForAt(DateTimeOffset landing, string flightNumber)
    {
        // Neighbourhood: every flight whose occupancy could overlap [landing, landing+ReleaseDelay).
        const int neighbourhood = 3;
        var ownTick = TickIndexForLanding(landing);
        var neighbours = Enumerable.Range(-neighbourhood, neighbourhood * 2 + 1)
            .Select(offset => FlightForTick(ownTick + offset))
            .Where(f => f.Landing <= landing)
            .OrderBy(f => f.Landing).ThenBy(f => f.FlightNumber, StringComparer.Ordinal)
            .ToList();

        var belts = new int[neighbours.Count];
        for (var i = 0; i < neighbours.Count; i++)
        {
            belts[i] = AssignBelt(neighbours, belts, i);
        }

        var ownIndex = neighbours.FindIndex(f => f.FlightNumber.Equals(flightNumber, StringComparison.OrdinalIgnoreCase));
        return ownIndex >= 0 ? belts[ownIndex] : (int)(StableHash(flightNumber) % BeltCount) + 1;
    }

    private static int AssignBelt(List<FeedFlight> ordered, int[] assignedBelts, int index)
    {
        var flight = ordered[index];
        var preferred = (int)(StableHash(flight.FlightNumber) % BeltCount) + 1;

        for (var probe = 0; probe < BeltCount; probe++)
        {
            var candidate = ((preferred - 1 + probe) % BeltCount) + 1;
            var free = true;
            for (var j = 0; j < index; j++)
            {
                var other = ordered[j];
                // Belt occupancy window: [landing, landing + ReleaseDelay).
                if (assignedBelts[j] == candidate
                    && other.Landing < flight.Landing + ReleaseDelay
                    && flight.Landing < other.Landing + ReleaseDelay)
                {
                    free = false;
                    break;
                }
            }

            if (free)
            {
                return candidate;
            }
        }

        // Recommended belt sharing (never expected at a 6-minute cadence): fall back to the
        // preferred belt and let the two flights unload together.
        return preferred;
    }

    private static BagClaimFlightState StateAt(DateTimeOffset landing, DateTimeOffset at)
    {
        var elapsed = at - landing;
        if (elapsed < TimeSpan.Zero)
        {
            return BagClaimFlightState.Inbound;
        }

        if (elapsed < UnloadingDelay)
        {
            return BagClaimFlightState.Arrived;
        }

        if (elapsed < LastBagDelay)
        {
            return BagClaimFlightState.Unloading;
        }

        if (elapsed < ReleaseDelay)
        {
            return BagClaimFlightState.LastBag;
        }

        return BagClaimFlightState.Released;
    }

    private static BagClaimArrival ToArrival(FeedFlight flight, int belt, DateTimeOffset boardTime)
    {
        return new BagClaimArrival(
            flight.FlightNumber,
            flight.Airline,
            flight.AirlineName,
            flight.OriginCode,
            flight.OriginName,
            flight.OriginCity,
            flight.Landing,
            belt,
            StateAt(flight.Landing, boardTime));
    }

    private static string AirlineCodeToString(AirlineCode code) => code switch
    {
        AirlineCode.Hb => "HB",
        AirlineCode.Af => "AF",
        AirlineCode.Ba => "BA",
        AirlineCode.Ca => "CA",
        AirlineCode.Lh => "LH",
        AirlineCode.Ek => "EK",
        _ => ((int)code).ToString(),
    };

    private static uint StableHash(string value)
    {
        // FNV-1a 32 bit: stable across processes and runs, unlike string.GetHashCode().
        var hash = 2166136261u;
        foreach (var raw in Encoding.UTF8.GetBytes(value))
        {
            hash ^= raw;
            hash *= 16777619u;
        }

        return hash;
    }
}
