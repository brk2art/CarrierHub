using System.ComponentModel;
using ModelContextProtocol.Server;

// Another team's service: public holidays and working days, as an MCP server. CarrierHub doesn't own it; the Ops
// Assistant only consumes it. Internal network only (no sign-in) to keep the example small; a real one would be
// protected like CarrierHub.McpHttp.

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddMcpServer()
    .WithHttpTransport(o => o.Stateless = true)
    .WithTools<CalendarTools>();

var app = builder.Build();

app.MapMcp("/mcp");
app.MapDefaultEndpoints();

app.Run();

[McpServerToolType]
public sealed class CalendarTools
{
    [McpServerTool(Name = "get_public_holidays")]
    [Description("Lists the official public holidays of a country between two dates (inclusive).")]
    public static object GetPublicHolidays(
        [Description("Country code. Supported: DK (Denmark).")] string country,
        [Description("First date, yyyy-MM-dd.")] DateOnly from,
        [Description("Last date, yyyy-MM-dd. At most one year after the first.")] DateOnly to)
    {
        if (Unsupported(country) is { } error) return error;
        if (to < from || to.DayNumber - from.DayNumber > 366)
            return new { error = "The range must run forward and cover at most one year." };

        var holidays = new List<object>();
        for (var year = from.Year; year <= to.Year; year++)
            holidays.AddRange(Denmark(year)
                .Where(h => h.Date >= from && h.Date <= to)
                .Select(h => new { date = h.Date.ToString("yyyy-MM-dd"), weekday = h.Date.DayOfWeek.ToString(), name = h.Name }));

        return new { country = "DK", from = from.ToString("yyyy-MM-dd"), to = to.ToString("yyyy-MM-dd"), holidays };
    }

    [McpServerTool(Name = "add_working_days")]
    [Description("Moves a date forward by a number of working days (Monday to Friday, skipping public holidays).")]
    public static object AddWorkingDays(
        [Description("Country code. Supported: DK (Denmark).")] string country,
        [Description("The date to start from, yyyy-MM-dd.")] DateOnly date,
        [Description("How many working days to move forward, 0 to 60.")] int days)
    {
        if (Unsupported(country) is { } error) return error;
        if (days is < 0 or > 60) return new { error = "days must be between 0 and 60." };

        var result = date;
        for (var moved = 0; moved < days;)
        {
            result = result.AddDays(1);
            if (IsWorkingDay(result)) moved++;
        }
        return new
        {
            country = "DK",
            start = date.ToString("yyyy-MM-dd"),
            workingDays = days,
            result = result.ToString("yyyy-MM-dd"),
            weekday = result.DayOfWeek.ToString()
        };
    }

    private static object? Unsupported(string country) =>
    country.Trim().Equals("DK", StringComparison.OrdinalIgnoreCase)
        ? null
        : new { error = $"Country '{country}' is not supported. Supported: DK." };

    private static bool IsWorkingDay(DateOnly d) =>
        d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && Denmark(d.Year).All(h => h.Date != d);

    // Denmark's official public holidays ("helligdage"). Great Prayer Day was abolished from 2024; Christmas Eve,
    // New Year's Eve and Constitution Day are customary days off, not public holidays.
    private static IEnumerable<(DateOnly Date, string Name)> Denmark(int year)
    {
        var easter = EasterSunday(year);
        yield return (new DateOnly(year, 1, 1), "New Year's Day");
        yield return (easter.AddDays(-3), "Maundy Thursday");
        yield return (easter.AddDays(-2), "Good Friday");
        yield return (easter, "Easter Sunday");
        yield return (easter.AddDays(1), "Easter Monday");
        yield return (easter.AddDays(39), "Ascension Day");
        yield return (easter.AddDays(49), "Whit Sunday");
        yield return (easter.AddDays(50), "Whit Monday");
        yield return (new DateOnly(year, 12, 25), "Christmas Day");
        yield return (new DateOnly(year, 12, 26), "Second Day of Christmas");
    }

    // The Gregorian Easter date (the Meeus/Jones/Butcher algorithm). 2026 → 5 April.
    private static DateOnly EasterSunday(int y)
    {
        int a = y % 19, b = y / 100, c = y % 100, d = b / 4, e = b % 4, f = (b + 8) / 25, g = (b - f + 1) / 3;
        int h = (19 * a + b - d - g + 15) % 30, i = c / 4, k = c % 4, l = (32 + 2 * e + 2 * i - h - k) % 7;
        int m = (a + 11 * h + 22 * l) / 451, n = h + l - 7 * m + 114;
        return new DateOnly(y, n / 31, n % 31 + 1);
    }
}
