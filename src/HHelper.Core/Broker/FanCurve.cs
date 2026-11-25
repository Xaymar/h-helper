namespace HHelper.Core.Broker;

/// <summary>
/// One fan table: N up-thresholds in °C where the fan steps up, and N+1 speed points (spec 3.4 to 3.6).
/// On MS-158L N is 6. Meanings are [H] until spec Q1 is answered.
/// </summary>
public sealed record FanCurve(IReadOnlyList<byte> Thresholds, IReadOnlyList<byte> Speeds)
{
    public override string ToString()
        => $"T[{string.Join(",", Thresholds)}] S[{string.Join(",", Speeds)}]";
}

/// <summary>Validation rules for a user curve against the factory snapshot (spec 3.6).</summary>
public static class FanCurveValidator
{
    /// <returns>Null when valid, else the reason the curve is refused.</returns>
    public static string? Validate(FanCurve proposed, FanCurve factory, int thresholdMarginC)
    {
        if (proposed.Thresholds.Count != factory.Thresholds.Count)
            return $"Expected {factory.Thresholds.Count} thresholds, got {proposed.Thresholds.Count}.";
        if (proposed.Speeds.Count != factory.Speeds.Count)
            return $"Expected {factory.Speeds.Count} speed points, got {proposed.Speeds.Count}.";

        var tMin = factory.Thresholds.Min() - thresholdMarginC;
        var tMax = factory.Thresholds.Max() + thresholdMarginC;
        for (var i = 0; i < proposed.Thresholds.Count; i++)
        {
            var t = proposed.Thresholds[i];
            if (t < tMin || t > tMax)
                return $"Threshold {i + 1} ({t} °C) is outside the factory range {tMin}..{tMax} °C.";
            if (i > 0 && t <= proposed.Thresholds[i - 1])
                return $"Threshold {i + 1} ({t} °C) must be higher than threshold {i} ({proposed.Thresholds[i - 1]} °C).";
        }

        var sMin = factory.Speeds.Min();
        var sMax = factory.Speeds.Max();
        for (var i = 0; i < proposed.Speeds.Count; i++)
        {
            var s = proposed.Speeds[i];
            if (s < sMin) return $"Speed point {i + 1} ({s}) is below the factory minimum ({sMin}).";
            if (s > sMax) return $"Speed point {i + 1} ({s}) is above the factory maximum ({sMax}).";
            if (i > 0 && s < proposed.Speeds[i - 1])
                return $"Speed point {i + 1} ({s}) must not be lower than speed point {i} ({proposed.Speeds[i - 1]}).";
        }

        if (proposed.Speeds[^1] < factory.Speeds[^1])
            return $"The last speed point ({proposed.Speeds[^1]}) must be at least the factory value ({factory.Speeds[^1]}) so full-load cooling is never weakened.";

        return null;
    }
}
