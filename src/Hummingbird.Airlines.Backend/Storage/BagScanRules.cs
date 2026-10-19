using System.Text.RegularExpressions;

namespace Hummingbird.Airlines.Backend.Storage;

/// <summary>Outcome of one make-up room bag scan. Every transport surface (gRPC <c>Disposition</c>,
/// GraphQL <c>BagScanDisposition</c>) mirrors these members.</summary>
public enum ScanDisposition
{
    /// <summary>Bag filed to a make-up belt.</summary>
    Accepted,

    /// <summary>The tag was already accepted in this session.</summary>
    Duplicate,

    /// <summary>The tag does not match the IATA barcode shape.</summary>
    Malformed,

    /// <summary>The make-up zone is outside MU1..MU3.</summary>
    UnknownZone,

    /// <summary>The session already accepted the maximum number of bags.</summary>
    LimitExceeded,
}

/// <summary>
/// Shared rule engine of the make-up room bag scanner (single source of truth for the
/// gRPC <c>BaggageScanner/ScanBags</c> stream and the GraphQL <c>scanBag</c> mutation).
/// One deterministic evaluation per scan; rules run in this fixed order so a tag is
/// never in two buckets:
///   1. LIMIT_EXCEEDED - the session already accepted <see cref="MaxAcceptedPerSession"/> bags,
///   2. MALFORMED      - the tag fails the IATA barcode shape (case-insensitive),
///   3. UNKNOWN_ZONE   - the make-up zone is outside MU1..MU3 (empty means MU1),
///   4. DUPLICATE      - the tag was already accepted within this session (case-insensitive),
///   5. ACCEPTED       - the bag files to belt 1..5 by a FNV-1a hash of the tag.
/// No cross-session state: the accepted-set of exactly ONE session is passed in.
/// </summary>
public static class BagScanRules
{
    /// <summary>Maximum number of bags one scanner session may accept.</summary>
    public const int MaxAcceptedPerSession = 8;

    /// <summary>Make-up zones the scanner knows about (case-insensitive).</summary>
    private static readonly HashSet<string> KnownZones = new(StringComparer.OrdinalIgnoreCase)
    {
        "MU1", "MU2", "MU3",
    };

    /// <summary>Bag-tag barcode shape: 2 letters + 6 digits, case-insensitive (hb248015
    /// and HB248015 are the same bag for the duplicate rule).</summary>
    private static readonly Regex TagShape =
        new("^[A-Z]{2}[0-9]{6}$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Evaluate one scan. Returns the disposition, the scanner-display message and the
    /// make-up belt (0 when the bag was NOT accepted, proto parity). <paramref name="acceptedTags"/>
    /// is the case-insensitive accepted-tag set of the current session only.
    /// </summary>
    public static (ScanDisposition Disposition, string Message, int Belt, string NormalizedTag) Evaluate(
        string? tagNumber,
        string? zone,
        IReadOnlySet<string> acceptedTags,
        int acceptedCount)
    {
        if (acceptedCount >= MaxAcceptedPerSession)
        {
            return (ScanDisposition.LimitExceeded,
                $"Session make-up limit reached ({MaxAcceptedPerSession} bags already accepted).",
                0, string.Empty);
        }

        var tag = (tagNumber ?? string.Empty).Trim();
        if (!TagShape.IsMatch(tag))
        {
            return (ScanDisposition.Malformed,
                $"Tag '{tagNumber}' does not match the IATA barcode shape (2 letters + 6 digits).",
                0, string.Empty);
        }

        var effectiveZone = string.IsNullOrWhiteSpace(zone) ? "MU1" : zone;
        if (!KnownZones.Contains(effectiveZone))
        {
            return (ScanDisposition.UnknownZone,
                $"Zone '{zone}' is not part of the make-up map (MU1..MU3).",
                0, string.Empty);
        }

        if (acceptedTags.Contains(tag))
        {
            return (ScanDisposition.Duplicate,
                $"Tag '{tag}' was already accepted in this session.",
                0, tag);
        }

        return (ScanDisposition.Accepted,
            $"Bag '{tag}' filed to make-up zone {effectiveZone.ToUpperInvariant()}, belt {BeltFor(tag)}.",
            BeltFor(tag), tag);
    }

    /// <summary>Deterministic make-up belt placement (1..5) via FNV-1a 32 bit of the tag.</summary>
    public static int BeltFor(string tag)
    {
        var hash = 2166136261u;
        foreach (var raw in System.Text.Encoding.UTF8.GetBytes(tag))
        {
            hash ^= raw;
            hash *= 16777619u;
        }

        return (int)(hash % 5) + 1;
    }

    /// <summary>Stable lower-case tally key for summaries ("accepted", "limitExceeded", ...).</summary>
    public static string Key(ScanDisposition disposition) => disposition switch
    {
        ScanDisposition.Accepted => "accepted",
        ScanDisposition.Malformed => "malformed",
        ScanDisposition.Duplicate => "duplicate",
        ScanDisposition.UnknownZone => "unknownZone",
        ScanDisposition.LimitExceeded => "limitExceeded",
        _ => "unspecified",
    };
}
