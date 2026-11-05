using System.Globalization;

namespace NudgeJudge.Core.Rules;

/// <summary>
/// A "time" condition value: days, hours, or both, in local time.
/// <list type="bullet">
/// <item><c>09:00-17:00</c>, <c>9-17</c>, <c>9am-5pm</c>, <c>9 to 5pm</c>: every day, end exclusive;</item>
/// <item><c>Mon-Fri 09:00-17:00</c>, <c>Sat,Sun</c>, <c>weekdays 9-17</c>, <c>weekends</c>: only on those days;</item>
/// <item><c>22:00-06:00</c>: overnight. The days name the day the window starts, so <c>Fri 22:00-02:00</c>
/// includes 01:00 on Saturday.</item>
/// </list>
/// </summary>
public static class TimeWindow
{
    public const string Example = "Mon-Fri 09:00-17:00";

    private const int AllDays = 0x7F;

    /// <summary>What a value means: a day mask (Sunday = bit 0) and, unless it's all day, start and end minutes.</summary>
    private readonly record struct Window(int Days, int? Start, int? End);

    /// <summary>A predicate on local time, or an error.</summary>
    public static (Func<DateTime, bool>? Predicate, string? Error) Compile(string? value)
    {
        var (window, error) = Parse(value);
        if (window is not { } w) return (null, error);
        var days = w.Days;
        if (w.Start is not { } s || w.End is not { } e)
            return (t => OnDay(days, t.DayOfWeek), null);
        if (s < e)
            return (t => OnDay(days, t.DayOfWeek) && Minute(t) >= s && Minute(t) < e, null);

        // Overnight: the evening part on a listed day, the morning part on the day after one.
        return (t =>
        {
            var m = Minute(t);
            return (m >= s && OnDay(days, t.DayOfWeek)) ||
                   (m < e && OnDay(days, (DayOfWeek)(((int)t.DayOfWeek + 6) % 7)));
        }, null);
    }

    /// <summary>
    /// How a valid value was read, in words, so a surprise shows before it routes anything:
    /// "Weekdays, 09:00 to 17:00", or "Every day, 09:00 to 05:00 the next day (overnight)" for "9-5".
    /// Null when the value doesn't parse (the error says why).
    /// </summary>
    public static string? Describe(string? value)
    {
        if (Parse(value).Window is not { } w) return null;
        var days = DescribeDays(w.Days);
        if (w.Start is not { } s || w.End is not { } e) return $"{days}, all day";
        var end = e == 24 * 60 ? "midnight" : Clock(e);
        return s < e
            ? $"{days}, {Clock(s)} to {end}"
            : $"{days}, {Clock(s)} to {end} the next day (overnight)";
    }

    private static (Window? Window, string? Error) Parse(string? value)
    {
        value = value?.Trim() ?? "";
        if (value.Length == 0) return (null, "Enter a time, e.g. " + Example + ".");
        if (value.Length > 200) return (null, "That's too long for a time.");

        // Split off a leading day part: everything up to the first token that starts with a digit.
        var text = value.ToLowerInvariant().Replace('–', '-').Replace('—', '-');
        var firstDigit = text.IndexOfAny("0123456789".ToCharArray());
        var dayText = (firstDigit < 0 ? text : text[..firstDigit]).Trim();
        var timeText = firstDigit < 0 ? "" : text[firstDigit..].Trim();

        var days = AllDays;
        if (dayText.Length > 0)
        {
            var parsedDays = ParseDays(dayText.Replace(" -", "-").Replace("- ", "-"));
            if (parsedDays is null) return (null, "Couldn't read the days. Use names like Mon-Fri, Sat,Sun or weekdays.");
            days = parsedDays.Value;
        }

        if (timeText.Length == 0) return (new Window(days, null, null), null);

        var parts = timeText.Split([" to ", "-"], 3, StringSplitOptions.TrimEntries);
        if (parts.Length != 2) return (null, "Write the hours as start-end, e.g. 09:00-17:00.");
        var (start, startError) = ParseTime(parts[0], isEnd: false);
        if (startError is not null) return (null, startError);
        var (end, endError) = ParseTime(parts[1], isEnd: true);
        if (endError is not null) return (null, endError);
        if (start == end) return (null, "The start and end are the same time.");
        return (new Window(days, start, end), null);
    }

    private static string DescribeDays(int mask)
    {
        switch (mask)
        {
            case AllDays: return "Every day";
            case 0b0111110: return "Weekdays";
            case 0b1000001: return "Weekends";
        }
        // Monday first, as people list a week; runs of three or more read as a range ("Mon to Thu").
        int[] order = [1, 2, 3, 4, 5, 6, 0];
        var names = CultureInfo.InvariantCulture.DateTimeFormat.AbbreviatedDayNames;
        var parts = new List<string>();
        for (var i = 0; i < order.Length;)
        {
            if (!OnDay(mask, (DayOfWeek)order[i])) { i++; continue; }
            var j = i;
            while (j + 1 < order.Length && OnDay(mask, (DayOfWeek)order[j + 1])) j++;
            parts.Add(j - i >= 2 ? $"{names[order[i]]} to {names[order[j]]}"
                : j > i ? $"{names[order[i]]}, {names[order[j]]}"
                : names[order[i]]);
            i = j + 1;
        }
        return string.Join(", ", parts);
    }

    private static string Clock(int minutes) => $"{minutes / 60:00}:{minutes % 60:00}";

    private static bool OnDay(int days, DayOfWeek day) => (days & (1 << (int)day)) != 0;

    private static int Minute(DateTime t) => t.Hour * 60 + t.Minute;

    /// <summary>Bit per day (Sunday = bit 0), or null when unreadable.</summary>
    private static int? ParseDays(string text)
    {
        var mask = 0;
        foreach (var raw in text.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw)
            {
                case "weekdays": mask |= 0b0111110; continue;
                case "weekends": case "weekend": mask |= 0b1000001; continue;
                case "daily": case "everyday": mask |= 0x7F; continue;
            }
            var range = raw.Split('-', StringSplitOptions.TrimEntries);
            if (range.Length > 2) return null;
            var from = Day(range[0]);
            var to = range.Length == 2 ? Day(range[1]) : from;
            if (from is null || to is null) return null;
            // Ranges wrap round the week: Fri-Mon is Fri, Sat, Sun, Mon.
            for (var d = from.Value; ; d = (d + 1) % 7)
            {
                mask |= 1 << d;
                if (d == to.Value) break;
            }
        }
        return mask == 0 ? null : mask;
    }

    private static int? Day(string s)
    {
        if (s.Length < 2) return null; // "t" and "s" are ambiguous
        var names = CultureInfo.InvariantCulture.DateTimeFormat.DayNames; // Sunday first
        for (var i = 0; i < names.Length; i++)
            if (names[i].StartsWith(s, StringComparison.OrdinalIgnoreCase)) return i;
        return null;
    }

    /// <summary>Minutes since midnight. Accepts 9, 09, 9:30, 09:30, 9am, 5:30pm; an end may be 24:00.</summary>
    private static (int Minutes, string? Error) ParseTime(string s, bool isEnd)
    {
        var bad = (0, $"Couldn't read “{s}” as a time. Use 24-hour times like 09:00, or 9am.");
        s = s.Replace(" ", "");
        var pm = s.EndsWith("pm", StringComparison.Ordinal);
        var am = s.EndsWith("am", StringComparison.Ordinal);
        if (am || pm) s = s[..^2];

        var colon = s.IndexOf(':');
        var hourText = colon < 0 ? s : s[..colon];
        var minuteText = colon < 0 ? "0" : s[(colon + 1)..];
        if (hourText.Length is < 1 or > 2 || minuteText.Length is < 1 or > 2 ||
            !int.TryParse(hourText, NumberStyles.None, CultureInfo.InvariantCulture, out var hour) ||
            !int.TryParse(minuteText, NumberStyles.None, CultureInfo.InvariantCulture, out var minute) ||
            minute > 59)
            return bad;

        if (am || pm)
        {
            if (hour is < 1 or > 12) return bad;
            hour = hour % 12 + (pm ? 12 : 0);
        }
        var total = hour * 60 + minute;
        if (total > 24 * 60 || (total == 24 * 60 && !isEnd)) return bad;
        return (total, null);
    }
}
