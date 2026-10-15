using System.Security.Cryptography;
using System.Text;
using Timer.Core;

namespace Timer.Storage;

// An edit changes one stretch of a list. A splice keeps only that stretch: where it starts, what was there
// before, and how many items replaced it. The untouched items before and after it are not stored, because
// Undo can read them from the live list. Memory and disk cost then follow the size of the edit, not the size
// of the ledger.
public sealed record Splice<T>(int Start, List<T> Before, int AfterLength);

// A text file (notes or groups) as a splice over its lines. A missing file is not the same as an
// empty one, so whether it existed before the edit is kept too.
public sealed record TextChange(bool BeforeIsNull, Splice<string> Lines);

// One reversible edit. The hashes describe the state the edit left behind, so Undo is only offered while
// the ledger, notes and groups still match it.
public sealed record UndoStep(string Description, int AfterCount, string AfterHash,
    Splice<Dictionary<string, string>> Rows, TextChange Notes, TextChange Groups,
    string AfterNotesHash, string AfterGroupsHash);

internal static class UndoMath
{
    public static readonly string[] Core = { "Date", "Account", "Start", "End", "Minutes" };
    // A missing groups file and an empty one are the same thing once the store has saved and reloaded.
    public const string EmptyGroups = "GroupId,Date,Account,Start,End,Minutes\n";

    public static bool SameFields(Dictionary<string, string> a, Dictionary<string, string> b)
        => a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var v) && v == p.Value);
    public static bool SameCore(Dictionary<string, string> a, Dictionary<string, string> b)
        => Core.All(k => a.GetValueOrDefault(k) == b.GetValueOrDefault(k));

    // The unchanged head and tail are dropped; what is left in the middle is the whole change.
    public static Splice<T> Diff<T>(IReadOnlyList<T> before, IReadOnlyList<T> after, Func<T, T, bool> same)
    {
        var head = 0;
        while (head < before.Count && head < after.Count && same(before[head], after[head])) head++;
        var tail = 0;
        while (tail < before.Count - head && tail < after.Count - head && same(before[before.Count - 1 - tail], after[after.Count - 1 - tail])) tail++;
        return new(head, before.Skip(head).Take(before.Count - head - tail).ToList(), after.Count - head - tail);
    }

    // `current` must be the state the splice was taken from (the state after the edit).
    public static List<T> Revert<T>(IReadOnlyList<T> current, Splice<T> splice)
    {
        var result = new List<T>(current.Take(splice.Start));
        result.AddRange(splice.Before);
        result.AddRange(current.Skip(splice.Start + splice.AfterLength));
        return result;
    }

    private static string[] Lines(string? text) => text is null ? Array.Empty<string>() : text.Split('\n');
    public static TextChange DiffText(string? before, string? after) => new(before is null, Diff(Lines(before), Lines(after), string.Equals));
    public static string? RevertText(string? current, TextChange change)
        => change.BeforeIsNull ? null : string.Join("\n", Revert(Lines(current), change.Lines));

    // The five fields that identify a credited row, which is also what the previous design compared.
    public static string HashSessions(IReadOnlyList<SessionRow> rows, int count)
    {
        var text = new StringBuilder();
        for (var i = 0; i < count; i++)
        { var r = rows[i]; text.Append(r.Date).Append('\u001f').Append(r.Account).Append('\u001f').Append(r.Start).Append('\u001f').Append(r.End).Append('\u001f').Append(r.Minutes).Append('\u001f').Append('\u001e'); }
        return Hash(text.ToString());
    }
    public static string HashRows(IReadOnlyList<Dictionary<string, string>> rows, int count)
    {
        var text = new StringBuilder();
        for (var i = 0; i < count; i++)
        { foreach (var key in Core) text.Append(rows[i][key]).Append('\u001f'); text.Append('\u001e'); }
        return Hash(text.ToString());
    }
    public static string HashText(string? text) => Hash(text ?? "\u0000none");
    public static string HashGroups(string? groups) => HashText(groups ?? EmptyGroups);
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
