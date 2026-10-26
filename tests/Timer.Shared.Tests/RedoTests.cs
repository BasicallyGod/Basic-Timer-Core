using Timer.Core;
using Timer.Storage;
using Xunit;

namespace Timer.Shared.Tests;

// Redo puts back what Undo took back, step by step, and ends the moment anything new is edited.
public sealed class RedoTests
{
    private static DateTime Day => new(2026, 10, 9);
    private static TrackingEngine Engine() => new(clock: () => Day.AddHours(15));
    private static SessionRow Row(string start, string minutes, string account = "Account one") => new("2026-10-09", account, start, "added", minutes);
    private static (string Rows, string Notes, string? Groups) State(LedgerEditor e) => (e.SessionsCsv, e.NotesCsv, e.GroupsCsv);

    [Fact]
    public void RedoWalksForwardThroughEveryEditAndUndoWalksBackAgain()
    {
        var engine = Engine(); engine.Sessions.Add(Row("08:00:00", "30"));
        var editor = new LedgerEditor(engine);
        var states = new List<(string, string, string?)> { State(editor) };
        editor.Add("Account two", Day.AddHours(9), Day.AddHours(9.5), "first"); states.Add(State(editor));
        editor.Edit(engine.Sessions[0], "Account one", Day.AddHours(8), Day.AddHours(8.75), "longer"); states.Add(State(editor));
        editor.Delete(engine.Sessions[1]); states.Add(State(editor));

        Assert.False(editor.CanRedo); Assert.Null(editor.RedoLabel);
        for (var i = states.Count - 2; i >= 0; i--) { editor.Undo(); Assert.Equal(states[i], State(editor)); Assert.True(editor.CanRedo); }
        Assert.Equal(3, editor.RedoCount); Assert.False(editor.CanUndo);
        for (var i = 1; i < states.Count; i++) { editor.Redo(); Assert.Equal(states[i], State(editor)); }
        Assert.False(editor.CanRedo); Assert.Equal(3, editor.UndoCount);
        for (var i = states.Count - 2; i >= 0; i--) { editor.Undo(); Assert.Equal(states[i], State(editor)); } // And the redone edits undo again.
    }

    [Fact]
    public void RedoIsNamedLikeTheEditAndRefusesWhenThereIsNothingToPutBack()
    {
        var engine = Engine(); var editor = new LedgerEditor(engine);
        Assert.Throws<InvalidOperationException>(() => editor.Redo());
        editor.Add("Account one", Day.AddHours(9), Day.AddHours(10), ""); editor.Undo();
        Assert.Equal("Add time", editor.RedoLabel); Assert.Empty(engine.Sessions);
        editor.Redo(); Assert.Single(engine.Sessions); Assert.Equal("Add time", editor.UndoLabel);
    }

    [Fact]
    public void ANewEditEndsRedo()
    {
        var engine = Engine(); var editor = new LedgerEditor(engine);
        editor.Add("Account one", Day.AddHours(9), Day.AddHours(10), ""); editor.Undo(); Assert.True(editor.CanRedo);
        editor.Add("Account two", Day.AddHours(11), Day.AddHours(12), "");
        Assert.False(editor.CanRedo); Assert.Equal(0, editor.RedoCount);
    }

    [Fact]
    public void RedoIsNotOfferedOnceTheLedgerHasMovedOnAndKeepsLaterRecordings()
    {
        var engine = Engine(); var editor = new LedgerEditor(engine);
        editor.Add("Account one", Day.AddHours(9), Day.AddHours(10), "note"); editor.Undo();
        engine.Sessions.Add(Row("13:00:00", "20", "Account two")); // Time recorded since.
        Assert.True(editor.CanRedo); editor.Redo();
        Assert.Equal(2, engine.Sessions.Count); Assert.Contains(engine.Sessions, s => s.Account == "Account two"); Assert.Equal("note", editor.Note(engine.Sessions.Single(s => s.Account == "Account one")));
        editor.Undo(); editor.Redo(); Assert.Equal(2, engine.Sessions.Count);

        editor.Undo(); engine.Sessions[0] = Row("07:00:00", "5", "Account three"); // Changed outside the editor.
        Assert.False(editor.CanRedo);
    }

    [Fact]
    public void RenamingOrDeletingAnAccountClearsRedoAsItClearsUndo()
    {
        var engine = Engine(); var editor = new LedgerEditor(engine);
        editor.Add("Account one", Day.AddHours(9), Day.AddHours(10), ""); editor.Add("Account two", Day.AddHours(11), Day.AddHours(12), "");
        editor.Undo(); Assert.True(editor.CanRedo);
        editor.RenameAccount("Account one", "Renamed"); Assert.False(editor.CanRedo); Assert.False(editor.CanUndo);
    }

    [Fact]
    public void RedoedStepsAreSavedAndReloadedAsOrdinaryUndoHistory()
    {
        var folder = Path.Combine(Path.GetTempPath(), "timer-redo-" + Guid.NewGuid());
        try
        {
            var engine = Engine();
            using (var store = new TimerStore(folder))
            {
                var settings = store.Load(engine); var editor = store.Editor!;
                editor.Add("Account one", Day.AddHours(9), Day.AddHours(10), "kept"); editor.Add("Account two", Day.AddHours(11), Day.AddHours(12), "");
                editor.Undo(); editor.Redo(); store.Save(engine, settings);
            }
            var again = Engine();
            using (var store = new TimerStore(folder))
            {
                store.Load(again); Assert.Equal(2, again.Sessions.Count); Assert.True(store.Editor!.CanUndo); Assert.False(store.Editor.CanRedo);
                store.Editor.Undo(); store.Editor.Undo(); Assert.Empty(again.Sessions);
            }
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
