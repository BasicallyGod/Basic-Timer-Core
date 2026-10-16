# Basic-Timer-Core

The shared engine behind Basic's timer apps, published as two NuGet packages on GitHub Packages.

| Package | What it holds |
| --- | --- |
| `BasicallyGod.Timer.Core` | Tracking engine, schedules (including overnight shifts), day maths, day notes, theme model, the note shorthand and `#Tag` rule (`NoteShorthand`, `AccountTags`), keyboard shortcut parsing (`HotkeySpec`) and the feed reader (`Feed`). No UI types. |
| `BasicallyGod.Timer.Storage` | SQLite database (schema 3, forward-only upgrades), `TimerStore`, undo history, ledger editing, credit review, exports. |

An app keeps its own settings without changing these libraries: `StoreSettings.Extras` carries appearance lines and JSON sections the library does not own and saves them back untouched.

## Versions

- **0.2.0** moves the note shorthand, the `#Tag` rule and the shortcut parser here from the desk app (namespace `Timer.Core`), and adds `Feed`: reading Markdown files from a folder into one newest-first stream, with duplicate and age rules and a folder-size note. `Trinkets.FindAccountTag` in the desk app is now `AccountTags.Find`.
- **0.1.0** is the engine and storage as they were when they left the original app.

## Releasing

Push a tag such as `v0.1.0`. CI runs the tests, then publishes both packages at that version.

## Using the packages

Add a `NuGet.config` with the source `https://nuget.pkg.github.com/BasicallyGod/index.json` and a token with `read:packages`, then reference `BasicallyGod.Timer.Core` and `BasicallyGod.Timer.Storage`.

## Where the code comes from

Copied from the alpha branch of Basic-Advanced-Timer at the shared-library boundary. Until that app consumes these packages, changes to the engine are made in both places, so keep them identical.
