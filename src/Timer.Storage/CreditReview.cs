using System.Globalization;
using Timer.Core;

namespace Timer.Storage;

public sealed record ReviewEntry(string Account, DateTime Day, DateTime? Start, DateTime? End,
    decimal Minutes, string Type, string Note, IReadOnlyList<SessionRow> Sources, string? GroupId = null);
public sealed record TidyGap(DateTime Start, DateTime End, string Previous, string Next);
public sealed record GapChoice(TidyGap Gap, string Account);

public sealed partial class LedgerEditor
{
    private static readonly string[] groupHeaders = { "GroupId", "Date", "Account", "Start", "End", "Minutes" };
    private static decimal Minutes(SessionRow row) => decimal.Parse(row.Minutes, CultureInfo.InvariantCulture);
    private static string Key(SessionRow row) => string.Join("\u001f", row.Date, row.Account, row.Start, row.End, row.Minutes);
    private static string Key(Dictionary<string, string> row) => string.Join("\u001f", groupHeaders.Skip(1).Select(h => row[h]));
    // CSV starts have whole-second precision and durations are rounded to 0.01
    // minutes. Permit only that sub-second representation error at a join.
    public static bool Adjoining(IReadOnlyList<SessionRow> rows)
    {
        if (rows.Count == 0 || rows.Any(s => s.Duration <= TimeSpan.Zero)) return false;
        var ordered = rows.OrderBy(s => s.From).ToArray();
        return ordered.Skip(1).Select((s, i) => Math.Abs((s.From - ordered[i].To).TotalSeconds)).All(gap => gap <= .6 + 1e-8);
    }
    public bool CanCombine(IReadOnlyList<SessionRow> rows)
    {
        if (rows.Count == 0 || rows.Any(s => s.Duration <= TimeSpan.Zero)) return false;
        var ordered = rows.OrderBy(s => s.From).ToArray();
        if (ordered.Skip(1).Select((s, i) => (s.From - ordered[i].To).TotalSeconds).Any(gap => gap < -.6 - 1e-8)) return false;
        var from = rows.Min(s => s.From); var until = rows.Max(s => s.To);
        return !engine.Sessions.Any(s => !rows.Any(r => ReferenceEquals(r, s)) && s.Duration > TimeSpan.Zero && s.From < until && s.To > from)
            && !(engine.Active is { } active && active.Start < until && engine.Now > from);
    }
    public IReadOnlyList<SessionRow> MergeGapRows(IReadOnlyList<SessionRow> rows)
    {
        var ordered = rows.OrderBy(s => s.From).ToArray(); var result = new List<SessionRow>();
        for (var i = 1; i < ordered.Length; i++)
        {
            var previous = ordered[i - 1]; var next = ordered[i];
            var start = previous.Day.AddSeconds(Math.Ceiling((previous.To - previous.Day).TotalSeconds - 1e-8));
            var minutes = decimal.Floor((decimal)(next.From - start).TotalMinutes * 100m + .00000001m) / 100m;
            if (minutes <= 0) continue;
            result.Add(new(previous.Date, previous.Account, start.ToString("HH:mm:ss"), "added", minutes.ToString("0.00", CultureInfo.InvariantCulture)));
        }
        return result;
    }
    public decimal MergeGapMinutes(IReadOnlyList<SessionRow> rows) => MergeGapRows(rows).Sum(Minutes);
    public void Group(IReadOnlyList<SessionRow> selected)
    {
        foreach (var row in selected) Index(row);
        if (selected.Count < 2 || selected.Select(Key).Distinct().Count() != selected.Count
            || selected.Any(s => s.Day != selected[0].Day || s.Account != selected[0].Account)
            || selected.Any(s => engine.Sessions.Count(r => Key(r) == Key(s)) != 1))
            throw new ArgumentException("Select at least two distinct entries for the same account and day.");
        if (!CanCombine(selected)) throw new ArgumentException("Merge blocks with only blank gaps between them. Overlapping or intervening time cannot be merged.");
        var table = CsvTable.Parse(groups ?? string.Join(",", groupHeaders) + "\n", groupHeaders);
        var keys = selected.Select(Key).ToHashSet();
        var oldGroups = table.Rows.Where(r => keys.Contains(Key(r))).Select(r => r["GroupId"]).ToHashSet();
        if (table.Rows.Any(r => oldGroups.Contains(r["GroupId"]) && !keys.Contains(Key(r))))
            throw new ArgumentException("Select every part of an existing group before merging it.");
        var fills = MergeGapRows(selected);
        if (fills.Any(row => engine.Sessions.Any(s => s.Date == row.Date && s.Start == row.Start && s.Account.Equals(row.Account, StringComparison.OrdinalIgnoreCase))))
            throw new ArgumentException("A gap fill would share an existing entry key. Edit that gap individually.");
        Change(fills.Count > 0 ? "Merge entries and fill gaps" : "Merge entries", () =>
        {
            table.Rows.RemoveAll(r => oldGroups.Contains(r["GroupId"]));
            var id = Guid.NewGuid().ToString("N");
            engine.Sessions.AddRange(fills);
            foreach (var fill in fills) SetNote(null, fill, "Added by merge");
            foreach (var row in selected.Concat(fills)) { var value = Fields(row); value["GroupId"] = id; table.Rows.Add(value); }
            groups = table.Serialize();
        });
    }
    public void Ungroup(string id)
    {
        var table = CsvTable.Parse(groups ?? string.Join(",", groupHeaders) + "\n", groupHeaders);
        if (!table.Rows.Any(r => r["GroupId"] == id)) throw new InvalidOperationException("This group changed. Reopen it.");
        Change("Ungroup entries", () => { table.Rows.RemoveAll(r => r["GroupId"] == id); groups = table.Serialize(); });
    }
    public IReadOnlyList<ReviewEntry> Entries(DateTime from, DateTime until)
    {
        var result = new List<ReviewEntry>();
        var relevant = engine.Sessions.Where(s => s.Day >= from.Date && s.Day < until.Date).ToArray();
        var used = new HashSet<SessionRow>(ReferenceEqualityComparer.Instance);
        // Old files are read lazily so unknown metadata remains untouched until a review/edit is requested.
        if (groups is not null)
        {
            var table = CsvTable.Parse(groups, groupHeaders);
            foreach (var group in table.Rows.GroupBy(r => r["GroupId"]))
            {
                var members = relevant.Where(s => group.Any(r => Key(r) == Key(s))).ToArray();
                if (members.Length < 2) continue;
                if (members.Any(s => s.Day != members[0].Day || s.Account != members[0].Account || used.Contains(s)))
                    throw new InvalidDataException("Invalid entry group: members must share an account and day and belong to one group.");
                result.Add(new(members[0].Account, members[0].Day, null, null, members.Sum(Minutes), "Grouped sessions",
                    string.Join("\n", members.Select(Note).Where(n => n.Length > 0).Distinct()), members, GroupId: group.Key));
                foreach (var member in members) used.Add(member);
            }
        }
        foreach (var row in relevant.Where(s => !used.Contains(s)))
            result.Add(new(row.Account, row.Day, row.From, row.To, Minutes(row),
                row.End == "edited" ? "Edited by hand" : row.End == "added" ? "Added by hand" : "Time", Note(row), new[] { row }));
        return result.OrderBy(r => r.Day).ThenBy(r => r.Account, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Start).ToArray();
    }
    public IReadOnlyList<TidyGap> TidyCandidates(DateTime day, int maximumMinutes = 5)
    {
        if (maximumMinutes is < 1 or > 60) throw new ArgumentException("Choose a gap limit of 1–60 minutes.");
        if (day.Date > engine.Now.Date) return Array.Empty<TidyGap>();
        var spans = engine.Sessions.Where(s => s.Day == day.Date && s.Duration > TimeSpan.Zero)
            .Select(s => (Start: s.From, End: s.To, s.Account, Live: false)).ToList();
        if (engine.Active is { } active && active.Start < day.Date.AddDays(1) && engine.Now > day.Date)
            spans.Add((active.Start, engine.Now, active.Account, true));
        var ordered = spans.OrderBy(s => s.Start).ToArray(); var result = new List<TidyGap>();
        for (var i = 1; i < ordered.Length; i++)
        {
            var previous = ordered[i - 1]; var next = ordered[i];
            if (previous.Live || next.Live || BreakAccount.Is(previous.Account)
                || BreakAccount.Is(next.Account) || next.Start > engine.Now
                || next.Start - previous.End < TimeSpan.FromSeconds(3) || next.Start - previous.End > TimeSpan.FromMinutes(maximumMinutes)
                || spans.Count(s => s.End == previous.End) != 1 || spans.Count(s => s.Start == next.Start) != 1
                || spans.Any(s => s.Start < next.Start && s.End > previous.End)
                || engine.Away.Any(a => a.Start < next.Start && (a.End ?? engine.Now) > previous.End)) continue;
            var start = day.Date.AddSeconds(Math.Ceiling((previous.End - day.Date).TotalSeconds - 1e-6));
            var end = day.Date.AddSeconds(Math.Floor((next.Start - day.Date).TotalSeconds + 1e-6));
            if ((end - start).TotalSeconds >= 3) result.Add(new(start, end, previous.Account, next.Account));
        }
        return result;
    }
    public void Tidy(IReadOnlyList<GapChoice> choices, int maximumMinutes = 5)
    {
        if (choices.Count == 0) return;
        var current = choices.Select(c => c.Gap.Start.Date).Distinct().SelectMany(d => TidyCandidates(d, maximumMinutes)).ToHashSet();
        if (choices.Select(c => c.Gap).Distinct().Count() != choices.Count || choices.Any(c => !current.Contains(c.Gap)
            || (c.Account != c.Gap.Previous && c.Account != c.Gap.Next)))
            throw new InvalidOperationException("The gaps changed. Reopen Tidy day; no time was added.");
        var rows = choices.Select(c => TimedRow(c.Account, c.Gap.Start,
            c.Gap.Start.AddSeconds(Math.Floor((c.Gap.End - c.Gap.Start).TotalSeconds / .6 + 1e-8) * .6), "added")).ToArray();
        foreach (var row in rows) ValidateInterval(row, null);
        Change("Tidy day", () => engine.Sessions.AddRange(rows));
    }
}
