using Timer.Core;
using Xunit;

namespace Timer.Shared.Tests;

public sealed class MonthlyRecurrenceTests
{
    private static readonly Recurrence Monthly = new(1, 0, null, 1);

    [Fact]
    public void AMonthlyRuleFallsOnTheSameDayOfEachMonthAndNoOther()
    {
        var start = new DateTime(2026, 10, 15);
        Assert.Equal(new[] { new DateTime(2026, 10, 15), new DateTime(2026, 11, 15), new DateTime(2026, 12, 15), new DateTime(2027, 1, 15) }, Monthly.Between(start, start, new DateTime(2027, 1, 31)));
        Assert.False(Monthly.Includes(start, new DateTime(2026, 9, 15))); // Never before it began.
    }

    [Fact]
    public void AMonthTooShortForTheDayTakesItsLastDay()
    {
        var start = new DateTime(2027, 1, 31);
        Assert.Equal(new[] { new DateTime(2027, 1, 31), new DateTime(2027, 2, 28), new DateTime(2027, 3, 31), new DateTime(2027, 4, 30) }, Monthly.Between(start, start, new DateTime(2027, 4, 30)));
        Assert.Equal(new DateTime(2028, 2, 29), Monthly.Between(new DateTime(2028, 1, 31), new DateTime(2028, 2, 1), new DateTime(2028, 2, 29)).Single()); // A leap year.
    }

    [Fact]
    public void EveryTwoMonthsSkipsTheOnesBetweenAndTheWeekdaySettingsAreIgnored()
    {
        var rule = new Recurrence(1, Recurrence.Weekdays, null, 2); var start = new DateTime(2026, 10, 15);
        Assert.Equal(new[] { new DateTime(2026, 10, 15), new DateTime(2026, 12, 15), new DateTime(2027, 2, 15) }, rule.Between(start, start, new DateTime(2027, 2, 28)));
    }

    [Fact]
    public void AMonthlyRuleStopsAfterItsLastDay()
    {
        var rule = Monthly with { Until = new DateTime(2026, 12, 20) }; var start = new DateTime(2026, 10, 15);
        Assert.Equal(3, rule.Between(start, start, new DateTime(2027, 6, 30)).Count);
    }

    [Fact]
    public void AMonthlyRuleIsStoredTypedAndReadBackAgain()
    {
        foreach (var rule in new[] { Monthly, new Recurrence(1, 0, null, 3), Monthly with { Until = new DateTime(2027, 3, 1) } })
        {
            Assert.Equal(rule, Recurrence.FromRule(rule.ToRule()));
            Assert.Equal(rule, Recurrence.Extract("Pay invoices " + rule.Typed(), new DateTime(2026, 10, 21), out var rest)); Assert.Equal("Pay invoices", rest);
        }
        Assert.Equal("Every month", Monthly.Words()); Assert.Equal("Every 3 months", new Recurrence(1, 0, null, 3).Words());
        Assert.Equal("every month until 2027-03-01", (Monthly with { Until = new DateTime(2027, 3, 1) }).Typed());
    }

    [Fact]
    public void AWeeklyRuleIsStoredExactlyAsBeforeAndBadMonthPartsAreRefused()
    {
        Assert.Equal("1;0;", new Recurrence(1, 0).ToRule()); Assert.Equal("2;0;", new Recurrence(2, 0).ToRule());
        Assert.Equal(new Recurrence(2, 0), Recurrence.FromRule("2;0;"));
        Assert.Null(Recurrence.FromRule("1;0;;0")); Assert.Null(Recurrence.FromRule("1;0;;x")); Assert.Null(Recurrence.FromRule("1;0;;1;1"));
    }

    [Fact]
    public void AnEveryWordInASentenceThatIsNotARepeatIsLeftAlone()
    {
        Assert.Null(Recurrence.Extract("Look at every monthly figure", new DateTime(2026, 10, 21), out var rest)); Assert.Equal("Look at every monthly figure", rest);
        Assert.Equal(new Recurrence(2, 0), Recurrence.Extract("sync every 2 weeks", new DateTime(2026, 10, 21), out _)); // The weeks form still reads.
    }
}
