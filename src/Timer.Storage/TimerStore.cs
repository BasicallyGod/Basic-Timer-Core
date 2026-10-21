using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Timer.Core;

namespace Timer.Storage;

public sealed record AccountSettings(string Name, double? BudgetHours = null, string BudgetPeriod = "week", bool Hidden = false, string? Colour = null);
/// <summary>
/// What the timer saves besides its time: the accounts, the theme and the few preferences every timer has. An app built on this library keeps its own
/// settings in <see cref="Extras"/>, which the store carries through unread, so the library does not grow a field for every feature an app adds.
/// </summary>
public sealed record StoreSettings(List<AccountSettings> Accounts, bool DecimalHours, string Theme, DayOfWeek WeekStart, bool Shadows = true, IReadOnlyList<ThemeDefinition>? Themes = null, bool TaskbarButton = true, bool Tooltips = true, bool Developer = false, bool SetupDone = true, bool MoveLaterAlways = false, int? PillScale = null, bool PillOnTop = true, bool DashboardOnTop = true, SettingsExtras? Extras = null);

/// <summary>
/// The settings an app adds for itself: lines in the appearance section that the library does not own, and whole sections of the settings file. They are read
/// as they were written and written back as they are, so the library never needs to know what they mean.
/// </summary>
public sealed record SettingsExtras(IReadOnlyList<string> AppearanceLines, IReadOnlyDictionary<string, IReadOnlyList<string>> Sections)
{
    public static readonly SettingsExtras Empty = new(System.Array.Empty<string>(), new Dictionary<string, IReadOnlyList<string>>());
}

// The timer's records live in one SQLite file (timer.db). Preferences stay in the settings JSON beside it.
// The in-memory model is unchanged: Load fills the engine from the database and Save writes it back in one transaction.
public sealed class TimerStore : IDisposable
{
    private readonly string directory;
    private readonly TimerDatabase database;
    private JsonObject settings = new();
    // What the database held the last time this run wrote it. An unchanged snapshot is not written again, so a quiet
    // 15 second checkpoint only moves the running timer's "last seen" time.
    private readonly Dictionary<string, string> written = new();
    public LedgerEditor? Editor { get; private set; }
    /// <summary>The open database, for tests that need to cause or check a storage state.</summary>
    public TimerDatabase Database => database;
    /// <summary>The folder holding the database, its backups and exports.</summary>
    public string DataFolder => directory;
    public string ExportDirectory => Path.Combine(directory, "Timesheets", "Exports");
    /// <summary>How many adjustments from an earlier version were removed when the database was opened. Zero for everyone else.</summary>
    public int AdjustmentsRemoved { get; private set; }
    /// <summary>True when the folder holds timesheet files from an older timer. They are never read, changed or deleted.</summary>
    public bool OldFilesFound { get; }
    /// <summary>True once per database: the first launch that finds old files, so the app can say they are unused. Asking marks it as told.</summary>
    public bool TakeOldFilesNotice()
    {
        if (!OldFilesFound || Meta("oldFilesNoted") is not null) return false;
        database.Execute("INSERT INTO meta (key, value) VALUES ('oldFilesNoted', '1')");
        return true;
    }
    /// <summary>Saves a copy of the database, and the settings file, in a new folder under Backups.</summary>
    public string Backup(string label)
    {
        var target = Path.Combine(directory, "Backups", label + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(target);
        database.CopyTo(Path.Combine(target, TimerDatabase.FileName));
        var source = Path.Combine(directory, SettingsFile);
        if (File.Exists(source)) File.Copy(source, Path.Combine(target, SettingsFile), true);
        return target;
    }
    private const string SettingsFile = "basics-advanced-timer.settings.json";
    public TimerStore(string directory)
    {
        this.directory = Path.GetFullPath(directory);
        if (File.Exists(Path.Combine(this.directory, "AccountTimer2.ps1")) || File.Exists(Path.Combine(this.directory, "StartAccountTimer2.vbs")))
            throw new IOException("Use a separate data folder or a copy of your timesheet data, rather than the running PowerShell application folder.");
        Directory.CreateDirectory(this.directory);
        OldFilesFound = File.Exists(Path.Combine(this.directory, "Timesheets", "sessions.csv"));
        database = TimerDatabase.Open(this.directory);
    }
    private static string Iso(DateTime time) => time.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
    private static DateTime ParseIso(string text) => DateTime.ParseExact(text, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
    private string? Meta(string key) => database.Scalar("SELECT value FROM meta WHERE key = $k", ("$k", key)) as string;

    // ---- Notes on a day. They are small and edited one at a time, so each change is its own statement, outside the undo history. ----
    public List<DayNote> LoadDayNotes() => database.Query("SELECT id, day, at_time, text, done, end_time, series_id FROM day_notes ORDER BY day, at_time, id",
        r => new DayNote(r.GetInt64(0), DateTime.ParseExact(r.GetString(1), "yyyy-MM-dd", CultureInfo.InvariantCulture),
            r.IsDBNull(2) ? null : TimeSpan.ParseExact(r.GetString(2), "hh\\:mm", CultureInfo.InvariantCulture), r.GetString(3), r.GetInt64(4) == 1,
            r.IsDBNull(5) ? null : TimeSpan.ParseExact(r.GetString(5), "hh\\:mm", CultureInfo.InvariantCulture), r.IsDBNull(6) ? null : r.GetInt64(6)));
    public long AddDayNote(DateTime day, TimeSpan? at, string text, TimeSpan? end = null, long? seriesId = null)
        => database.Insert("INSERT INTO day_notes (day, at_time, text, end_time, series_id) VALUES ($d, $t, $x, $e, $s)", ("$s", seriesId), ("$e", end?.ToString("hh\\:mm", CultureInfo.InvariantCulture)), ("$d", day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), ("$t", at?.ToString("hh\\:mm", CultureInfo.InvariantCulture)), ("$x", text));
    public void UpdateDayNote(DayNote note)
        => database.Execute("UPDATE day_notes SET day = $d, at_time = $t, text = $x, done = $n, end_time = $e, series_id = $s WHERE id = $i", ("$s", note.SeriesId), ("$e", note.End?.ToString("hh\\:mm", CultureInfo.InvariantCulture)), ("$d", note.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ("$t", note.At?.ToString("hh\\:mm", CultureInfo.InvariantCulture)), ("$x", note.Text), ("$n", note.Done ? 1 : 0), ("$i", note.Id));
    // ---- Repeating notes. A series holds the template; its notes are made ahead by the app and are ordinary notes that carry the series' id. ----
    private static string Time(TimeSpan? time) => time?.ToString("hh\\:mm", CultureInfo.InvariantCulture) ?? "";
    private static string DayText(DateTime day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public List<NoteSeries> LoadSeries() => database.Query("SELECT id, rule, start_day, generated_to, text, at_time, end_time FROM note_series ORDER BY id",
        r => new NoteSeries(r.GetInt64(0), Recurrence.FromRule(r.GetString(1)) ?? new Recurrence(1, 0), DateTime.ParseExact(r.GetString(2), "yyyy-MM-dd", CultureInfo.InvariantCulture),
            DateTime.ParseExact(r.GetString(3), "yyyy-MM-dd", CultureInfo.InvariantCulture), r.GetString(4),
            r.IsDBNull(5) ? null : TimeSpan.ParseExact(r.GetString(5), "hh\\:mm", CultureInfo.InvariantCulture), r.IsDBNull(6) ? null : TimeSpan.ParseExact(r.GetString(6), "hh\\:mm", CultureInfo.InvariantCulture)));
    public long AddSeries(NoteSeries series)
        => database.Insert("INSERT INTO note_series (rule, start_day, generated_to, text, at_time, end_time) VALUES ($r, $s, $g, $x, $t, $e)", ("$r", series.Rule.ToRule()), ("$s", DayText(series.Start)),
            ("$g", DayText(series.GeneratedTo)), ("$x", series.Text), ("$t", series.At is null ? null : Time(series.At)), ("$e", series.End is null ? null : Time(series.End)));
    public void UpdateSeries(NoteSeries series)
        => database.Execute("UPDATE note_series SET rule = $r, start_day = $s, generated_to = $g, text = $x, at_time = $t, end_time = $e WHERE id = $i", ("$r", series.Rule.ToRule()), ("$s", DayText(series.Start)),
            ("$g", DayText(series.GeneratedTo)), ("$x", series.Text), ("$t", series.At is null ? null : Time(series.At)), ("$e", series.End is null ? null : Time(series.End)), ("$i", series.Id));
    /// <summary>Removes a series. Its notes stay as ordinary notes.</summary>
    public void DeleteSeries(long id)
    {
        database.Execute("UPDATE day_notes SET series_id = NULL WHERE series_id = $i", ("$i", id));
        database.Execute("DELETE FROM note_series WHERE id = $i", ("$i", id));
    }
    public void DeleteDayNote(long id) => database.Execute("DELETE FROM day_notes WHERE id = $i", ("$i", id));

    /// <summary>The sections of the settings file the library reads and writes itself. Every other section is the app's.</summary>
    private static readonly HashSet<string> OwnedSections = new() { "workingHours", "dayOverrides", "migrationAppearance", "migrationThemes" };
    public StoreSettings Load(TrackingEngine engine)
    {
        var settingsPath = Path.Combine(directory, SettingsFile);
        if (File.Exists(settingsPath))
        {
            settings = JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject ?? throw new InvalidDataException("Settings must be a JSON object.");
            if (settings["schemaVersion"]?.GetValue<int>() != 1 || settings["sections"] is not JsonObject)
                throw new InvalidDataException("Unsupported or incomplete settings schema; no files have been reset.");
        }
        else settings = new() { ["schemaVersion"] = 1, ["sections"] = new JsonObject() };
        var accounts = database.Query("SELECT name, hidden, colour, budget_seconds, budget_period FROM accounts ORDER BY position, id",
            r => new AccountSettings(r.GetString(0), r.IsDBNull(3) ? null : r.GetDouble(3) / 3600.0, r.GetString(4), r.GetInt64(1) == 1, r.GetString(2).ToUpperInvariant()));
        var firstRun = false; // A brand new database, which gets the getting started setup.
        if (accounts.Count == 0 && Meta("accountsSeeded") is null)
            { accounts = new() { new("Account one"), new("Account two"), new("Internal") }; firstRun = true; }
        var decimalHours = false;
        var weekStart = DayOfWeek.Monday;
        foreach (var line in Lines("workingHours"))
        {
            var p = line.Split('=', 2); if (p.Length != 2) continue;
            switch (p[0].ToLowerInvariant())
            {
                case "start": engine.Schedule.Default = engine.Schedule.Default with { Start = Clock(p[1]) }; break;
                case "end": engine.Schedule.Default = engine.Schedule.Default with { End = Clock(p[1]) }; break;
                case "hours": decimalHours = p[1] == "decimal"; break;
                case "weekstart": weekStart = Dow(p[1]); break;
                case "days": engine.Schedule.Days = p[1].Split(',').Select(Dow).ToHashSet(); break;
                default:
                    if (p[0].StartsWith("shift."))
                    { var parts = p[1].Split(','); engine.Schedule.Shifts[Dow(p[0][6..])] = new(Clock(parts[0]), Clock(parts[1])); }
                    break;
            }
        }
        foreach (var line in Lines("dayOverrides"))
        {
            var p = line.Split('\t'); var day = DateTime.ParseExact(p[0], "yyyy-MM-dd", CultureInfo.InvariantCulture);
            engine.Schedule.Overrides[day] = p[1] == "off" ? new(true, null) : new(false, new(Clock(p[1]), Clock(p[2])));
        }
        foreach (var (day, start) in database.Query("SELECT day, start_at FROM day_starts", r => (r.GetString(0), r.GetString(1))))
            engine.DayStarts[DateTime.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture)] = ParseIso(start);
        var shifts = engine.Schedule.Shifts.Values.Append(engine.Schedule.Default)
            .Concat(engine.Schedule.Overrides.Values.Where(o => !o.Off).Select(o => o.Shift!));
        // An end earlier than the start is an overnight shift, which finishes the next morning. Only an end equal to the start has no meaning.
        if (shifts.Any(s => s.End == s.Start)) throw new InvalidDataException("A working shift must finish at a different time from when it starts.");
        // An earlier version let you adjust an account's time without a block on the clock. That feature is gone, so those rows are left out here and
        // fall away on the next save. A copy of the database is kept first, and the undo history (which could bring them back) is cleared.
        var adjustments = database.Query("SELECT a.name, e.day, e.start_at FROM entries e LEFT JOIN accounts a ON a.id = e.account_id WHERE e.kind = 'adjustment'",
            r => (Account: r.IsDBNull(0) ? BreakAccount.Name : r.GetString(0), Day: r.GetString(1), Start: r.IsDBNull(2) ? "00:00:00" : r.GetString(2)[11..]));
        if (adjustments.Count > 0)
        {
            Backup("before-removing-adjustments");
            database.Execute("DELETE FROM edit_changes"); database.Execute("DELETE FROM edits");
            AdjustmentsRemoved = adjustments.Count;
        }
        engine.Sessions.AddRange(database.Query(
            "SELECT e.kind, a.name, e.day, e.start_at, e.end_at, e.seconds, e.origin, e.minutes_text FROM entries e LEFT JOIN accounts a ON a.id = e.account_id WHERE e.kind <> 'adjustment' ORDER BY e.id", ToRow));
        persistedEdits.Clear();
        var steps = new List<UndoStep>();
        foreach (var edit in database.Query("SELECT id, description FROM edits ORDER BY id", r => (Id: r.GetInt64(0), Description: r.GetString(1))))
        {
            var parts = database.Query("SELECT table_name, before_json, after_json FROM edit_changes WHERE edit_id = $e", r => (Table: r.GetString(0), Before: r.GetString(1), After: r.GetString(2)), ("$e", edit.Id))
                .ToDictionary(p => p.Table);
            var ledger = JsonSerializer.Deserialize<LedgerPart>(parts["ledger"].After) ?? throw new InvalidDataException("Invalid edit history.");
            var step = new UndoStep(edit.Description, ledger.Count, ledger.Hash, JsonSerializer.Deserialize<Splice<Dictionary<string, string>>>(parts["ledger"].Before)!,
                JsonSerializer.Deserialize<TextChange>(parts["notes"].Before)!, JsonSerializer.Deserialize<TextChange>(parts["groups"].Before)!,
                parts["notes"].After, parts["groups"].After); // An older build also saved a positions part; it is ignored.
            steps.Add(step); persistedEdits.Add((edit.Id, step));
        }
        Editor = new LedgerEditor(engine, null, null, Meta("notes"), Meta("groups"), steps: steps);
        if (adjustments.Count > 0) Editor.DropNotes(adjustments);
        // The editor may have trimmed or refused steps it could not use, so what is saved is brought back in line on the next save.
        engine.Away.AddRange(database.Query("SELECT start_at, end_at FROM away ORDER BY id", r => new AwayInterval(ParseIso(r.GetString(0)), r.IsDBNull(1) ? null : ParseIso(r.GetString(1)))));
        var running = database.Query("SELECT kind, a.name, start_at, last_seen FROM running LEFT JOIN accounts a ON a.id = running.account_id",
            r => (Kind: r.GetString(0), Account: r.IsDBNull(1) ? BreakAccount.Name : r.GetString(1), Start: ParseIso(r.GetString(2)), Seen: ParseIso(r.GetString(3)))).FirstOrDefault();
        if (running.Account is not null) engine.Recover(new(running.Account, running.Start), running.Seen);
        var appearance = Lines("migrationAppearance").ToList();
        var theme = ThemeLibrary.Renamed(appearance.FirstOrDefault() ?? ThemeLibrary.Default);
        var shadows = !appearance.Skip(1).Contains("shadows=off");
        var taskbar = !appearance.Skip(1).Contains("taskbar=off");
        var tooltips = !appearance.Skip(1).Contains("tooltips=off");
        var developer = appearance.Skip(1).Contains("developer=on");
        var moveLater = appearance.Skip(1).Contains("shove=always");
        // A pill size chosen by hand, as a percentage, or none for the automatic size.
        int? pillScale = appearance.Skip(1).Select(l => l.StartsWith("pill=", StringComparison.Ordinal) && int.TryParse(l.AsSpan(5), out var n) && n is >= 50 and <= 120 ? n : (int?)null).FirstOrDefault(n => n is not null);
        // The setup is done once it says so. A database from before it existed counts as done, and a new one is pending until it is finished or skipped.
        var setupDone = appearance.Skip(1).Contains("setup=done") || (!appearance.Skip(1).Contains("setup=pending") && !firstRun);
        var pillOnTop = !appearance.Skip(1).Contains("pilltop=off"); var dashboardOnTop = !appearance.Skip(1).Contains("dashtop=off");
        // Anything in the appearance section or the settings file that the library does not own belongs to the app, and is handed to it as it was written.
        var ownedLines = new[] { "shadows=", "taskbar=", "tooltips=", "developer=", "shove=", "pill=", "setup=", "pilltop=", "dashtop=" };
        var extraLines = appearance.Skip(1).Where(l => !ownedLines.Any(k => l.StartsWith(k, StringComparison.Ordinal))).ToList();
        var extraSections = settings["sections"]!.AsObject().Where(p => !OwnedSections.Contains(p.Key) && p.Value is JsonArray).ToDictionary(p => p.Key, p => (IReadOnlyList<string>)Lines(p.Key).ToList());
        var themes = ThemeStorage.FromLines(Lines("migrationThemes"));
        // A name that is neither System, a built-in nor one of the user's own falls back to System, so a removed theme never leaves the app unstyled.
        if (theme != ThemeLibrary.System && !ThemeLibrary.BuiltIn.Concat(themes).Any(t => t.Name == theme)) theme = ThemeLibrary.System;
        // Written back on the first save, so a brand new database does not start with the sample accounts every launch.
        return new(accounts, decimalHours, theme, weekStart, shadows, themes, taskbar, tooltips, developer, setupDone, moveLater, pillScale, pillOnTop, dashboardOnTop, new SettingsExtras(extraLines, extraSections));
    }
    private static SessionRow ToRow(Microsoft.Data.Sqlite.SqliteDataReader r)
    {
        var kind = r.GetString(0); var account = kind == "work" || !r.IsDBNull(1) ? r.GetString(1) : BreakAccount.Name;
        var day = r.GetString(2); var origin = r.GetString(6);
        var minutes = r.IsDBNull(7) ? Math.Round(r.GetDouble(5) / 60.0, 2, MidpointRounding.ToEven).ToString("0.##", CultureInfo.InvariantCulture) : r.GetString(7);
        var start = r.IsDBNull(3) ? "00:00:00" : r.GetString(3)[11..];
        var end = origin == "added" ? "added" : origin == "edited" ? "edited" : r.GetString(4)[11..];
        return new(day, account, start, end, minutes);
    }
    public void Save(TrackingEngine engine, StoreSettings preferences)
    {
        // The caller already handles file errors, so a database error is reported the same way.
        var edits = persistedEdits.ToList();
        try { SaveRecords(engine, preferences); }
        catch (Exception error)
        {
            persistedEdits.Clear(); persistedEdits.AddRange(edits); // The transaction rolled back, so the list must too.
            if (error is Microsoft.Data.Sqlite.SqliteException) throw new IOException("The timer's database could not be saved. " + error.Message, error);
            throw;
        }
    }
    private void SaveRecords(TrackingEngine engine, StoreSettings preferences)
    {
        Editor ??= new LedgerEditor(engine);
        var sections = (JsonObject)settings["sections"]!;
        sections["dayOverrides"] = Array(engine.Schedule.Overrides.OrderBy(p => p.Key).Select(p => p.Value.Off
            ? $"{p.Key:yyyy-MM-dd}\toff" : $"{p.Key:yyyy-MM-dd}\t{p.Value.Shift!.Start:hh\\:mm}\t{p.Value.Shift.End:hh\\:mm}"));
        sections["migrationAppearance"] = Array(new[] { preferences.Theme, preferences.Shadows ? "shadows=on" : "shadows=off", preferences.TaskbarButton ? "taskbar=on" : "taskbar=off", preferences.Tooltips ? "tooltips=on" : "tooltips=off", preferences.Developer ? "developer=on" : "developer=off", preferences.SetupDone ? "setup=done" : "setup=pending", preferences.MoveLaterAlways ? "shove=always" : "shove=ask", preferences.PillScale is { } pill ? "pill=" + pill : "pill=auto", preferences.PillOnTop ? "pilltop=on" : "pilltop=off", preferences.DashboardOnTop ? "dashtop=on" : "dashtop=off" }
            .Concat(preferences.Extras?.AppearanceLines ?? System.Array.Empty<string>()));
        // The app's own sections are written as they were handed over.
        foreach (var (name, lines) in preferences.Extras?.Sections ?? SettingsExtras.Empty.Sections) if (!OwnedSections.Contains(name)) sections[name] = Array(lines);
        sections["migrationThemes"] = Array((preferences.Themes ?? System.Array.Empty<ThemeDefinition>()).Select(ThemeStorage.ToLine));
        var hours = Lines("workingHours").Where(s =>
        {
            var key = s.Split('=')[0];
            return key is not ("start" or "end" or "days" or "hours" or "weekstart") && !key.StartsWith("shift.", StringComparison.OrdinalIgnoreCase);
        }).ToList();
        hours.Add($"start={engine.Schedule.Default.Start:hh\\:mm}");
        hours.Add($"end={engine.Schedule.Default.End:hh\\:mm}");
        hours.Add("days=" + string.Join(",", engine.Schedule.Days.OrderBy(d => (int)d).Select(d => d.ToString()[..3].ToLowerInvariant())));
        hours.AddRange(engine.Schedule.Shifts.OrderBy(p => (int)p.Key).Select(p => $"shift.{p.Key.ToString()[..3].ToLowerInvariant()}={p.Value.Start:hh\\:mm},{p.Value.End:hh\\:mm}"));
        hours.Add("weekstart=" + preferences.WeekStart.ToString()[..3].ToLowerInvariant());
        hours.Add("hours=" + (preferences.DecimalHours ? "decimal" : "hm")); sections["workingHours"] = Array(hours);
        var settingsText = settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        if (!written.TryGetValue(SettingsFile, out var previous) || previous != settingsText) { AtomicSettings(settingsText); written[SettingsFile] = settingsText; }

        var records = new StringBuilder();
        foreach (var a in preferences.Accounts) records.Append(a).Append('\n');
        foreach (var s in engine.Sessions) records.Append(s).Append('\n');
        foreach (var a in engine.Away) records.Append(a).Append('\n');
        foreach (var d in engine.DayStarts.OrderBy(d => d.Key)) records.Append(d.Key.Ticks).Append(d.Value.Ticks).Append('\n');
        var blobs = new Dictionary<string, string>
        {
            ["notes"] = Editor.NotesCsv, ["groups"] = Editor.GroupsCsv ?? "GroupId,Date,Account,Start,End,Minutes\n", ["accountsSeeded"] = "1"
        };
        foreach (var b in blobs) records.Append(b.Key).Append('=').Append(b.Value).Append('\n');
        var snapshot = records.ToString();
        database.InTransaction(() =>
        {
            if (!written.TryGetValue("records", out var last) || last != snapshot) WriteRecords(engine, preferences, blobs);
            SaveEdits(engine);
            database.Execute("DELETE FROM meta WHERE key IN ('editState', 'positions')"); // Left by earlier test builds.
            // The running timer is kept current on every save, so a crash is credited up to the last save.
            database.Execute("DELETE FROM running");
            if (engine.Active is { } active)
            {
                var isBreak = BreakAccount.Is(active.Account);
                database.Execute("INSERT INTO running (id, kind, account_id, resume_account_id, start_at, last_seen) VALUES (1, $k, (SELECT id FROM accounts WHERE name = $n), (SELECT id FROM accounts WHERE name = $r), $s, $l)",
                    ("$k", isBreak ? "break" : "work"), ("$n", isBreak ? null : active.Account), ("$r", isBreak ? engine.ResumeAfterBreak : null), ("$s", Iso(active.Start)), ("$l", Iso(engine.Now)));
            }
        });
        written["records"] = snapshot;
    }
    private sealed record LedgerPart(int Count, string Hash);
    private readonly List<(long Id, UndoStep Step)> persistedEdits = new();
    // Each edit is a row in edits with its parts in edit_changes. Saving adds only the edits made since the last save and
    // removes the ones that were undone or trimmed, so the history is no longer rewritten whole at every checkpoint.
    // The parts are still splices over the ledger and the notes, groups and positions text, because those are not tables yet.
    private void SaveEdits(TrackingEngine engine)
    {
        var current = Editor!.Steps;
        foreach (var gone in persistedEdits.Where(p => !current.Any(c => ReferenceEquals(c, p.Step))).ToList())
        { database.Execute("DELETE FROM edits WHERE id = $i", ("$i", gone.Id)); persistedEdits.Remove(gone); }
        foreach (var step in current.Where(c => !persistedEdits.Any(p => ReferenceEquals(p.Step, c))))
        {
            var id = database.Insert("INSERT INTO edits (at, description) VALUES ($a, $d)", ("$a", Iso(engine.Now)), ("$d", step.Description));
            void Part(string table, object before, string after) => database.Execute("INSERT INTO edit_changes (edit_id, table_name, row_id, before_json, after_json) VALUES ($e, $t, 0, $b, $a)",
                ("$e", id), ("$t", table), ("$b", JsonSerializer.Serialize(before)), ("$a", after));
            Part("ledger", step.Rows, JsonSerializer.Serialize(new LedgerPart(step.AfterCount, step.AfterHash)));
            Part("notes", step.Notes, step.AfterNotesHash); Part("groups", step.Groups, step.AfterGroupsHash);
            persistedEdits.Add((id, step));
        }
    }
    private void WriteRecords(TrackingEngine engine, StoreSettings preferences, Dictionary<string, string> blobs)
    {
        database.Execute("DELETE FROM running"); database.Execute("DELETE FROM entries"); database.Execute("DELETE FROM away"); database.Execute("DELETE FROM day_starts");
        var ids = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        // Accounts keep their row (and so their id) across saves; only the ones no longer listed or used are removed.
        var listed = preferences.Accounts.Select(a => a.Name).ToList();
        var used = engine.Sessions.Where(s => !BreakAccount.Is(s.Account)).Select(s => s.Account).Concat(engine.Active is { } act && !BreakAccount.Is(act.Account) ? new[] { act.Account } : System.Array.Empty<string>());
        var extra = used.Where(n => !listed.Contains(n, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        database.Execute("DELETE FROM accounts WHERE name NOT IN (SELECT value FROM json_each($names))", ("$names", JsonSerializer.Serialize(listed.Concat(extra))));
        var position = 0;
        foreach (var a in preferences.Accounts.Concat(extra.Select(n => new AccountSettings(n, Hidden: true, Colour: "#91B7D5"))))
        {
            database.Execute(@"INSERT INTO accounts (name, colour, position, hidden, budget_seconds, budget_period) VALUES ($n, $c, $p, $h, $b, $u)
                ON CONFLICT(name) DO UPDATE SET colour = $c, position = $p, hidden = $h, budget_seconds = $b, budget_period = $u",
                ("$n", a.Name), ("$c", a.Colour ?? "#91B7D5"), ("$p", position++), ("$h", a.Hidden ? 1 : 0),
                ("$b", a.BudgetHours is { } h ? Math.Round(h * 3600) : null), ("$u", a.BudgetPeriod));
        }
        foreach (var row in database.Query("SELECT id, name FROM accounts", r => (r.GetInt64(0), r.GetString(1)))) ids[row.Item2] = row.Item1;
        var now = Iso(engine.Now);
        foreach (var s in engine.Sessions)
        {
            var isBreak = BreakAccount.Is(s.Account);
            var kind = isBreak ? "break" : "work";
            var origin = s.End.Equals("added", StringComparison.OrdinalIgnoreCase) ? "added" : s.End.Equals("edited", StringComparison.OrdinalIgnoreCase) ? "edited" : "recorded";
            var from = s.Date + "T" + s.Start;
            var end = s.End.Contains(':') ? s.Date + "T" + s.End : Iso(s.To);
            database.Execute(@"INSERT INTO entries (kind, account_id, day, start_at, end_at, seconds, origin, created_at, minutes_text) VALUES ($k, $a, $d, $s, $e, $x, $o, $c, $m)",
                ("$k", kind), ("$a", isBreak ? null : ids[s.Account]), ("$d", s.Date), ("$s", from), ("$e", end),
                ("$x", s.Duration.TotalSeconds), ("$o", origin), ("$c", now), ("$m", s.Minutes));
        }
        foreach (var a in engine.Away) database.Execute("INSERT INTO away (start_at, end_at) VALUES ($s, $e)", ("$s", Iso(a.Start)), ("$e", a.End is { } e ? Iso(e) : null));
        foreach (var d in engine.DayStarts) database.Execute("INSERT INTO day_starts (day, start_at) VALUES ($d, $s)", ("$d", d.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), ("$s", Iso(d.Value)));
        foreach (var b in blobs) database.Execute("INSERT INTO meta (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = $v", ("$k", b.Key), ("$v", b.Value));
    }
    private void AtomicSettings(string content)
    {
        var path = Path.Combine(directory, SettingsFile);
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        { var bytes = new UTF8Encoding(true).GetBytes("﻿" + content); stream.Write(bytes); stream.Flush(true); }
        if (File.Exists(path)) File.Copy(path, path + ".bak", true);
        File.Move(temporary, path, true);
    }
    private IEnumerable<string> Lines(string key)
    {
        var node = settings["sections"]?[key];
        if (node is null) return System.Array.Empty<string>();
        if (node is not JsonArray array) throw new InvalidDataException("Settings section " + key + " must be an array.");
        return array.Select(s => s?.GetValue<string>() ?? "").ToArray();
    }
    private static JsonArray Array(IEnumerable<string> lines) => new(lines.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
    private static TimeSpan Clock(string s) => TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out var time) && time >= TimeSpan.Zero && time < TimeSpan.FromDays(1)
        ? time : throw new InvalidDataException("Invalid working-hours clock.");
    private static DayOfWeek Dow(string s) => s.Trim().ToLowerInvariant() switch
    { "sun" => DayOfWeek.Sunday, "mon" => DayOfWeek.Monday, "tue" => DayOfWeek.Tuesday, "wed" => DayOfWeek.Wednesday, "thu" => DayOfWeek.Thursday, "fri" => DayOfWeek.Friday, "sat" => DayOfWeek.Saturday, _ => throw new InvalidDataException("Invalid weekday.") };
    private bool disposed;
    /// <summary>Whether the database has been closed, after which nothing more can be saved.</summary>
    public bool IsDisposed => disposed;
    public void Dispose() { disposed = true; database.Dispose(); }
}
