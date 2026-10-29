using System.Globalization;
using TokenTracker.Core;

namespace TokenTracker.Core.Tests;

public sealed class RangeTimeTests
{
    private static DateTimeOffset Utc(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    [Fact]
    public void YesterdayCoversBeijingCalendarDay()
    {
        var now = Utc("2026-10-28T02:00:00Z"); // 10:00 Beijing
        var (fromMs, toMs) = RangeTime.Resolve(1, now);

        var from = DateTimeOffset.FromUnixTimeMilliseconds(fromMs);
        var to = DateTimeOffset.FromUnixTimeMilliseconds(toMs);

        Assert.Equal(Utc("2026-10-27T00:00:00+08:00").ToUnixTimeMilliseconds(), fromMs);
        Assert.Equal(Utc("2026-10-28T00:00:00+08:00").ToUnixTimeMilliseconds(), toMs);
        Assert.Equal(TimeSpan.FromDays(1), to - from);
    }

    [Fact]
    public void TodayStartsAtBeijingMidnight()
    {
        var now = Utc("2026-10-28T02:00:00Z");
        var (fromMs, toMs) = RangeTime.Resolve(0, now);

        Assert.Equal(Utc("2026-10-28T00:00:00+08:00").ToUnixTimeMilliseconds(), fromMs);
        Assert.Equal(long.MaxValue, toMs);
    }

    [Fact]
    public void RollingAndAllRanges()
    {
        var now = Utc("2026-10-28T02:00:00Z");

        Assert.Equal(now.AddDays(-7).ToUnixTimeMilliseconds(), RangeTime.Resolve(2, now).FromMs);
        Assert.Equal(now.AddDays(-30).ToUnixTimeMilliseconds(), RangeTime.Resolve(3, now).FromMs);
        Assert.Equal(0, RangeTime.Resolve(4, now).FromMs);
        Assert.Equal(long.MaxValue, RangeTime.Resolve(4, now).ToMs);
    }
}
