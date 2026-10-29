using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using TokenTracker.Core.Storage;

namespace TokenTracker.Core.Pricing;

public sealed class HolidayService
{
    public const string DefaultUrlTemplate = "https://api.apisbo.com/holidays/year/{0}";

    private readonly SpanStore _store;
    private readonly HttpClient _http;
    private readonly string _urlTemplate;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public HolidayService(SpanStore store, HttpClient? http = null, string? urlTemplate = null)
    {
        _store = store;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        _urlTemplate = string.IsNullOrWhiteSpace(urlTemplate)
            ? DefaultUrlTemplate
            : urlTemplate;
    }

    public async Task<bool> EnsureYearAsync(int year, CancellationToken token = default)
    {
        if (BeijingCalendar.HasLoadedHolidayData(year))
        {
            return true;
        }

        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (BeijingCalendar.HasLoadedHolidayData(year))
            {
                return true;
            }

            var stored = _store.GetHolidayYear(year);
            if (!string.IsNullOrWhiteSpace(stored))
            {
                var dates = stored.Split(
                    '\n',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (dates.Length > 0)
                {
                    BeijingCalendar.SetHolidayData(year, dates);
                    return true;
                }
            }

            var fetched = await FetchAsync(year, token).ConfigureAwait(false);
            if (fetched.Count < 5)
            {
                Debug.WriteLine($"holiday data for {year} looks incomplete ({fetched.Count} dates), skipped");
                return false;
            }

            _store.UpsertHolidayYear(
                year,
                string.Join('\n', fetched),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            BeijingCalendar.SetHolidayData(year, fetched);

            var offset = TimeSpan.FromHours(8);
            var yearStartMs = new DateTimeOffset(year, 1, 1, 0, 0, 0, offset)
                .ToUnixTimeMilliseconds();
            var yearEndMs = new DateTimeOffset(year + 1, 1, 1, 0, 0, 0, offset)
                .ToUnixTimeMilliseconds();
            _store.RecomputeCostsInRange(yearStartMs, yearEndMs);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"holiday fetch failed for {year}: {exception.Message}");
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task EnsureYearsAsync(
        IEnumerable<int> years,
        CancellationToken token = default)
    {
        foreach (var year in years.Distinct())
        {
            try
            {
                await EnsureYearAsync(year, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task<List<string>> FetchAsync(int year, CancellationToken token)
    {
        var url = string.Format(CultureInfo.InvariantCulture, _urlTemplate, year);
        using var response = await _http.GetAsync(url, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(token)
            .ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token)
            .ConfigureAwait(false);

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        if (root.TryGetProperty("code", out var code) && !IsSuccessCode(code))
        {
            return [];
        }

        if (!root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var dates = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var type = item.TryGetProperty("type", out var typeElement)
                ? typeElement.GetString()
                : null;
            if (!string.Equals(type, "holiday", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var date = item.TryGetProperty("date", out var dateElement)
                ? dateElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(date) ||
                !DateTime.TryParseExact(
                    date,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var parsed) ||
                parsed.Year != year ||
                !seen.Add(date))
            {
                continue;
            }

            dates.Add(date);
        }

        return dates;
    }

    private static bool IsSuccessCode(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt32(out var code) && code == 0,
            JsonValueKind.String => string.Equals(
                element.GetString(),
                "0",
                StringComparison.Ordinal),
            _ => true,
        };
}
