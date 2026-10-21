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

    [Theory]
    [InlineData("Client call 14:00 - 15:30", "Client call")]
    [InlineData("14:00-15:30 Client call", "Client call")]
    [InlineData("Client call 14:00 \u2013 15:30 #Acme", "Client call #Acme")]
    public void ATimeRangeMakesAMeetingWithAStartAndAnEnd(string typed, string words)
    {
        var parts = NoteShorthand.Parse(typed, Today);
        Assert.Equal(words, parts.Text); Assert.Equal(new TimeSpan(14, 0, 0), parts.At); Assert.Equal(new TimeSpan(15, 30, 0), parts.End);
        Assert.True(DayNotes.IsMeeting(new DayNote(1, Today, parts.At, parts.Text, false, parts.End)));
        Assert.Equal(Today.AddHours(15.5), DayNotes.EndsAt(new DayNote(1, Today, parts.At, parts.Text, false, parts.End)));
    }

    [Fact]
    public void ARangeThatEndsBeforeItStartsIsNotAMeetingAndALoneTimeStaysATimedNote()
    {
        var backwards = NoteShorthand.Parse("call 15:30 - 14:00", Today); Assert.Null(backwards.End);
        var lone = NoteShorthand.Parse("call Sam 14:30", Today); Assert.Null(lone.End); Assert.Equal(new TimeSpan(14, 30, 0), lone.At);
        Assert.False(DayNotes.IsMeeting(new DayNote(1, Today, new TimeSpan(14, 0, 0), "x")));
    }

    [Fact]
    public void ClearingAMeetingsTimePutsTheWholeRangeBackIntoTheWords()
        => Assert.Equal("14:00 - 15:30 Client call", NoteShorthand.WithTimeInWords("Client call", new TimeSpan(14, 0, 0), new TimeSpan(15, 30, 0)));

    [Theory]
    [InlineData("Standup every weekday", "Standup", Recurrence.Weekdays, 1)]
    [InlineData("Standup every day", "Standup", Recurrence.AllDays, 1)]
    [InlineData("Team call every mon", "Team call", 1 << 1, 1)]
    [InlineData("Gym every mon,wed,fri", "Gym", (1 << 1) | (1 << 3) | (1 << 5), 1)]
    [InlineData("Review every week", "Review", 0, 1)]
    [InlineData("Review every 2 weeks", "Review", 0, 2)]
    [InlineData("every 2 weeks fri Review", "Review", 1 << 5, 2)]
    public void AnEveryPhraseMakesANoteRepeatAndLeavesTheOtherWords(string typed, string words, int mask, int weeks)
    {
        var parts = NoteShorthand.Parse(typed, Today);
        Assert.Equal(words, parts.Text); Assert.Equal(new Recurrence(weeks, mask), parts.Repeat);
    }

    [Fact]
    public void ARepeatKeepsItsTimeRangeAndItsEndDay()
    {
        var parts = NoteShorthand.Parse("Standup 09:30 - 10:00 every weekday until 2026-12-18", Today);
        Assert.Equal("Standup", parts.Text); Assert.Equal(new TimeSpan(9, 30, 0), parts.At); Assert.Equal(new TimeSpan(10, 0, 0), parts.End);
        Assert.Equal(new Recurrence(1, Recurrence.Weekdays, new DateTime(2026, 12, 18)), parts.Repeat);
    }

    [Theory]
    [InlineData("Daily report to Sam")]            // Only a phrase starting with "every" repeats, so a word in a sentence never does.
    [InlineData("every time we meet")]
    [InlineData("every 1 weeks")]
    [InlineData("every")]
    [InlineData("every weekday")]                   // Only the phrase: it stays as the words.
    public void OrdinaryWordsAreNotARepeat(string typed) => Assert.Null(NoteShorthand.Parse(typed, Today).Repeat);

    [Fact]
    public void ARuleKnowsItsOccurrencesAndSurvivesBeingWrittenDown()
    {
        var start = new DateTime(2026, 10, 14); // A Wednesday.
        var weekdays = new Recurrence(1, Recurrence.Weekdays);
        Assert.Equal(new[] { 14, 15, 16, 19, 20 }, weekdays.Between(start, start, new DateTime(2026, 10, 20)).Select(d => d.Day));
        var fortnightly = new Recurrence(2, 0); // Same weekday as the start, every second week.
        Assert.Equal(new[] { new DateTime(2026, 10, 14), new DateTime(2026, 10, 28), new DateTime(2026, 11, 11) }, fortnightly.Between(start, start, new DateTime(2026, 11, 15)));
        Assert.Empty(new Recurrence(1, Recurrence.Weekdays, new DateTime(2026, 10, 13)).Between(start, start, new DateTime(2026, 10, 30))); // Ended before it began.
        foreach (var rule in new[] { weekdays, fortnightly, new Recurrence(3, (1 << 1) | (1 << 4), new DateTime(2027, 1, 1)) })
        {
            Assert.Equal(rule, Recurrence.FromRule(rule.ToRule()));
            Assert.Equal(rule, NoteShorthand.Parse("x " + rule.Typed(), Today).Repeat); // What is typed back into the words reads as the same rule.
        }
        Assert.Equal("Every weekday", weekdays.Words()); Assert.Equal("Every 2 weeks", fortnightly.Words());
    }

    [Fact]
    public void ASeriesOnlyMakesNotesFromTodayAndNeverAgainForADayAlreadyMade()
    {
        var series = new NoteSeries(1, new Recurrence(1, Recurrence.Weekdays), new DateTime(2026, 10, 1), new DateTime(2026, 10, 1).AddDays(-1), "Standup", null, null);
        var today = new DateTime(2026, 10, 14);
        var days = series.Pending(today, 7);
        Assert.Equal(new DateTime(2026, 10, 14), days[0]); Assert.Equal(new DateTime(2026, 10, 21), days[^1]); // Nothing before today, and a week ahead.
        var later = series with { GeneratedTo = days[^1] };
        Assert.Empty(later.Pending(today, 7));                                  // Already made, so a removed one is not made again.
        Assert.Equal(new[] { 22 }, later.Pending(today.AddDays(1), 7).Select(d => d.Day)); // A day on, one more day comes into reach.
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
