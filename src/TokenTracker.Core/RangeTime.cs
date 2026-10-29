using TokenTracker.Core.Pricing;

namespace TokenTracker.Core;

public static class RangeTime
{
    public static (long FromMs, long ToMs) Resolve(int range, DateTimeOffset now)
    {
        var beijing = BeijingCalendar.ToBeijing(now);
        var todayStart = new DateTimeOffset(
            beijing.Year,
            beijing.Month,
            beijing.Day,
            0,
            0,
            0,
            beijing.Offset);
        var todayStartMs = todayStart.ToUnixTimeMilliseconds();

        return range switch
        {
            0 => (todayStartMs, long.MaxValue),
            1 => (todayStart.AddDays(-1).ToUnixTimeMilliseconds(), todayStartMs),
            2 => (now.AddDays(-7).ToUnixTimeMilliseconds(), long.MaxValue),
            3 => (now.AddDays(-30).ToUnixTimeMilliseconds(), long.MaxValue),
            _ => (0, long.MaxValue),
        };
    }
}
