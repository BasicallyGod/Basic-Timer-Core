using System.Globalization;
using System.Text.Json;
using Microsoft.VisualBasic.FileIO;
using Timer.Core;

namespace Timer.Storage;

// Sessions remain the credited ledger. Extra CSV fields and reversible edits
// travel with it; Undo restores the edited stretch and retains later recordings.
public sealed partial class LedgerEditor
{
    private readonly TrackingEngine engine;
    private readonly string[] headers;
    private readonly Dictionary<SessionRow, Dictionary<string, string>> raw = new(ReferenceEqualityComparer.Instance);
    private CsvTable notes;
    private string? groups;
    // Most recent step last. Each step stores only what its edit changed.
    private readonly List<UndoStep> history = new();
    private readonly Dictionary<UndoStep, int> stepBytes = new(ReferenceEqualityComparer.Instance);
    // Steps taken back by Undo, most recent last, each stored as the edit that would put it back. They are kept in memory only, and any new edit ends them.
    private readonly List<UndoStep> redo = new();
    public const int MaxUndoSteps = 50;
    // A few very large edits could still add up, so the saved history has a size limit as well.
    public const int MaxHistoryBytes = 2_000_000;
    private readonly int historyBudget;
    public UndoStep? UndoState => history.LastOrDefault();
    public int UndoCount => history.Count;
    public bool Changed { get; private set; }
    // Offered only while the ledger, notes, and groups still match what the last edit left behind.
    public bool CanUndo => UndoState is { } u && Matches(u);
    public string? UndoLabel => CanUndo ? UndoState?.Description : null;
    public UndoStep? RedoState => redo.LastOrDefault();
    public int RedoCount => redo.Count;
    // Offered only while the ledger, notes, and groups still match what the last Undo left behind.
    public bool CanRedo => RedoState is { } r && Matches(r);
    public string? RedoLabel => CanRedo ? RedoState?.Description : null;
    private bool Matches(UndoStep step) => engine.Sessions.Count >= step.AfterCount
        && UndoMath.HashSessions(engine.Sessions, step.AfterCount) == step.AfterHash
        && UndoMath.HashText(NotesCsv) == step.AfterNotesHash
        && UndoMath.HashGroups(groups) == step.AfterGroupsHash;
    public LedgerEditor(TrackingEngine engine, string[]? headers = null, IEnumerable<Dictionary<string, string>>? rows = null,
        string? notesCsv = null, string? groupsCsv = null, int maxHistoryBytes = MaxHistoryBytes, IEnumerable<UndoStep>? steps = null)
    {
        this.engine = engine; historyBudget = maxHistoryBytes; this.headers = headers ?? new[] { "Date", "Account", "Start", "End", "Minutes" };
        if (rows is not null) foreach (var pair in engine.Sessions.Zip(rows)) raw[pair.First] = new(pair.Second);
        notes = CsvTable.Parse(notesCsv ?? "Date,Account,Start,Note\n", new[] { "Date", "Account", "Start", "Note" });
        groups = groupsCsv;
        if (steps is not null)
        {
            foreach (var step in steps.TakeLast(MaxUndoSteps)) { CheckStep(step); history.Add(step); }
            TrimHistory();
        }
    }
    /// <summary>The undo history, oldest first. Steps never change once made, so the store can tell which ones it has already saved.</summary>
    public IReadOnlyList<UndoStep> Steps => history;
    private static void CheckStep(UndoStep? step)
    {
        if (step is null || step.Rows is null || step.Notes is null || step.Groups is null
            || step.Rows.Before is null || step.Rows.Start < 0 || step.Rows.AfterLength < 0
            || step.Rows.Start + step.Rows.AfterLength > step.AfterCount
            || step.Rows.Before.Any(r => r is null || UndoMath.Core.Any(k => !r.ContainsKey(k))))
            throw new InvalidDataException("Invalid edit history.");
    }
    private int Bytes(UndoStep step)
    {
        if (!stepBytes.TryGetValue(step, out var size)) stepBytes[step] = size = JsonSerializer.SerializeToUtf8Bytes(step).Length;
        return size;
    }
    // Keep at most 50 steps and about 2 MB of history, dropping the oldest first. The newest step always stays.
    private void TrimHistory()
    {
        while (history.Count > MaxUndoSteps) history.RemoveAt(0);
        while (history.Count > 1 && history.Sum(Bytes) > historyBudget) history.RemoveAt(0);
        foreach (var gone in stepBytes.Keys.Where(k => !history.Contains(k)).ToList()) stepBytes.Remove(gone);
    }
    private static bool SameKey(Dictionary<string, string> item, SessionRow row) =>
        item["Date"] == row.Date && item["Start"] == row.Start && item["Account"].Equals(row.Account, StringComparison.OrdinalIgnoreCase);
    /// <summary>Forgets the notes of entries that no longer exist, so one cannot reappear on a new entry that starts at the same time.</summary>
    public void DropNotes(IEnumerable<(string Account, string Day, string Start)> entries)
    {
        var keys = entries.Select(e => (e.Day, e.Account.ToLowerInvariant(), e.Start)).ToHashSet();
        if (notes.Rows.RemoveAll(n => keys.Contains((n["Date"], n["Account"].ToLowerInvariant(), n["Start"]))) > 0) Changed = true;
    }
    public string Note(SessionRow row) => notes.Rows.LastOrDefault(n => SameKey(n, row))?.GetValueOrDefault("Note", "") ?? "";
    public List<Dictionary<string, string>> Rows() => engine.Sessions.Select(Fields).ToList();
    private Dictionary<string, string> Fields(SessionRow row)
    {
        var fields = raw.TryGetValue(row, out var original) ? new Dictionary<string, string>(original) : new Dictionary<string, string>();
        fields["Date"] = row.Date; fields["Account"] = row.Account; fields["Start"] = row.Start;
        fields["End"] = row.End; fields["Minutes"] = row.Minutes;
        return fields;
    }
    public string SessionsCsv => new CsvTable(headers, Rows()).Serialize();
    public string NotesCsv => notes.Serialize();
    public string? GroupsCsv => groups;
    private int Index(SessionRow row)
    {
        var index = engine.Sessions.FindIndex(s => ReferenceEquals(s, row));
        if (index < 0) throw new InvalidOperationException("This entry changed. Reopen it before editing.");
        return index;
    }
    public static SessionRow TimedRow(string account, DateTime start, DateTime end, string origin)
    {
        if (string.IsNullOrWhiteSpace(account) || end <= start || (end - start).TotalSeconds < 3
            || end > start.Date.AddDays(1)) throw new ArgumentException("Use an interval of at least three seconds within one day.");
        return new(start.ToString("yyyy-MM-dd"), account, start.ToString("HH:mm:ss"), origin,
            Math.Round((end - start).TotalMinutes, 2, MidpointRounding.ToEven).ToString("0.00", CultureInfo.InvariantCulture));
    }
    // Overlapping recorded blocks can be allowed on purpose (the same hours billed to two accounts), but a block never runs into the one being recorded now.
    private void ValidateInterval(SessionRow row, SessionRow? except, bool allowOverlap = false)
    {
        if ((!allowOverlap && engine.Sessions.Any(s => !ReferenceEquals(s, except) && s.Duration > TimeSpan.Zero && s.From < row.To && s.To > row.From))
            || (engine.Active is { } active && active.Start < row.To && engine.Now > row.From))
            throw new ArgumentException(allowOverlap ? "This runs into the time being recorded now. Stop tracking first, or choose another end." : "This interval overlaps recorded or running time. Choose a free gap.");
    }
    private void CheckNoteKey(SessionRow row)
    {
        if (engine.Sessions.Count(s => s.Date == row.Date && s.Start == row.Start && s.Account.Equals(row.Account, StringComparison.OrdinalIgnoreCase)) > 1)
            throw new ArgumentException("These entries share a legacy note key. They cannot be edited individually here.");
    }
    private void SetNote(SessionRow? old, SessionRow row, string text)
    {
        var fields = old is null ? null : notes.Rows.LastOrDefault(n => SameKey(n, old));
        var value = fields is null ? new Dictionary<string, string>() : new Dictionary<string, string>(fields);
        if (old is not null) notes.Rows.RemoveAll(n => SameKey(n, old));
        notes.Rows.RemoveAll(n => SameKey(n, row));
        if (!string.IsNullOrWhiteSpace(text))
        { value["Date"] = row.Date; value["Account"] = row.Account; value["Start"] = row.Start; value["Note"] = text.Trim(); notes.Rows.Add(value); }
    }
    // Whether the ledger, as it was before a new edit, is still what the last step left behind.
    private static bool LedgerMatches(UndoStep step, List<Dictionary<string, string>> rows)
        => rows.Count >= step.AfterCount && UndoMath.HashRows(rows, step.AfterCount) == step.AfterHash;
    private void Change(string description, Action mutation)
    {
        var before = Rows(); var beforeNotes = NotesCsv; var beforeGroups = groups;
        // Validate group metadata before touching the ledger; keep unrelated fields.
        CsvTable? table;
        try { table = groups is null ? null : CsvTable.Parse(groups, new[] { "GroupId", "Date", "Account", "Start", "End", "Minutes" }); }
        catch (MalformedLineException error) { throw new InvalidDataException("Could not read entry-group metadata; no edit was applied.", error); }
        mutation();
        if (table is not null && groups == beforeGroups)
        {
            var present = engine.Sessions.Select(s => string.Join("\u001f", s.Date, s.Account, s.Start, s.End, s.Minutes)).ToHashSet(StringComparer.Ordinal);
            table.Rows.RemoveAll(r => !present.Contains(string.Join("\u001f", r["Date"], r["Account"], r["Start"], r["End"], r["Minutes"])));
            var singletons = table.Rows.GroupBy(r => r["GroupId"]).Where(g => g.Count() < 2).Select(g => g.Key).ToHashSet();
            table.Rows.RemoveAll(r => singletons.Contains(r["GroupId"]));
            groups = table.Serialize();
        }
        redo.Clear(); // A new edit is a new line of history, so what was taken back can no longer be put back.
        if (history.Count > 0 && !LedgerMatches(history[^1], before)) history.Clear();
        var notesNow = NotesCsv;
        history.Add(new(description, engine.Sessions.Count, UndoMath.HashSessions(engine.Sessions, engine.Sessions.Count),
            UndoMath.Diff(before, Rows(), UndoMath.SameFields),
            UndoMath.DiffText(beforeNotes, notesNow), UndoMath.DiffText(beforeGroups, groups),
            UndoMath.HashText(notesNow), UndoMath.HashGroups(groups)));
        TrimHistory();
        Changed = true;
    }
    public void Edit(SessionRow old, string account, DateTime start, DateTime end, string note, bool allowOverlap = false)
    {
        var index = Index(old);
        var row = old;
        if (account != old.Account || start != old.From || end != old.To)
        { row = TimedRow(account, start, end, "edited"); ValidateInterval(row, old, allowOverlap); }
        CheckNoteKey(old);
        if (engine.Sessions.Any(s => !ReferenceEquals(s, old) && s.Date == row.Date && s.Start == row.Start && s.Account.Equals(row.Account, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Another entry uses that account and start time.");
        if (row == old && Note(old) == note.Trim()) return;
        Change("Edit entry", () => { raw[row] = Fields(old); engine.Sessions[index] = row; SetNote(old, row, note); });
    }
    public void Add(string account, DateTime start, DateTime end, string note)
    {
        var row = TimedRow(account, start, end, "added"); ValidateInterval(row, null);
        if (engine.Sessions.Any(s => s.Date == row.Date && s.Start == row.Start && s.Account.Equals(row.Account, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Another entry uses that account and start time.");
        Change("Add time", () => { engine.Sessions.Add(row); SetNote(null, row, note); });
    }
    /// <summary>One block taken for copying: where it began relative to the first block copied, its credited minutes exactly as written, and its note.</summary>
    public sealed record ClipBlock(string Account, TimeSpan Offset, string Minutes, string Note);
    /// <summary>
    /// Adds copies of the blocks with the first one starting at <paramref name="anchor"/>, keeping each block's account, credited length, note and
    /// gaps between them, as one undoable step. Everything is checked before anything is added, so a paste is whole or not at all.
    /// Like Add time it adds credited time, and it refuses a place that overlaps recorded or running time or runs past midnight.
    /// </summary>
    public void Paste(IReadOnlyList<ClipBlock> blocks, DateTime anchor)
    {
        if (blocks.Count == 0) throw new ArgumentException("There is nothing copied to paste.");
        anchor = new DateTime(anchor.Year, anchor.Month, anchor.Day, anchor.Hour, anchor.Minute, anchor.Second);
        var added = new List<(SessionRow Row, string Note)>();
        foreach (var block in blocks)
        {
            var start = anchor + block.Offset;
            var row = new SessionRow(start.ToString("yyyy-MM-dd"), block.Account, start.ToString("HH:mm:ss"), "added", block.Minutes);
            if (row.Duration <= TimeSpan.Zero || row.To > start.Date.AddDays(1)) throw new ArgumentException("The copied time does not fit before midnight from here. Choose an earlier time.");
            ValidateInterval(row, null);
            if (added.Any(a => a.Row.From < row.To && a.Row.To > row.From) || engine.Sessions.Any(x => x.Date == row.Date && x.Start == row.Start && x.Account.Equals(row.Account, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("This overlaps recorded or running time. Choose a free gap.");
            added.Add((row, block.Note));
        }
        Change(added.Count == 1 ? "Paste block" : "Paste blocks", () => { foreach (var (row, note) in added) { engine.Sessions.Add(row); SetNote(null, row, note); } });
    }
    public void Delete(SessionRow row)
    {
        var index = Index(row); CheckNoteKey(row);
        Change("Delete entry", () => { engine.Sessions.RemoveAt(index); notes.Rows.RemoveAll(n => SameKey(n, row)); });
    }
    /// <summary>Moves every past entry, note and group from one account name to another.
    /// The new name must not already appear in the history, so two accounts can never be combined by accident.
    /// Earlier Undo steps would put the old name back under a new one, so the history starts afresh.</summary>
    public int RenameAccount(string oldName, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName) || newName != newName.Trim()) throw new ArgumentException("Choose a name for the account.");
        bool Is(string name) => name.Equals(oldName, StringComparison.OrdinalIgnoreCase);
        if (!newName.Equals(oldName, StringComparison.OrdinalIgnoreCase) && (engine.Sessions.Any(r => r.Account.Equals(newName, StringComparison.OrdinalIgnoreCase))
            || notes.Rows.Any(r => r["Account"].Equals(newName, StringComparison.OrdinalIgnoreCase))))
            throw new ArgumentException($"\"{newName}\" is already used in the history.");
        if (engine.Active is { } active && Is(active.Account)) throw new InvalidOperationException("Stop tracking this account before renaming it.");
        var count = engine.Sessions.Count(r => Is(r.Account));
        if (count == 0 && !notes.Rows.Any(r => Is(r["Account"]))) return 0;
        Change("Rename account", () =>
        {
            for (var i = 0; i < engine.Sessions.Count; i++)
            {
                var old = engine.Sessions[i];
                if (!Is(old.Account)) continue;
                var row = old with { Account = newName };
                raw[row] = Fields(old); raw[row]["Account"] = newName;
                engine.Sessions[i] = row;
            }
            foreach (var item in notes.Rows.Where(r => Is(r["Account"]))) item["Account"] = newName;
            if (groups is not null)
            {
                var table = CsvTable.Parse(groups, groupHeaders);
                foreach (var item in table.Rows.Where(r => Is(r["Account"]))) item["Account"] = newName;
                groups = table.Serialize();
            }
        });
        history.Clear(); redo.Clear(); stepBytes.Clear();
        return count;
    }
    /// <summary>
    /// Removes an account's whole history: its entries, their notes and its groups. Earlier Undo steps would bring back entries for an account that
    /// no longer exists, so the history starts afresh, as it does for a rename. Returns how many entries went.
    /// </summary>
    public int DeleteAccount(string name)
    {
        bool Is(string other) => other.Equals(name, StringComparison.OrdinalIgnoreCase);
        if (engine.Active is { } active && Is(active.Account)) throw new InvalidOperationException("Stop tracking this account before deleting it.");
        var count = engine.Sessions.Count(r => Is(r.Account));
        if (count == 0 && !notes.Rows.Any(r => Is(r["Account"])) && (groups is null || !CsvTable.Parse(groups, groupHeaders).Rows.Any(r => Is(r["Account"])))) return 0;
        Change("Delete account", () =>
        {
            engine.Sessions.RemoveAll(r => Is(r.Account));
            notes.Rows.RemoveAll(r => Is(r["Account"]));
            if (groups is not null)
            {
                var table = CsvTable.Parse(groups, groupHeaders);
                table.Rows.RemoveAll(r => Is(r["Account"]));
                groups = table.Serialize();
            }
        });
        history.Clear(); redo.Clear(); stepBytes.Clear();
        return count;
    }
    /// <summary>Moves entries to another account, with their notes, as one edit. Times do not change, so nothing can overlap.</summary>
    public void Reassign(IReadOnlyList<SessionRow> rows, string account)
    {
        if (rows.Count == 0) return;
        if (string.IsNullOrWhiteSpace(account) || account != account.Trim()) throw new ArgumentException("Choose an account.");
        var moving = rows.Where(r => !r.Account.Equals(account, StringComparison.OrdinalIgnoreCase)).ToList();
        if (moving.Count == 0) return;
        foreach (var row in moving)
        {
            Index(row); CheckNoteKey(row);
            if (engine.Sessions.Any(s => !moving.Any(m => ReferenceEquals(m, s)) && s.Date == row.Date && s.Start == row.Start && s.Account.Equals(account, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Another entry for that account starts at the same time.");
        }
        var keys = moving.Select(r => (r.Date, r.Start)).ToList();
        if (keys.Distinct().Count() != keys.Count) throw new ArgumentException("Two of these entries start at the same time, so they cannot move to one account.");
        var notesBefore = moving.Select(Note).ToList();
        Change(moving.Count == 1 ? "Move to " + BreakAccount.Display(account) : $"Move {moving.Count} entries to {BreakAccount.Display(account)}", () =>
        {
            for (var i = 0; i < moving.Count; i++)
            {
                var old = moving[i]; var index = Index(old);
                var row = old with { Account = account };
                raw[row] = Fields(old); raw[row]["Account"] = account;
                engine.Sessions[index] = row;
                notes.Rows.RemoveAll(n => SameKey(n, old));
                SetNote(null, row, notesBefore[i]);
            }
        });
    }
    /// <summary>Deletes several entries as one edit, so a single Undo brings them all back.</summary>
    public void Delete(IReadOnlyList<SessionRow> rows)
    {
        if (rows.Count == 0) return;
        foreach (var row in rows)
        {
            Index(row); CheckNoteKey(row);
            }
        Change(rows.Count == 1 ? "Delete entry" : "Delete entries", () =>
        {
            engine.Sessions.RemoveAll(s => rows.Any(r => ReferenceEquals(r, s)));
            notes.Rows.RemoveAll(n => rows.Any(r => SameKey(n, r)));
        });
    }
    public void Undo()
    {
        if (!CanUndo) throw new InvalidOperationException("No matching edit is available to undo.");
        var undo = UndoState!;
        var inverse = Apply(undo);
        history.RemoveAt(history.Count - 1); redo.Add(inverse); TrimHistory(); Changed = true;
    }
    /// <summary>Puts back the edit that Undo last took back. Like Undo it is offered only while nothing else has changed since, and a new edit ends it.</summary>
    public void Redo()
    {
        if (!CanRedo) throw new InvalidOperationException("No matching edit is available to redo.");
        var again = RedoState!;
        var inverse = Apply(again);
        redo.RemoveAt(redo.Count - 1); history.Add(inverse); TrimHistory(); Changed = true;
    }
    // Reverts one step and returns the step that reverts that, so Undo and Redo are the same move in opposite directions.
    private UndoStep Apply(UndoStep undo)
    {
        var start = undo.Rows.Start; var after = start + undo.Rows.AfterLength;
        var beforeRows = Rows(); var beforeNotes = NotesCsv; var beforeGroups = groups;
        // Rows the edit did not touch keep their objects. Only the changed stretch is rebuilt, and everything is
        // checked before anything is replaced, so a bad history leaves the ledger as it was.
        var restoredRows = new List<(SessionRow Row, Dictionary<string, string> Fields)>();
        foreach (var fields in undo.Rows.Before)
        {
            var row = new SessionRow(fields["Date"], fields["Account"], fields["Start"], fields["End"], fields["Minutes"]);
            _ = row.From; _ = row.To;
            restoredRows.Add((row, new(fields)));
        }
        var restoredNotes = CsvTable.Parse(UndoMath.RevertText(NotesCsv, undo.Notes) ?? "Date,Account,Start,Note\n", new[] { "Date", "Account", "Start", "Note" });
        var restoredGroups = UndoMath.RevertText(groups, undo.Groups);
        var tail = engine.Sessions.Skip(undo.AfterCount).ToArray();
        var rows = new List<SessionRow>(engine.Sessions.Take(start));
        foreach (var item in restoredRows) { raw[item.Row] = item.Fields; rows.Add(item.Row); }
        rows.AddRange(engine.Sessions.Skip(after).Take(undo.AfterCount - after));
        engine.Sessions.Clear(); engine.Sessions.AddRange(rows); engine.Sessions.AddRange(tail);
        notes = restoredNotes; groups = restoredGroups;
        var notesNow = NotesCsv;
        return new(undo.Description, engine.Sessions.Count, UndoMath.HashSessions(engine.Sessions, engine.Sessions.Count),
            UndoMath.Diff(beforeRows, Rows(), UndoMath.SameFields),
            UndoMath.DiffText(beforeNotes, notesNow), UndoMath.DiffText(beforeGroups, groups),
            UndoMath.HashText(notesNow), UndoMath.HashGroups(groups));
    }
}

internal sealed record CsvTable(string[] Headers, List<Dictionary<string, string>> Rows)
{
    public static CsvTable Parse(string content, string[] required)
    {
        using var parser = new TextFieldParser(new StringReader(content)) { HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
        parser.SetDelimiters(",");
        var headers = parser.ReadFields() ?? throw new InvalidDataException("Missing metadata CSV header.");
        if (required.Except(headers).Any() || headers.Distinct().Count() != headers.Length) throw new InvalidDataException("Invalid metadata CSV header.");
        var rows = new List<Dictionary<string, string>>();
        while (!parser.EndOfData)
        {
            var fields = parser.ReadFields()!;
            if (fields.Length != headers.Length) throw new InvalidDataException("Invalid metadata CSV row.");
            rows.Add(headers.Zip(fields).ToDictionary(p => p.First, p => p.Second));
        }
        return new(headers, rows);
    }
    public string Serialize()
    {
        string Quote(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        return string.Join(",", Headers.Select(Quote)) + "\r\n" + string.Concat(Rows.Select(r => string.Join(",", Headers.Select(h => Quote(r.GetValueOrDefault(h, "")))) + "\r\n"));
    }
}
