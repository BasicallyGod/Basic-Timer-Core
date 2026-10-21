using System.Globalization;
using System.Text.RegularExpressions;

namespace Timer.Core;

/// <summary>A note on a day, with an optional time. Notes with a date feed Coming up, and later the timeline. A note with a time and an end (14:00 - 15:30) is a meeting: planned time, not tracked time.</summary>
public sealed record DayNote(long Id, DateTime Day, TimeSpan? At, string Text, bool Done = false, TimeSpan? End = null);

public static class DayNotes
{
    private static readonly Regex Time = new(@"(?:^|\s)@?([01]?\d|2[0-3]):([0-5]\d)(?=\s|$)", RegexOptions.Compiled);

    /// <summary>Pulls a time such as 14:30 (or @14:30) out of what was typed. A note with no time is just a note for the day.</summary>
    public static (string Text, TimeSpan? At) Parse(string typed)
    {
        var match = Time.Match(typed);
        if (!match.Success) return (typed.Trim(), null);
        var at = new TimeSpan(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture), 0);
        var text = Regex.Replace(typed.Remove(match.Index, match.Length).Trim(), @"\s{2,}", " ");
        return (text.Length == 0 ? typed.Trim() : text, at);
    }

    /// <summary>The notes still to come: those today or later that are not done, soonest first, within a number of days.</summary>
    public static List<DayNote> ComingUp(IEnumerable<DayNote> notes, DateTime now, int days)
        => notes.Where(n => !n.Done && n.Day.Date >= now.Date && n.Day.Date <= now.Date.AddDays(days) && (n.Day.Date > now.Date || n.At is null || n.Day.Date + n.At.Value >= now.AddHours(-1)))
            .OrderBy(n => n.Day.Date).ThenBy(n => n.At ?? TimeSpan.FromDays(1)).ThenBy(n => n.Id).ToList();

    /// <summary>A task is a note with no time that is not a deadline: the kind that is ticked off, and the one the To-do list holds.</summary>
    public static bool IsTask(DayNote note) => note.At is null && !IsDeadline(note);
    /// <summary>A meeting is a note with both a start and a later end on the same day.</summary>
    public static bool IsMeeting(DayNote note) => note.At is { } start && note.End is { } end && end > start;
    /// <summary>When a meeting ends: its day at its end time, or null for a note that is not a meeting.</summary>
    public static DateTime? EndsAt(DayNote note) => IsMeeting(note) ? note.Day.Date + note.End!.Value : null;
    /// <summary>
    /// The to-do list: tasks dated today or earlier that are still open, so one not finished carries over to the next day, plus the ones ticked off today.
    /// Tasks dated later wait in Coming up until their day.
    /// </summary>
    public static List<DayNote> Todos(IEnumerable<DayNote> notes, DateTime now)
        => notes.Where(n => IsTask(n) && n.Day.Date <= now.Date && (!n.Done || n.Day.Date == now.Date)).OrderBy(n => n.Day.Date).ThenBy(n => n.Id).ToList();
    /// <summary>A note whose words start with ! is a deadline. The marker stays in the text, so it shows where it is edited.</summary>
    public static bool IsDeadline(DayNote note) => note.Text.StartsWith('!');
    /// <summary>The words without the deadline marker, for showing where the marker itself would be noise.</summary>
    public static string DisplayText(DayNote note) => note.Text.TrimStart('!', ' ');
    /// <summary>When a deadline falls due: its day at its time, or at 17:00 when it has none.</summary>
    public static DateTime DueAt(DayNote note) => note.Day.Date + (note.At ?? TimeSpan.FromHours(17));
    /// <summary>The deadline to watch: the soonest one not yet ticked off. One already past stays until it is done, so it is not forgotten.</summary>
    public static DayNote? NextDeadline(IEnumerable<DayNote> notes) => notes.Where(n => !n.Done && IsDeadline(n)).OrderBy(DueAt).ThenBy(n => n.Id).FirstOrDefault();
}
