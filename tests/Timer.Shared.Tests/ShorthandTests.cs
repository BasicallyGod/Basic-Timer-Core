using Timer.Core;
using Xunit;

namespace Timer.Shared.Tests;

public sealed class ShorthandTests
{
    private static readonly DateTime Today = new(2026, 10, 14); // A Wednesday.
    private static readonly string[] Accounts = { "Account one", "Account two", "Acme" };

    [Fact]
    public void ATimeAndADayAreTakenOutOfTheWords()
    {
        var parts = NoteShorthand.Parse("call Sam 14:30 @fri", Today);
        Assert.Equal("call Sam", parts.Text); Assert.Equal(new TimeSpan(14, 30, 0), parts.At); Assert.Equal(new DateTime(2026, 10, 16), parts.Day);
    }

    [Fact]
    public void DaysUnderstandTomorrowWeekdaysAndDates()
    {
        Assert.Equal(new DateTime(2026, 10, 15), NoteShorthand.DayFor("tomorrow", Today));
        Assert.Equal(new DateTime(2026, 10, 21), NoteShorthand.DayFor("wed", Today)); // Today is a Wednesday, so the next one is a week on.
        Assert.Equal(new DateTime(2026, 10, 21), NoteShorthand.DayFor("21oct", Today));
        Assert.Equal(new DateTime(2026, 10, 21), NoteShorthand.DayFor("2026-10-21", Today));
        Assert.Null(NoteShorthand.DayFor("soon", Today)); // Plain words are never taken for a date.
    }

    [Fact]
    public void ATagLinksOneAccountAndOnlyOne()
    {
        Assert.Equal("Account one", NoteShorthand.AccountOf("ring #AccountOne", Accounts));
        Assert.Equal("Acme", NoteShorthand.AccountOf("#acm update", Accounts));
        Assert.Null(NoteShorthand.AccountOf("#Account is ambiguous", Accounts)); // It starts both "Account one" and "Account two".
        Assert.Null(NoteShorthand.AccountOf("#Nobody", Accounts));
        Assert.Null(NoteShorthand.AccountOf("#Break", new[] { "Break", "Acme" }));
    }

    [Fact]
    public void RenamingAnAccountRewritesItsTagAndDeletingItTurnsTheTagIntoAWord()
    {
        var renamed = new Dictionary<string, string> { ["Acme"] = "Acme Ltd" };
        Assert.Equal("ring #AcmeLtd today", NoteShorthand.Relink("ring #Acme today", Accounts, renamed, Array.Empty<string>()));
        Assert.Equal("ring Acme today", NoteShorthand.Relink("ring #Acme today", Accounts, new Dictionary<string, string>(), new[] { "Acme" }));
    }

    [Fact]
    public void RecognisedPartsPutTheirWordsBackWhenTakenOut()
    {
        Assert.Equal("14:30 ring Sam", NoteShorthand.WithTimeInWords("ring Sam", new TimeSpan(14, 30, 0)));
        Assert.Equal("! 14:30 ring Sam", NoteShorthand.WithTimeInWords("! ring Sam", new TimeSpan(14, 30, 0))); // A deadline marker stays first.
        Assert.Equal("ring Sam", NoteShorthand.WithoutDeadline("! ring Sam"));
        Assert.Equal("ring Acme", NoteShorthand.WithoutAccountTag("ring #Acme", Accounts));
    }
}

public sealed class HotkeyTests
{
    [Fact]
    public void AShortcutIsReadWrittenAndValidated()
    {
        var spec = HotkeySpec.Parse("alt + ctrl + n")!;
        Assert.Equal("Ctrl+Alt+N", spec.ToString()); Assert.Equal(HotkeySpec.Control | HotkeySpec.AltMod, spec.Modifiers); Assert.Equal('N', HotkeySpec.VirtualKey(spec.Key));
        Assert.Equal("Win+F9", HotkeySpec.Parse("win+f9")!.ToString()); Assert.Equal(0x78, HotkeySpec.VirtualKey("F9"));
        Assert.Null(HotkeySpec.Parse("N")); Assert.Null(HotkeySpec.Parse("Shift+N")); // A letter alone, or with Shift alone, would take over typing.
        Assert.Null(HotkeySpec.Parse("Ctrl+Enter")); Assert.Null(HotkeySpec.Parse("Ctrl+A+B")); Assert.Null(HotkeySpec.Parse("Ctrl+F13")); Assert.Null(HotkeySpec.Parse("")); Assert.Null(HotkeySpec.Parse(null));
    }
}
