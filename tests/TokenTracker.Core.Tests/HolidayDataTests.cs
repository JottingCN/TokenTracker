using System.Globalization;
using TokenTracker.Core.Pricing;

namespace TokenTracker.Core.Tests;

public sealed class HolidayDataTests
{
    private static DateTimeOffset Utc(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    [Fact]
    public void LoadedHolidayDataAppliesToYearWithoutBuildInData()
    {
        var holiday = new DateOnly(2031, 1, 1);
        Assert.False(BeijingCalendar.IsHoliday(holiday));
        Assert.True(BeijingCalendar.IsPeak(Utc("2031-01-01T02:00:00Z")));

        BeijingCalendar.SetHolidayData(2031, ["2031-01-01"]);

        Assert.True(BeijingCalendar.HasLoadedHolidayData(2031));
        Assert.True(BeijingCalendar.IsHoliday(holiday));
        Assert.False(BeijingCalendar.IsPeak(Utc("2031-01-01T02:00:00Z")));
        Assert.True(BeijingCalendar.IsPeak(Utc("2031-01-02T02:00:00Z")));
    }

    [Fact]
    public void LoadedDataDoesNotDropBuiltInHolidays()
    {
        BeijingCalendar.SetHolidayData(2026, ["2026-12-31"]);

        Assert.True(BeijingCalendar.IsHoliday(new DateOnly(2026, 10, 1)));
        Assert.True(BeijingCalendar.IsHoliday(new DateOnly(2026, 12, 31)));
        Assert.False(BeijingCalendar.IsHoliday(new DateOnly(2026, 12, 30)));
    }
}
