using System.Globalization;
using System.Text.RegularExpressions;

namespace Timer.Core;

/// <summary>What was recognised in a typed note: its words, and a time and a day when the words named them.</summary>
public sealed record NoteParts(string Text, TimeSpan? At, DateTime? Day);

/// <summary>
/// The note shorthand in one place. A time such as 14:30 (or @14:30) sets the time. A word starting with @ sets the day: @today, @tomorrow, a weekday
/// such as @fri (the next one), a date such as @21oct, or @2026-10-21. A leading ! makes the note a deadline, and a #Tag naming one account links it to
/// that account, as it does for a to-do. Every part is something typed on purpose, so ordinary words are never taken for a date, and every part
/// can be taken back out: clearing one puts it back into the words instead of losing it.
/// </summary>
public static class NoteShorthand
{
    private static readonly Regex Day = new(@"(?:^|\s)@([A-Za-z]+|\d{1,2}-?[A-Za-z]{3,9}|\d{4}-\d{2}-\d{2})(?=\s|$)", RegexOptions.Compiled);
    private static readonly string[] Months = { "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec" };

    /// <summary>Splits typed words into the note's text, its time and the day it names, if any. Today decides what tomorrow and the weekdays mean.</summary>
    public static NoteParts Parse(string typed, DateTime today, bool task = false)
    {
        DateTime? day = null; var rest = typed;
        foreach (Match match in Day.Matches(typed))
        {
            if (DayFor(match.Groups[1].Value, today) is not { } found) continue;
            day = found; rest = Regex.Replace(typed.Remove(match.Index, match.Length).Trim(), @"\s{2,}", " ");
            if (rest.Length == 0) rest = match.Groups[1].Value; // A note that was only a date keeps the word.
            break;
        }
        // Typed into the To-do list a note is a task: a time-like word stays a word and a leading ! is dropped, so what was typed there stays there.
        if (task) { var plain = rest.Trim().TrimStart('!', ' '); return new NoteParts(plain.Length == 0 ? rest.Trim() : plain, null, day); }
        var (text, at) = DayNotes.Parse(rest);
        return new NoteParts(text, at, day);
    }

    /// <summary>The day a word after @ stands for, or null when it is not one.</summary>
    public static DateTime? DayFor(string word, DateTime today)
    {
        var w = word.ToLowerInvariant().Trim(); today = today.Date;
        if (w == "today") return today;
        if (w is "tomorrow" or "tmrw") return today.AddDays(1);
        if (DateTime.TryParseExact(w, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var iso)) return iso;
        var names = new[] { "sun", "mon", "tue", "wed", "thu", "fri", "sat" };
        if (w.Length >= 3 && Array.FindIndex(names, n => w.StartsWith(n, StringComparison.Ordinal)) is var index and >= 0 && IsDayName(w, index))
        {
            var ahead = ((index - (int)today.DayOfWeek) + 7) % 7; return today.AddDays(ahead == 0 ? 7 : ahead);
        }
        var m = Regex.Match(w, @"^(\d{1,2})-?([a-z]{3,9})$");
        if (m.Success && Array.IndexOf(Months, m.Groups[2].Value[..3]) is var month and >= 0 && int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) is var d and >= 1 and <= 31)
        {
            foreach (var year in new[] { today.Year, today.Year + 1 })
                if (d <= DateTime.DaysInMonth(year, month + 1) && new DateTime(year, month + 1, d) is var candidate && candidate >= today) return candidate;
        }
        return null;
    }
    private static bool IsDayName(string w, int index)
    {
        var full = new[] { "sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday" }[index];
        return full.StartsWith(w, StringComparison.Ordinal);
    }

    /// <summary>The #Tag that names an account, for writing into a note: the account's name with its spaces taken out.</summary>
    public static string TagFor(string account) => "#" + new string(account.Where(c => !char.IsWhiteSpace(c)).ToArray());

    /// <summary>
    /// A note's words after accounts were renamed or deleted: a tag that named a renamed account names its new name, and one that named a deleted account
    /// becomes an ordinary word. <paramref name="before"/> are the account names as they were, since that is what the old tag was read against.
    /// </summary>
    public static string Relink(string text, IEnumerable<string> before, IReadOnlyDictionary<string, string> renamed, IReadOnlyCollection<string> deleted)
    {
        var names = before.ToList();
        if (AccountTags.Find(text, names) is not { } found) return text;
        var start = text[found.Index] == '#' ? found.Index : found.Index + 1;
        if (renamed.TryGetValue(found.Account, out var now)) return text.Remove(start, 1 + found.Tag.Length).Insert(start, TagFor(now));
        return deleted.Contains(found.Account) ? text.Remove(start, 1) : text;
    }

    /// <summary>The account a note's #Tag links to, or null.</summary>
    public static string? AccountOf(string text, IEnumerable<string> accounts) => AccountTags.Find(text, accounts)?.Account;

    // ---- Taking recognised parts back out. Each puts the words back, so nothing typed is lost. ----

    /// <summary>The words with a time they no longer carry written back at the front.</summary>
    public static string WithTimeInWords(string text, TimeSpan at)
    {
        var time = at.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
        // A deadline marker has to stay first, or putting the time back would quietly make the note an ordinary one.
        return text.StartsWith('!') ? "! " + time + " " + text.TrimStart('!', ' ') : time + " " + text;
    }
    /// <summary>The words without the deadline marker.</summary>
    public static string WithoutDeadline(string text) => text.TrimStart('!', ' ');
    /// <summary>The words with the #Tag written as an ordinary word, so the sentence still reads.</summary>
    public static string WithoutAccountTag(string text, IEnumerable<string> accounts)
    {
        if (AccountTags.Find(text, accounts) is not { } found) return text;
        var start = text[found.Index] == '#' ? found.Index : found.Index + 1; // The tag may follow a space that is part of the match.
        return text.Remove(start, 1);
    }
    /// <summary>The words shown for a note: without the deadline marker, and without the account tag that has its own chip. A note that was only its tag shows the account.</summary>
    public static string Display(DayNote note, IEnumerable<string> accounts)
    {
        var text = DayNotes.DisplayText(note);
        if (AccountTags.Find(text, accounts) is not { } found) return text;
        var shown = Regex.Replace(text.Remove(found.Index, found.Length).Trim(), @"\s{2,}", " ");
        return shown.Length == 0 ? found.Account : shown;
    }
}
