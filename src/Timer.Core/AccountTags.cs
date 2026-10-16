using System.Text.RegularExpressions;

namespace Timer.Core;

/// <summary>
/// The #Tag rule in one place, shared by to-dos, notes and the feed so one shorthand means one thing everywhere. A tag names an account by its name
/// with the spaces taken out, in any case (#AccountOne for "Account one"), or by the start of one when only a single account begins that way.
/// </summary>
public static class AccountTags
{
    private static readonly Regex Tag = new(@"(?:^|\s)#([\p{L}\p{N}_-]+)", RegexOptions.Compiled);

    /// <summary>
    /// Finds the first #Tag in some words that names exactly one account, with where the tag sits. A tag that names no account, or more than one,
    /// is left alone, so nothing is ever linked by guesswork. The reserved Break account is never a target.
    /// </summary>
    public static (string Account, int Index, int Length, string Tag)? Find(string typed, IEnumerable<string> accounts)
    {
        var names = accounts.Where(n => !BreakAccount.Is(n)).ToList();
        foreach (Match match in Tag.Matches(typed))
        {
            var tag = match.Groups[1].Value;
            string Squash(string name) => new string(name.Where(c => !char.IsWhiteSpace(c)).ToArray());
            var exact = names.Where(n => Squash(n).Equals(tag, StringComparison.OrdinalIgnoreCase)).ToList();
            var starts = exact.Count > 0 ? exact : names.Where(n => Squash(n).StartsWith(tag, StringComparison.OrdinalIgnoreCase)).ToList();
            if (starts.Count == 1) return (starts[0], match.Index, match.Length, tag);
        }
        return null;
    }
}
