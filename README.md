# Basic-Timer-Core

The shared engine behind Basic's timer apps, published as two NuGet packages on GitHub Packages.

| Package | What it holds |
| --- | --- |
| `BasicallyGod.Timer.Core` | Tracking engine, schedules (including overnight shifts), day maths, day notes, theme model, the note shorthand and `#Tag` rule (`NoteShorthand`, `AccountTags`), keyboard shortcut parsing (`HotkeySpec`) and the feed reader (`Feed`). No UI types. |
| `BasicallyGod.Timer.Storage` | SQLite database (schema 6, forward-only upgrades), `TimerStore`, undo history, ledger editing, credit review, exports. |

An app keeps its own settings without changing these libraries: `StoreSettings.Extras` carries appearance lines and JSON sections the library does not own and saves them back untouched.

## Versions

- **Next release (0.5.0)** separates note type from scheduling. `DayNote.Kind` accepts Task, Meeting or Deadline independently of time, and `HasDate=false` represents an undated task. `Day` retains capture context for an undated task; it must not be interpreted as a due date. Untyped legacy notes retain their existing marker/time interpretation. `NoteSeries.Kind` preserves the type of recurring timed tasks. Storage format 6 adds these fields with a backup before upgrading; older app builds refuse the newer database, so keep the backup for rollback.
- **0.2.0** moves the note shorthand, the `#Tag` rule and the shortcut parser here from the desk app (namespace `Timer.Core`), and adds `Feed`: reading Markdown files from a folder into one newest-first stream, with duplicate and age rules and a folder-size note. `Trinkets.FindAccountTag` in the desk app is now `AccountTags.Find`.
- **0.1.0** is the engine and storage as they were when they left the original app.

## Releasing

Push a tag such as `v0.1.0`. CI runs the tests, then publishes both packages at that version.

## Using the packages

Add a `NuGet.config` with the source `https://nuget.pkg.github.com/BasicallyGod/index.json` and a token with `read:packages`, then reference `BasicallyGod.Timer.Core` and `BasicallyGod.Timer.Storage`.

## Where the code comes from

Copied from the alpha branch of Basic-Advanced-Timer at the shared-library boundary. Until that app consumes these packages, changes to the engine are made in both places, so keep them identical.

## Explicit note scheduling

Use `DayNoteKind.Task` for a task with an optional date and time. An undated task must have no time, end or series. Use `DayNoteKind.Meeting` for a dated event with a required start and optional end, and `DayNoteKind.Deadline` for a dated deadline with an optional due time. `DayNotes.Validate` and the storage write methods reject invalid combinations before writing. New callers should set the type explicitly; omitting it retains legacy inference for existing applications.

`DayNotes.KindOf` recognises start-only meeting events. The existing `IsMeeting` predicate deliberately continues to mean a **bounded** meeting suitable for drawing on the timeline; it requires an end after the start. `EndsAt` returns null for an open-ended event. This avoids inventing a duration or tracked time. `ComingUp` excludes undated tasks and keeps unfinished timed tasks visible for their day; `Todos` includes undated tasks regardless of capture day. `DueAt` rejects an undated task.

Rollout: merge the shared-library change and release version 0.5.0 through the existing CI tag/manual-release workflow, then update consuming apps to that version and wire their editors, reminder rules and calendar filters to the new fields. This change provides the shared model and storage; it does not enable new scheduling controls in an existing desktop build. A consumer must propagate Kind when generating recurring occurrences and preserve Kind/HasDate during note updates and retry writes. Undated tasks need a separate list or filter rather than being attached to the day being viewed.
