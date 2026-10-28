using System.Globalization;

namespace Timer.Core;

/// <summary>
/// How often a note repeats: every N weeks (weeks start on Monday), on the days in <see cref="DaysMask"/> (one bit per DayOfWeek, Sunday as bit 0; 0 means
/// the same weekday as the first one), until an optional last day. Typed as `every day`, `every weekday`, `every mon`, `every mon,wed`,
/// `every week`, `every 2 weeks`, `every 2 weeks fri`, `every month` or `every 3 months`, each with an optional `until 2026-12-31` (or `until 31dec`). Always spelled
/// with "every", so a word such as "daily" in a sentence never makes a note repeat by accident. A monthly rule (<see cref="EveryMonths"/> above 0) falls on the
/// same day of the month as the first one, or the last day of a month too short for it (the 31st becomes the 28th, 29th or 30th), and ignores the weekday
/// settings.
/// </summary>
public sealed record Recurrence(int EveryWeeks, int DaysMask, DateTime? Until = null, int EveryMonths = 0)
{
    public const int AllDays = 0b1111111, Weekdays = 0b0111110;
    private static readonly string[] Full = { "sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday" };

    private static DateTime WeekStart(DateTime day) => day.Date.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    /// <summary>Whether a series that began on <paramref name="start"/> has an occurrence on <paramref name="day"/>.</summary>
    public bool Includes(DateTime start, DateTime day)
    {
        day = day.Date; start = start.Date;
        if (day < start || (Until is { } last && day > last.Date)) return false;
        if (EveryMonths > 0)
        {
            var months = (day.Year - start.Year) * 12 + day.Month - start.Month;
            return months % EveryMonths == 0 && day.Day == Math.Min(start.Day, DateTime.DaysInMonth(day.Year, day.Month));
        }
        var mask = DaysMask == 0 ? 1 << (int)start.DayOfWeek : DaysMask;
        if ((mask & (1 << (int)day.DayOfWeek)) == 0) return false;
        return (WeekStart(day) - WeekStart(start)).Days / 7 % Math.Max(1, EveryWeeks) == 0;
    }

    /// <summary>The occurrence days from <paramref name="from"/> to <paramref name="to"/>, both included.</summary>
    public List<DateTime> Between(DateTime start, DateTime from, DateTime to)
    {
        var found = new List<DateTime>();
        for (var day = from.Date; day <= to.Date; day = day.AddDays(1)) if (Includes(start, day)) found.Add(day);
        return found;
    }

    private string Days()
    {
        if (DaysMask == AllDays) return "day";
        if (DaysMask == Weekdays) return "weekday";
        return string.Join(",", Enumerable.Range(0, 7).Select(i => (i + 1) % 7).Where(i => (DaysMask & (1 << i)) != 0).Select(i => Full[i][..3]));
    }

    /// <summary>The rule written so it can be typed back and read again: every weekday, every mon,wed, every 2 weeks fri, with until 2026-12-31 when it ends.</summary>
    public string Typed()
    {
        var text = EveryMonths > 0
            ? EveryMonths == 1 ? "every month" : "every " + EveryMonths + " months"
            : EveryWeeks <= 1
            ? DaysMask == 0 ? "every week" : "every " + Days()
            : "every " + EveryWeeks + " weeks" + (DaysMask == 0 ? "" : " " + Days());
        return Until is { } last ? text + " until " + last.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : text;
    }

    /// <summary>The rule in words for showing: Every weekday, Every Mon, Wed, Every 2 weeks, with the last day when there is one.</summary>
    public string Words()
    {
        var days = DaysMask is AllDays or Weekdays or 0 ? Days() : string.Join(", ", Enumerable.Range(0, 7).Select(i => (i + 1) % 7).Where(i => (DaysMask & (1 << i)) != 0).Select(i => char.ToUpperInvariant(Full[i][0]) + Full[i][1..3]));
        var text = EveryMonths > 0
            ? EveryMonths == 1 ? "Every month" : "Every " + EveryMonths + " months"
            : EveryWeeks <= 1
            ? DaysMask == 0 ? "Every week" : "Every " + days
            : "Every " + EveryWeeks + " weeks" + (DaysMask == 0 ? "" : " on " + days);
        return Until is { } last ? text + " until " + last.ToString("d MMM yyyy", CultureInfo.GetCultureInfo("en-GB")) : text;
    }

    /// <summary>The rule as stored: weeks;mask;last day, and a fourth part, the months, only for a monthly rule (so a weekly rule is stored exactly as it always was).</summary>
    public string ToRule() => $"{EveryWeeks};{DaysMask};{Until?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}" + (EveryMonths > 0 ? ";" + EveryMonths : "");
    public static Recurrence? FromRule(string? rule)
    {
        var bits = (rule ?? "").Split(';');
        if (bits.Length is not (3 or 4) || !int.TryParse(bits[0], out var weeks) || !int.TryParse(bits[1], out var mask) || weeks < 1 || mask is < 0 or > AllDays) return null;
        var months = 0;
        if (bits.Length == 4 && (!int.TryParse(bits[3], out months) || months < 1)) return null;
        DateTime? until = null;
        if (bits[2].Length > 0)
        {
            if (!DateTime.TryParseExact(bits[2], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var last)) return null;
            until = last;
        }
        return new Recurrence(weeks, mask, until, months);
    }

    private static int? DayList(string token)
    {
        var mask = 0;
        foreach (var part in token.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var w = part.ToLowerInvariant();
            var index = w.Length >= 3 ? Array.FindIndex(Full, f => f.StartsWith(w, StringComparison.Ordinal)) : -1;
            if (index < 0) return null;
            mask |= 1 << index;
        }
        return mask == 0 ? null : mask;
    }

    /// <summary>
    /// Finds a repeat phrase in typed words, such as `every weekday` or `every day until 31dec`, and returns the words without it. Only a whole phrase counts, so
    /// a sentence like "every time we meet" is left alone. Today decides what a date such as 31dec means.
    /// </summary>
    public static Recurrence? Extract(string typed, DateTime today, out string rest)
    {
        rest = typed;
        var tokens = typed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < tokens.Length; i++)
        {
            var word = tokens[i].ToLowerInvariant(); int length; int weeks = 1, mask, months = 0;
            if (word == "every" && i + 1 < tokens.Length)
            {
                var next = tokens[i + 1].ToLowerInvariant();
                if (next == "day") { length = 2; mask = AllDays; }
                else if (next is "weekday" or "weekdays") { length = 2; mask = Weekdays; }
                else if (next == "month") { length = 2; mask = 0; months = 1; }
                else if (int.TryParse(next, out var m) && m is >= 2 and <= 12 && i + 2 < tokens.Length && tokens[i + 2].ToLowerInvariant() is "months" or "month") { length = 3; mask = 0; months = m; }
                else if (next == "week") { length = 2; mask = 0; if (i + 2 < tokens.Length && DayList(tokens[i + 2].ToLowerInvariant()) is { } listed) { mask = listed; length++; } }
                else if (int.TryParse(next, out var n) && n is >= 2 and <= 12 && i + 2 < tokens.Length && tokens[i + 2].ToLowerInvariant() is "weeks" or "week")
                {
                    weeks = n; length = 3; mask = 0;
                    if (i + 3 < tokens.Length && DayList(tokens[i + 3].ToLowerInvariant()) is { } listed) { mask = listed; length++; }
                }
                else if (DayList(next) is { } days) { length = 2; mask = days; }
                else continue;
            }
            else continue;
            DateTime? until = null;
            if (i + length + 1 < tokens.Length && tokens[i + length].Equals("until", StringComparison.OrdinalIgnoreCase)
                && NoteShorthand.DayFor(tokens[i + length + 1], today) is { } last) { until = last; length += 2; }
            rest = string.Join(" ", tokens.Take(i).Concat(tokens.Skip(i + length)));
            return new Recurrence(weeks, mask, until, months);
        }
        return null;
    }
}

/// <summary>
/// A repeating note's template and how far its notes have been made. The notes themselves are ordinary dated notes that carry <see cref="Id"/>, so each can be
/// moved, ticked or removed alone; <see cref="GeneratedTo"/> is the last day notes were made for, so a note removed from the series is not made again.
/// </summary>
public sealed record NoteSeries(long Id, Recurrence Rule, DateTime Start, DateTime GeneratedTo, string Text, TimeSpan? At, TimeSpan? End, DayNoteKind? Kind = null)
{
    public const int HorizonDays = 56;

    /// <summary>The days still to make notes for: after what was made, from today on (the past is never filled in), up to the horizon.</summary>
    public IReadOnlyList<DateTime> Pending(DateTime today, int horizonDays = HorizonDays)
    {
        var from = new[] { GeneratedTo.Date.AddDays(1), Start.Date, today.Date }.Max();
        return Rule.Between(Start, from, today.Date.AddDays(horizonDays));
    }
}
