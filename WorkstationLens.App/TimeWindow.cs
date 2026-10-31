namespace WorkstationLens.App;

/// <summary>
/// A bounded half-open UTC range [Start, End) shared by charts, insights and
/// downloaded evidence. Absolute custom dates are Unix seconds in UTC;
/// the browser translates the user's local datetime into those seconds.
/// </summary>
internal sealed record TimeWindow(long Start, long End, bool IsCustom)
{
    public double RequestedHours => (End - Start) / 3600.0;

    public static bool TryCreate(int? hours, long? from, long? to, out TimeWindow window,
        out string error)
    {
        window = null!;
        error = "";
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        const long max = 90L * 86400L;
        if (from.HasValue || to.HasValue)
        {
            if (!from.HasValue || !to.HasValue)
            {
                error = "Custom dates require both from and to.";
                return false;
            }
            if (from.Value >= to.Value || to.Value > now + 60 ||
                from.Value < now - max - 60 || to.Value - from.Value > max)
            {
                error = "Choose a past time range within the last 90 days (from before to).";
                return false;
            }
            window = new TimeWindow(from.Value, to.Value, true);
            return true;
        }
        int selectedHours = Math.Clamp(hours ?? 24, 1, 2160);
        window = new TimeWindow(now - selectedHours * 3600L, now + 1, false);
        return true;
    }

    public static TimeWindow LastHours(int hours)
    {
        TryCreate(hours,null,null,out var window,out _);
        return window;
    }
}
