using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Timer.Core;

/// <summary>
/// One item in the feed: a headline and some words, with a time. <see cref="Id"/> is what read and done marks are kept against, so it has to stay the
/// same when the same entry is read again from a rewritten file.
/// </summary>
/// <param name="Id">The author's own ID line when there is one, otherwise a fingerprint of the entry's date, headline and words.</param>
/// <param name="When">The entry's own date, or the file's last-written time (a little earlier for each place down the file) when it has none.</param>
/// <param name="HasDate">Whether the entry carried its own Date line.</param>
/// <param name="Urgent">Whether the headline began with a ! marker, which is dropped from <see cref="Headline"/>.</param>
public sealed record FeedEntry(string Id, string Headline, string Body, DateTime When, bool HasDate, bool Urgent, string Source);

/// <summary>
/// Reading a wire of updates written by something else (a scheduled assistant, a script, a person) into Markdown files. The app only ever reads, so a
/// file can be rewritten at any moment. An entry runs from a heading to the next heading. A Date: line (2026-10-16 or 2026-10-16 08:30) gives its time and an
/// ID: line gives it a name that survives edits; both are taken out of the words. Tags such as #AccountOne stay in the words and are linked to accounts
/// with <see cref="AccountTags"/> when shown. The text is treated as untrusted: it is only ever split into pieces, never run or rendered as HTML.
/// </summary>
public static class Feed
{
    private static readonly Regex Heading = new(@"^\s{0,3}#{1,6}\s+(.*?)\s*#*\s*$", RegexOptions.Compiled);
    private static readonly Regex DateLine = new(@"^\s*date\s*:\s*(.+?)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex IdLine = new(@"^\s*id\s*:\s*(.+?)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly string[] DateFormats = { "yyyy-MM-dd HH:mm", "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd" };

    /// <summary>A file bigger than this is skipped, so a runaway export cannot freeze or fill the app.</summary>
    public const long MaxFileBytes = 1_000_000;
    /// <summary>Only this many files are read from a folder; the rest are skipped with a note.</summary>
    public const int MaxFiles = 200;
    /// <summary>The folder size above which the settings note suggests clearing out old files by hand.</summary>
    public const long LargeFolderBytes = 50_000_000;
    /// <summary>Entries older than this many days are hidden unless the person chooses the longer limit.</summary>
    public const int DefaultRetentionDays = 30, LongRetentionDays = 90;

    /// <summary>Splits one file's text into entries. A file with no headings is a single entry named after the file.</summary>
    public static IReadOnlyList<FeedEntry> Parse(string text, string source, DateTime fileTime)
    {
        var lines = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var blocks = new List<(string? Headline, List<string> Lines)>();
        foreach (var line in lines)
        {
            if (Heading.Match(line) is { Success: true } h && h.Groups[1].Value.Length > 0) blocks.Add((h.Groups[1].Value, new List<string>()));
            else
            {
                if (blocks.Count == 0) blocks.Add((null, new List<string>()));
                blocks[^1].Lines.Add(line);
            }
        }
        var entries = new List<FeedEntry>();
        foreach (var (headline, body) in blocks)
        {
            string? id = null; DateTime? date = null; var kept = new List<string>();
            foreach (var line in body)
            {
                if (id is null && IdLine.Match(line) is { Success: true } i) { id = i.Groups[1].Value.Trim().ToLowerInvariant(); continue; }
                if (date is null && DateLine.Match(line) is { Success: true } d && ParseDate(d.Groups[1].Value) is { } when) { date = when; continue; }
                kept.Add(line);
            }
            var words = string.Join('\n', kept).Trim('\n', ' ', '\t').TrimEnd();
            if (headline is null && words.Length == 0 && date is null && id is null) continue; // Text before the first heading that is only blank.
            var title = (headline ?? source).Trim();
            var urgent = title.StartsWith('!');
            if (urgent) title = title.TrimStart('!', ' ');
            if (title.Length == 0) title = source;
            var position = entries.Count;
            var entryTime = date ?? fileTime.AddSeconds(-position); // Earlier in a file counts as newer, as in a file written newest first.
            var key = id is not null ? "id:" + id : "h:" + Fingerprint(date, title, words);
            entries.Add(new FeedEntry(key, title, words, entryTime, date is not null, urgent, source));
        }
        return entries;
    }

    private static DateTime? ParseDate(string text) =>
        DateTime.TryParseExact(text.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value) ? value : null;

    private static string Fingerprint(DateTime? date, string headline, string body)
    {
        var normal = string.Join('\n', body.Split('\n').Select(l => l.TrimEnd()));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes((date?.Ticks.ToString(CultureInfo.InvariantCulture) ?? "") + "\n" + headline + "\n" + normal));
        return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
    }

    /// <summary>
    /// Joins entries from every file into one stream, newest first. An entry that appears again with the same ID (a task that rewrote its whole file)
    /// counts once, keeping the version with the later time. Ties keep their order, so the result does not shuffle between reads.
    /// </summary>
    public static IReadOnlyList<FeedEntry> Merge(IEnumerable<FeedEntry> entries)
    {
        var best = new Dictionary<string, (FeedEntry Entry, int Order)>();
        var order = 0;
        foreach (var entry in entries)
        {
            order++;
            if (!best.TryGetValue(entry.Id, out var seen) || entry.When > seen.Entry.When) best[entry.Id] = (entry, seen.Entry is null ? order : seen.Order);
        }
        return best.Values.OrderByDescending(v => v.Entry.When).ThenBy(v => v.Order).Select(v => v.Entry).ToList();
    }

    /// <summary>Whether an entry is recent enough to show. Hiding is all it does: nothing in the folder is ever changed or deleted.</summary>
    public static bool Within(FeedEntry entry, DateTime now, int days) => entry.When >= now.AddDays(-Math.Max(1, days));

    /// <summary>The retention limit in days from a saved choice: 90 when the longer limit is chosen, otherwise 30.</summary>
    public static int RetentionDays(bool longer) => longer ? LongRetentionDays : DefaultRetentionDays;

    /// <summary>What was read from a folder, with the counts the settings note reports.</summary>
    /// <param name="Entries">Every entry from every file, merged, newest first, before any retention limit is applied.</param>
    /// <param name="Files">How many source files were read.</param>
    /// <param name="Bytes">The size of all the source files in the folder (read or skipped).</param>
    /// <param name="Skipped">Files not read, each with the reason in plain words.</param>
    public sealed record Scan(IReadOnlyList<FeedEntry> Entries, int Files, long Bytes, IReadOnlyList<string> Skipped);

    /// <summary>
    /// Reads the .md and .txt files directly in a folder (not its subfolders). A missing folder is simply empty. A file that is too large, or that cannot
    /// be read, is skipped and named in <see cref="Scan.Skipped"/>; the others are still read.
    /// </summary>
    public static Scan ScanFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return new Scan(Array.Empty<FeedEntry>(), 0, 0, Array.Empty<string>());
        var all = new List<FeedEntry>(); var skipped = new List<string>(); long bytes = 0; var read = 0;
        IEnumerable<FileInfo> files;
        try
        {
            files = new DirectoryInfo(folder).EnumerateFiles().Where(f => f.Extension.Equals(".md", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new Scan(Array.Empty<FeedEntry>(), 0, 0, new[] { "The folder could not be read." }); }
        foreach (var file in files)
        {
            bytes += file.Length;
            if (read >= MaxFiles) { skipped.Add($"{file.Name}: more than {MaxFiles} files, so the rest are not read."); continue; }
            if (file.Length > MaxFileBytes) { skipped.Add($"{file.Name}: larger than 1 MB."); continue; }
            try { all.AddRange(Parse(File.ReadAllText(file.FullName), Path.GetFileNameWithoutExtension(file.Name), file.LastWriteTime)); read++; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { skipped.Add($"{file.Name}: could not be read."); }
        }
        return new Scan(Merge(all), read, bytes, skipped);
    }

    /// <summary>The settings note on a feed folder: its size and files, how many entries show and how many the age limit hides.</summary>
    public static string Describe(Scan scan, int showing, int hidden, int days)
    {
        var size = scan.Bytes >= 1_000_000 ? (scan.Bytes / 1_000_000.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB" : Math.Max(1, scan.Bytes / 1000) + " KB";
        if (scan.Files == 0 && scan.Bytes == 0) return "No feed files found in the folder.";
        var line = $"{size} in {scan.Files} {(scan.Files == 1 ? "file" : "files")}. {showing} {(showing == 1 ? "entry" : "entries")} showing, {hidden} hidden as older than {days} days.";
        return scan.Bytes >= LargeFolderBytes ? line + " The folder is large; old files can be cleared out by hand." : line;
    }
}
