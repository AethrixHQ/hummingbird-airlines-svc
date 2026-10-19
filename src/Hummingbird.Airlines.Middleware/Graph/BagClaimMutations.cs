using System.Collections.Concurrent;
using HotChocolate;
using Hummingbird.Airlines.Backend.Domain;
using Hummingbird.Airlines.Backend.Services;
using Hummingbird.Airlines.Backend.Storage;
using Hummingbird.Airlines.Middleware.Soap;
using Hummingbird.Airlines.Middleware.Translation;

namespace Hummingbird.Airlines.Middleware.Graph;

/// <summary>Kind of bag in mutation inputs/views (mirrors the REST type discriminators
/// "checked" / "carryOn"). Serialized UPPER_SNAKE by the GraphQL wire convention.</summary>
public enum BagKind
{
    /// <summary>Hold luggage with dimensions and fragility (aviation: checked).</summary>
    Checked,

    /// <summary>Cabin luggage (aviation: carry-on).</summary>
    CarryOn,
}

/// <summary>Make-up room scan disposition - the GraphQL face of the gRPC <c>Disposition</c>
/// enum (same five values, same evaluation order, see <see cref="BagScanRules"/>).</summary>
public enum BagScanDisposition
{
    /// <summary>Bag filed to a make-up belt.</summary>
    Accepted,

    /// <summary>The tag was already accepted in this session.</summary>
    Duplicate,

    /// <summary>The tag does not match the IATA barcode shape.</summary>
    Malformed,

    /// <summary>The make-up zone is outside MU1..MU3.</summary>
    UnknownZone,

    /// <summary>The session already accepted 8 bags.</summary>
    LimitExceeded,
}

/// <summary>Polymorphic bag input. Flat on purpose - the same shape as the REST JSON body with
/// its type discriminator. Fields of the other kind are ignored (checked: length/width/height/
/// fragile; carry-on: hasLaptop/fitsUnderSeat).</summary>
public sealed class BagInput
{
    /// <summary>Checked (hold) or carry-on (cabin) luggage.</summary>
    public BagKind Kind { get; set; }

    /// <summary>Weight in kilograms; 0 &lt; kg &lt;= 100. In-allowance overweight keeps the bag and produces a warning instead of an error.</summary>
    public double WeightKg { get; set; }

    /// <summary>Free-text colour used to identify the bag at the belt (omit for empty).</summary>
    public string? Color { get; set; }

    /// <summary>Optional bag tag id; the system assigns HB-serial tags when omitted.</summary>
    public string? TagId { get; set; }

    /// <summary>Checked bags only: length in centimetres.</summary>
    public int? LengthCm { get; set; }

    /// <summary>Checked bags only: width in centimetres.</summary>
    public int? WidthCm { get; set; }

    /// <summary>Checked bags only: height in centimetres.</summary>
    public int? HeightCm { get; set; }

    /// <summary>Checked bags only: contains fragile items.</summary>
    public bool? Fragile { get; set; }

    /// <summary>Carry-on bags only: carries a laptop / large electronic device.</summary>
    public bool? HasLaptop { get; set; }

    /// <summary>Carry-on bags only: fits under the seat in front.</summary>
    public bool? FitsUnderSeat { get; set; }
}

/// <summary>Input of <c>checkIn</c>: flight registration with an optional bag list that is
/// registered atomically with the boarding pass.</summary>
public sealed class CheckInInput
{
    /// <summary>Six-character booking reference (case-insensitive), e.g. PRS205.</summary>
    public string BookingRef { get; set; } = string.Empty;

    /// <summary>Bags to register together with the check-in (optional).</summary>
    public List<BagInput>? Bags { get; set; }
}

/// <summary>Input of <c>dropBag</c>: exactly one bag at the bag drop (the booking must be checked in already).</summary>
public sealed class DropBagInput
{
    /// <summary>Six-character booking reference (case-insensitive).</summary>
    public string BookingRef { get; set; } = string.Empty;

    /// <summary>The bag to register.</summary>
    public BagInput Bag { get; set; } = new();
}

/// <summary>Input of <c>scanBag</c>: one scan at the make-up room scanner cart. Sessions are
/// GraphQL-side only (isolated from the gRPC scanner sessions).</summary>
public sealed class ScanBagInput
{
    /// <summary>Client-chosen session id. Same id = same session state (duplicates + 8-bag limit); sessions expire after 30 minutes of inactivity.</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Client-side scan identifier; echoed unchanged on the ack.</summary>
    public string ScanId { get; set; } = string.Empty;

    /// <summary>Bag tag (IATA barcode shape: 2 letters + 6 digits, case-insensitive).</summary>
    public string TagNumber { get; set; } = string.Empty;

    /// <summary>Make-up zone MU1 / MU2 / MU3; omit (or empty) means MU1.</summary>
    public string? Zone { get; set; }
}

/// <summary>A bag as stored on the booking. Common fields plus the kind-specific ones of the
/// matching bag kind (the other kind's fields are null).</summary>
public sealed class BagView
{
    /// <summary>Bag tag id (assigned by the system when the input omitted it).</summary>
    public string TagId { get; set; } = string.Empty;

    /// <summary>Checked or carry-on.</summary>
    public BagKind Kind { get; set; }

    /// <summary>Weight in kilograms as accepted.</summary>
    public double WeightKg { get; set; }

    /// <summary>Free-text colour.</summary>
    public string Color { get; set; } = string.Empty;

    /// <summary>Checked bags only.</summary>
    public int? LengthCm { get; set; }

    /// <summary>Checked bags only.</summary>
    public int? WidthCm { get; set; }

    /// <summary>Checked bags only.</summary>
    public int? HeightCm { get; set; }

    /// <summary>Checked bags only.</summary>
    public bool? Fragile { get; set; }

    /// <summary>Carry-on bags only.</summary>
    public bool? HasLaptop { get; set; }

    /// <summary>Carry-on bags only.</summary>
    public bool? FitsUnderSeat { get; set; }

    public static BagView From(Baggage bag) => new()
    {
        TagId = bag.TagId,
        Kind = bag is CheckedBaggage ? BagKind.Checked : BagKind.CarryOn,
        WeightKg = bag.WeightKg,
        Color = bag.Color,
        LengthCm = (bag as CheckedBaggage)?.LengthCm,
        WidthCm = (bag as CheckedBaggage)?.WidthCm,
        HeightCm = (bag as CheckedBaggage)?.HeightCm,
        Fragile = (bag as CheckedBaggage)?.Fragile,
        HasLaptop = (bag as CarryOnBaggage)?.HasLaptop,
        FitsUnderSeat = (bag as CarryOnBaggage)?.FitsUnderSeat,
    };
}

/// <summary>Boarding pass issued by a successful check-in (GraphQL face of the REST
/// CheckInResponse minus the flight designator expansion).</summary>
public sealed class CheckInPayload
{
    /// <summary>Booking reference (echo).</summary>
    public string BookingRef { get; set; } = string.Empty;

    /// <summary>Flight number checked in for.</summary>
    public string FlightNumber { get; set; } = string.Empty;

    /// <summary>Full passenger name on the boarding pass.</summary>
    public string PassengerName { get; set; } = string.Empty;

    /// <summary>Assigned seat (row depends on cabin class).</summary>
    public string Seat { get; set; } = string.Empty;

    /// <summary>Departure gate at the origin airport.</summary>
    public string Gate { get; set; } = string.Empty;

    /// <summary>Scheduled departure instant (UTC).</summary>
    public DateTime ScheduledDepartureUtc { get; set; }

    /// <summary>Boarding start instant (UTC), 40 minutes before departure.</summary>
    public DateTime BoardingTimeUtc { get; set; }

    /// <summary>Boarding sequence number.</summary>
    public int BoardingSequence { get; set; }

    /// <summary>Bags accepted together with the check-in (overweight reported as warnings, not errors).</summary>
    public IReadOnlyList<BagView> Bags { get; set; } = [];

    /// <summary>Structured warnings of the accepted bags.</summary>
    public IReadOnlyList<Warning> Warnings { get; set; } = [];

    public static CheckInPayload From(CheckInResult result) => new()
    {
        BookingRef = result.BookingRef,
        FlightNumber = result.FlightNumber,
        PassengerName = result.PassengerName,
        Seat = result.Seat,
        Gate = result.Gate,
        ScheduledDepartureUtc = result.ScheduledDepartureUtc,
        BoardingTimeUtc = result.BoardingTimeUtc,
        BoardingSequence = result.BoardingSequence,
        Bags = result.Bags.Select(BagView.From).ToList(),
        Warnings = WarningTranslator.Translate(result.Warnings),
    };
}

/// <summary>Reply of a single bag drop: the full bag list of the booking plus warnings.</summary>
public sealed class DropBagPayload
{
    /// <summary>Booking reference (echo).</summary>
    public string BookingRef { get; set; } = string.Empty;

    /// <summary>Always true - business rejections surface as GraphQL errors with codes, never in the payload.</summary>
    public bool Success { get; set; }

    /// <summary>All bags now on the booking, newest last.</summary>
    public IReadOnlyList<BagView> Bags { get; set; } = [];

    /// <summary>Structured warnings (e.g. overweight within the acceptance window).</summary>
    public IReadOnlyList<Warning> Warnings { get; set; } = [];

    public static DropBagPayload From(BaggageRegistrationReply reply) => new()
    {
        BookingRef = reply.BookingRef,
        Success = reply.Success,
        Bags = reply.Bags.Select(BagView.From).ToList(),
        Warnings = WarningTranslator.Translate(reply.Warnings),
    };
}

/// <summary>One scanner-cart scan answer (GraphQL face of the gRPC <c>ScanAck</c>).</summary>
public sealed class BagScanAck
{
    /// <summary>Client scan id, echoed unchanged.</summary>
    public string ScanId { get; set; } = string.Empty;

    /// <summary>The scanned tag exactly as it was sent.</summary>
    public string TagNumber { get; set; } = string.Empty;

    /// <summary>Disposition evaluated in the shared rule order (limit > shape > zone > duplicate > accept).</summary>
    public BagScanDisposition Disposition { get; set; }

    /// <summary>Scanner-display message (same wording as the gRPC stream).</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Make-up belt 1..5; 0 when the bag was not accepted (proto parity).</summary>
    public int Belt { get; set; }

    /// <summary>Accepted scans in this session so far.</summary>
    public int AcceptedCount { get; set; }

    /// <summary>Rejected scans in this session so far.</summary>
    public int RejectedCount { get; set; }
}

/// <summary>Reply of <c>resetScanSession</c>: idempotent session clear.</summary>
public sealed class ResetScanSessionPayload
{
    /// <summary>The session id that was cleared.</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>True when an existing session was discarded; false when there was nothing to clear.</summary>
    public bool Removed { get; set; }
}

/// <summary>GraphQL mutations over the legacy path and the scanner rule engine:
/// <list type="bullet">
///   <item><c>checkIn</c> and <c>dropBag</c> run through the SAME SoapGateway round-trips as
///     REST /api/v1 and the SOAP luggage endpoint, so verdicts and error codes have one source
///     of truth (BOOKING_NOT_FOUND, CHECKIN_CLOSED, CHECKIN_REQUIRED, ALREADY_CHECKED_IN,
///     BAGGAGE_TYPE_LIMIT, FLIGHT_DEPARTED, INVALID_REQUEST, ...).</item>
///   <item><c>scanBag</c>/<c>resetScanSession</c> evaluate the shared <see cref="BagScanRules"/>
///     engine against a GraphQL-owned session store - protocol parity for the gRPC scanner.</item>
/// </list>
/// Aethrix Studio finds the root under SDL name <c>type Mutation</c>.
/// </summary>
public sealed class Mutation
{
    /// <summary>Check in a booking and optionally register bags atomically (boarding pass path).</summary>
    public CheckInPayload CheckIn(CheckInInput input, [Service] SoapGateway gateway) =>
        LegacyGuard.Guard(() =>
            CheckInPayload.From(gateway.CheckIn(
                input.BookingRef,
                (input.Bags ?? []).Select(BagMapper.ToDomain).ToList())));

    /// <summary>Register exactly one bag at the bag drop (requires prior check-in).</summary>
    public DropBagPayload DropBag(DropBagInput input, [Service] SoapGateway gateway) =>
        LegacyGuard.Guard(() =>
            DropBagPayload.From(gateway.RegisterBaggage(input.BookingRef, BagMapper.ToDomain(input.Bag))));

    /// <summary>One make-up room scan; dispositions are data, not errors (identical rules to gRPC ScanBags).</summary>
    public BagScanAck ScanBag(ScanBagInput input, [Service] BagScanSessionStore sessions)
    {
        if (string.IsNullOrWhiteSpace(input.SessionId))
        {
            throw new GraphQLException(
                ErrorBuilder.New()
                    .SetMessage("A non-empty sessionId is required (sessions are client-owned).")
                    .SetCode("INVALID_REQUEST")
                    .Build());
        }

        return sessions.EvaluateAndRecord(input.SessionId, input.ScanId, input.TagNumber, input.Zone);
    }

    /// <summary>Discard a scanner session (idempotent; unknown ids answer removed=false).</summary>
    public ResetScanSessionPayload ResetScanSession(string sessionId, [Service] BagScanSessionStore sessions) =>
        sessions.Reset(sessionId ?? string.Empty);
}

/// <summary>Maps a <see cref="BagInput"/> (REST-shape flat discriminated bag) to the polymorphic
/// backend domain object the SOAP legacy systems expect.</summary>
internal static class BagMapper
{
    public static Baggage ToDomain(BagInput input)
    {
        if (input.Kind == BagKind.Checked)
        {
            var bag = new CheckedBaggage { WeightKg = input.WeightKg, Color = input.Color ?? string.Empty };
            if (input.TagId is not null) bag.TagId = input.TagId;
            if (input.LengthCm is { } length) bag.LengthCm = length;
            if (input.WidthCm is { } width) bag.WidthCm = width;
            if (input.HeightCm is { } height) bag.HeightCm = height;
            if (input.Fragile is { } fragile) bag.Fragile = fragile;
            return bag;
        }

        var carryOn = new CarryOnBaggage { WeightKg = input.WeightKg, Color = input.Color ?? string.Empty };
        if (input.TagId is not null) carryOn.TagId = input.TagId;
        if (input.HasLaptop is { } laptop) carryOn.HasLaptop = laptop;
        if (input.FitsUnderSeat is { } fits) carryOn.FitsUnderSeat = fits;
        return carryOn;
    }
}

/// <summary>
/// GraphQL face of the REST/SOAP failure vocabulary: legacy faults become GraphQL errors with
/// the SAME stable code (extensions.code) the ProblemDetails middleware writes, gateway timeout
/// becomes LEGACY_TIMEOUT, unreachable legacy becomes LEGACY_UNAVAILABLE.
/// </summary>
internal static class LegacyGuard
{
    public static T Guard<T>(Func<T> operation)
    {
        try
        {
            return operation();
        }
        catch (LegacyFaultException fault)
        {
            throw GraphError(fault.Code, fault.Message);
        }
        catch (LegacyTimeoutException timeout)
        {
            throw GraphError("LEGACY_TIMEOUT", timeout.Message);
        }
        catch (LegacyUnavailableException unavailable)
        {
            throw GraphError("LEGACY_UNAVAILABLE", unavailable.Message);
        }
    }

    private static GraphQLException GraphError(string code, string message) => new(
        ErrorBuilder.New()
            .SetMessage(message)
            .SetCode(code)
            .Build());
}

/// <summary>
/// In-memory scanner sessions for the GraphQL <c>scanBag</c> mutation. Each sessionId owns a
/// private accepted-tag set and counters - the same per-session isolation as the gRPC stream,
/// but bound to an explicit client id instead of the connection lifetime. Sessions expire after
/// 30 minutes of inactivity; the store stays bounded by evicting the least recently touched
/// session when saturated.
/// </summary>
public sealed class BagScanSessionStore
{
    private sealed class Session(string id)
    {
        public string Id { get; } = id;
        public HashSet<string> Accepted { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int AcceptedCount { get; private set; }
        public int RejectedCount { get; private set; }
        public DateTime LastAccess { get; private set; } = DateTime.UtcNow;

        public void CountAccept() => AcceptedCount++;
        public void CountReject() => RejectedCount++;
        public void Touch() => LastAccess = DateTime.UtcNow;
    }

    private const int MaxSessions = 512;
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);

    public BagScanAck EvaluateAndRecord(string sessionId, string scanId, string tagNumber, string zone)
    {
        PruneExpired();
        var session = _sessions.GetOrAdd(sessionId, static id => new Session(id));

        lock (session)
        {
            var (disposition, message, belt, normalizedTag) =
                BagScanRules.Evaluate(tagNumber, zone, session.Accepted, session.AcceptedCount);

            if (disposition == ScanDisposition.Accepted)
            {
                session.CountAccept();
                session.Accepted.Add(normalizedTag);
            }
            else
            {
                session.CountReject();
            }

            session.Touch();

            return new BagScanAck
            {
                ScanId = scanId,
                TagNumber = tagNumber,
                Disposition = Map(disposition),
                Message = message,
                Belt = belt,
                AcceptedCount = session.AcceptedCount,
                RejectedCount = session.RejectedCount,
            };
        }
    }

    public ResetScanSessionPayload Reset(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var session)
            && _sessions.TryRemove(new KeyValuePair<string, Session>(sessionId, session))
            ? new ResetScanSessionPayload { SessionId = sessionId, Removed = true }
            : new ResetScanSessionPayload { SessionId = sessionId, Removed = false };

    private static BagScanDisposition Map(ScanDisposition disposition) => disposition switch
    {
        ScanDisposition.Accepted => BagScanDisposition.Accepted,
        ScanDisposition.Duplicate => BagScanDisposition.Duplicate,
        ScanDisposition.Malformed => BagScanDisposition.Malformed,
        ScanDisposition.UnknownZone => BagScanDisposition.UnknownZone,
        ScanDisposition.LimitExceeded => BagScanDisposition.LimitExceeded,
        _ => throw new InvalidOperationException($"Unknown scan disposition '{disposition}'."),
    };

    private void PruneExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var (key, session) in _sessions)
        {
            if (now - session.LastAccess > Ttl)
            {
                _sessions.TryRemove(new KeyValuePair<string, Session>(key, session));
            }
        }

        // Keep the store bounded even before sessions expire: evict the least recently
        // touched sessions while saturated so an abusive caller cannot grow it unboundedly.
        while (_sessions.Count >= MaxSessions)
        {
            Session? oldest = null;
            foreach (var (_, session) in _sessions)
            {
                if (oldest is null || session.LastAccess < oldest.LastAccess)
                {
                    oldest = session;
                }
            }

            if (oldest is null || !_sessions.TryRemove(new KeyValuePair<string, Session>(oldest.Id, oldest)))
            {
                break;
            }
        }
    }
}
