namespace Hummingbird.Airlines.Backend.Storage;

/// <summary>
/// Virtual clock shared by every simulated feed (baggage claim board, gate turnaround):
/// virtual time = process start + real elapsed x speed. Anchoring at process start keeps
/// calendar dates plausible for any speed while event sequence ids remain deterministic
/// and monotonic for the process lifetime (resuming across a restart is out of scope for
/// this testbed).
/// </summary>
public static class SimClock
{
    /// <summary>Minimum allowed time compression factor (plain real time).</summary>
    public const double MinSpeed = 1;

    /// <summary>Maximum allowed time compression factor.</summary>
    public const double MaxSpeed = 60;

    /// <summary>Board-clock origin: the process start instant (UTC ms).</summary>
    public static long OriginUnixMs { get; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Normalize a speed hint: out-of-range or non-finite values clamp to [1, 60].</summary>
    public static double ClampSpeed(double? speed) =>
        speed is { } candidate && double.IsFinite(candidate) && candidate >= MinSpeed
            ? Math.Min(Math.Round(candidate, 3), MaxSpeed)
            : MinSpeed;

    /// <summary>Virtual now for the given compression factor (speed 1 = exactly real UTC).</summary>
    public static DateTimeOffset Time(double speed)
    {
        var clamped = ClampSpeed(speed);
        var realMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return DateTimeOffset.FromUnixTimeMilliseconds(
            clamped <= MinSpeed ? realMs : OriginUnixMs + (long)((realMs - OriginUnixMs) * clamped));
    }
}
