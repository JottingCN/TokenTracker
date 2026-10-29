using System.Collections.Concurrent;
using System.Globalization;

namespace TokenTracker.Core.Pricing;

public static class BeijingCalendar
{
    // Statutory weekday holidays for 2025-2026, extracted from chinese-days 1.5.9.
    // Weekends (including make-up workday weekends) are always idle. Update yearly.
    private static readonly HashSet<string> BuiltInHolidays = new(StringComparer.Ordinal)
    {
        "2025-01-01",
        "2025-01-28",
        "2025-01-29",
        "2025-01-30",
        "2025-01-31",
        "2025-02-03",
        "2025-02-04",
        "2025-04-04",
        "2025-05-01",
        "2025-05-02",
        "2025-05-05",
        "2025-06-02",
        "2025-10-01",
        "2025-10-02",
        "2025-10-03",
        "2025-10-06",
        "2025-10-07",
        "2025-10-08",
        "2026-01-01",
        "2026-01-02",
        "2026-02-16",
        "2026-02-17",
        "2026-02-18",
        "2026-02-19",
        "2026-02-20",
        "2026-02-23",
        "2026-04-06",
        "2026-05-01",
        "2026-05-04",
        "2026-05-05",
        "2026-06-19",
        "2026-09-25",
        "2026-10-01",
        "2026-10-02",
        "2026-10-05",
        "2026-10-06",
        "2026-10-07",
    };

    private static readonly ConcurrentDictionary<int, HashSet<string>> LoadedHolidays = new();

    private static readonly TimeZoneInfo ChinaTimeZone = ResolveChinaTimeZone();

    public static void SetHolidayData(int year, IEnumerable<string> dates) =>
        LoadedHolidays[year] = new HashSet<string>(dates, StringComparer.Ordinal);

    public static bool HasLoadedHolidayData(int year) => LoadedHolidays.ContainsKey(year);

    public static DateTimeOffset ToBeijing(DateTimeOffset timestamp) =>
        TimeZoneInfo.ConvertTime(timestamp, ChinaTimeZone);

    public static bool IsHoliday(DateOnly date)
    {
        var key = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (LoadedHolidays.TryGetValue(date.Year, out var loaded) && loaded.Contains(key))
        {
            return true;
        }

        return BuiltInHolidays.Contains(key);
    }

    public static bool IsPeak(DateTimeOffset timestamp)
    {
        var beijing = ToBeijing(timestamp);

        if (beijing.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            return false;
        }

        if (IsHoliday(DateOnly.FromDateTime(beijing.DateTime)))
        {
            return false;
        }

        var hour = beijing.Hour;
        return hour is >= 9 and < 12 or >= 14 and < 18;
    }

    private static TimeZoneInfo ResolveChinaTimeZone()
    {
        foreach (var id in new[] { "China Standard Time", "Asia/Shanghai" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return TimeZoneInfo.CreateCustomTimeZone(
            "China Standard Time",
            TimeSpan.FromHours(8),
            "China Standard Time",
            "China Standard Time");
    }
}
