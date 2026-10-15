using System.Globalization;

namespace Timer.Core;

public sealed record Shift(TimeSpan Start, TimeSpan End);
public sealed record DayOverride(bool Off, Shift? Shift);
public sealed record AwayInterval(DateTime Start, DateTime? End);
public sealed record ActiveSession(string Account, DateTime Start);
public sealed record DayPlan(bool Working, DateTime Start, DateTime End);
public sealed record DayTotals(TimeSpan Working, TimeSpan Overtime, TimeSpan Allocated, TimeSpan Unallocated)
{
    public TimeSpan Elapsed => Working + Overtime;
}
public sealed class WorkSchedule
{
    public Shift Default { get; set; } = new(TimeSpan.FromHours(9), TimeSpan.FromHours(17));
    public HashSet<DayOfWeek> Days { get; set; } = new() { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday };
    public Dictionary<DayOfWeek, Shift> Shifts { get; set; } = new();
    public Dictionary<DateTime, DayOverride> Overrides { get; set; } = new();
    /// <summary>The working time that falls between two moments, by the scheduled hours of each day in between (days off and overrides included).</summary>
    public TimeSpan WorkingTimeBetween(DateTime from, DateTime to)
    {
        if (to <= from) return TimeSpan.Zero;
        var total = TimeSpan.Zero;
        // From the day before, since an overnight shift that began then can still be running at the start.
        for (var day = from.Date.AddDays(-1); day <= to.Date; day = day.AddDays(1))
        {
            var plan = Scheduled(day); if (!plan.Working) continue;
            var start = plan.Start > from ? plan.Start : from; var end = plan.End < to ? plan.End : to;
            if (end > start) total += end - start;
        }
        return total;
    }
    /// <summary>
    /// A shift on a day. One that ends at or before its start (22:00 to 06:00) is an overnight shift: it belongs to the day it starts and ends
    /// the next morning, so its end falls on the following date.
    /// </summary>
    public static DayPlan PlanOf(DateTime day, Shift shift) => new(true, day + shift.Start, shift.End > shift.Start ? day + shift.End : day.AddDays(1) + shift.End);
    /// <summary>Whether a plan runs past midnight into the next day.</summary>
    public static bool IsOvernight(DayPlan plan) => plan.Working && plan.End > plan.Start.Date.AddDays(1);
    public DayPlan Scheduled(DateTime day)
    {
        day = day.Date;
        if (Overrides.TryGetValue(day, out var change))
            return change.Off ? new(false, day, day) : PlanOf(day, change.Shift!);
        var hours = Shifts.GetValueOrDefault(day.DayOfWeek) ?? Default;
        return Days.Contains(day.DayOfWeek) ? PlanOf(day, hours) : new(false, day, day);
    }
    /// <summary>The part of yesterday's overnight shift that falls on this day (from midnight to its end), or null when there is none.</summary>
    public DayPlan? Tail(DateTime day)
    {
        day = day.Date; var previous = Scheduled(day.AddDays(-1));
        return IsOvernight(previous) ? new DayPlan(true, day, previous.End) : null;
    }
}

// Minutes is the authoritative credited duration.
public sealed record SessionRow(string Date, string Account, string Start, string End, string Minutes)
{
    public DateTime Day => DateTime.ParseExact(Date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    public DateTime From => Day + TimeSpan.ParseExact(Start, "hh\\:mm\\:ss", CultureInfo.InvariantCulture);
    public TimeSpan Duration => TimeSpan.FromMinutes(double.Parse(Minutes, CultureInfo.InvariantCulture));
    public DateTime To => From + Duration;
}

public sealed class TrackingEngine
{
    private readonly Func<DateTime> clock;
    public WorkSchedule Schedule { get; }
    public List<SessionRow> Sessions { get; } = new();
    public Dictionary<DateTime, DateTime> DayStarts { get; } = new();
    public List<AwayInterval> Away { get; } = new();
    public ActiveSession? Active { get; private set; }
    public DateTime Now => clock();
    public TrackingEngine(WorkSchedule? schedule = null, Func<DateTime>? clock = null)
    { Schedule = schedule ?? new(); this.clock = clock ?? (() => DateTime.Now); }

    public DayPlan Plan(DateTime day)
    {
        var plan = Schedule.Scheduled(day);
        if (!DayStarts.TryGetValue(day.Date, out var manual)) return plan;
        var end = plan.Working ? plan.End : WorkSchedule.PlanOf(day.Date, Schedule.Default).End;
        return new(true, manual, end < manual ? manual : end);
    }
    /// <summary>
    /// How long a shift's overtime may run on after its scheduled end before the day is taken to be over by itself. A day shift's overtime stops at midnight,
    /// where the day changes; an overnight shift has no midnight to stop it, so it stops this long after the shift ends, or when the next shift starts.
    /// </summary>
    public static readonly TimeSpan OvertimeTail = TimeSpan.FromHours(6);
    /// <summary>
    /// The date of the working day a moment belongs to. That is its own date, except in the early hours after an overnight shift from yesterday, when it is
    /// yesterday's: the shift's clock, finishing and starting all carry on across midnight, and the day changes when the next shift starts.
    /// </summary>
    public DateTime ShiftKey(DateTime at)
    {
        var today = at.Date; var previous = Plan(today.AddDays(-1));
        if (WorkSchedule.IsOvernight(previous) && at < previous.End + OvertimeTail)
        {
            var own = Plan(today);
            if (!(own.Working && at >= own.Start)) return today.AddDays(-1);
        }
        return today;
    }
    /// <summary>The working day now belongs to, which is today unless an overnight shift from yesterday is still going.</summary>
    public DateTime CurrentKey => ShiftKey(Now);
    private bool AwayOf(AwayInterval away, DateTime key, DayPlan plan)
    {
        if (WorkSchedule.IsOvernight(plan)) return away.Start >= plan.Start;
        // A day shift counts a finish made on its own date, but not one made in the small hours that closed last night's overnight shift.
        return away.Start.Date == key && !(Schedule.Tail(key) is { } tail && away.Start < tail.End + OvertimeTail);
    }
    /// <summary>
    /// Where an away spell that has not been ended stops by itself. A day's finish stops at midnight, where the day ends; one made during an overnight shift
    /// (or in its overtime after midnight) stops when the next shift starts, so finishing at 06:05 does not also stop tonight's shift.
    /// </summary>
    private DateTime AwayBoundary(AwayInterval away)
    {
        foreach (var key in new[] { away.Start.Date, away.Start.Date.AddDays(-1) })
        {
            var plan = Plan(key);
            if (!WorkSchedule.IsOvernight(plan) || away.Start < plan.Start || away.Start >= plan.End + OvertimeTail) continue;
            for (var day = key.AddDays(1); day <= key.AddDays(3); day = day.AddDays(1)) if (Plan(day) is { Working: true } next && next.Start > away.Start) return next.Start;
            return plan.End + OvertimeTail;
        }
        return away.Start.Date.AddDays(1);
    }
    private DateTime AwayEnd(AwayInterval away, DateTime now) { if (away.End is { } end) return end; var boundary = AwayBoundary(away); return now < boundary ? now : boundary; }
    private bool FinishedFor(DateTime key) { var plan = Plan(key); return Away.Any(a => a.End is null && AwayOf(a, key, plan)); }
    public bool Finished => FinishedFor(CurrentKey);
    public bool DayRunning { get { var key = CurrentKey; var plan = Plan(key); return plan.Working && Now >= plan.Start && !FinishedFor(key); } }
    public TimeSpan ActiveElapsed => Active is { } active ? Positive(Now - active.Start) : TimeSpan.Zero;

    public void StartDay()
    {
        var now = Now; var key = ShiftKey(now);
        var plan = Plan(key);
        if (!plan.Working || now < plan.Start) DayStarts[key] = now;
        for (var i = 0; i < Away.Count; i++)
            if (Away[i].End is null && AwayOf(Away[i], key, plan)) Away[i] = Away[i] with { End = now };
    }
    public void FinishDay()
    {
        if (!DayRunning) return;
        StopAccount();
        Away.Add(new(Now, null));
    }
    /// <summary>The account to go back to when the current break ends, or null when no break is running or nothing was running before it.</summary>
    public string? ResumeAfterBreak { get; private set; }
    public void ToggleAccount(string account)
    {
        var stop = string.Equals(Active?.Account, account, StringComparison.OrdinalIgnoreCase);
        var before = Active?.Account;
        var stoppedEnd = Active is null ? (DateTime?)null : Now;
        StopAccount();
        ResumeAfterBreak = null;
        if (!stop)
        {
            StartDay();
            if (BreakAccount.Is(account) && before is not null && !BreakAccount.Is(before)) ResumeAfterBreak = before;
            var start = Now;
            // A block's length is kept to hundredths of a minute and its start to the second, so the block just stopped can end a fraction of a second
            // after the new one begins, which drew the two side by side as if they overlapped. The new block starts at the whole second
            // after the old one's end instead (a second or so later at most), so consecutive blocks always meet cleanly.
            if (stoppedEnd is not null && LastStoredEnd is { } end && end.Date == start.Date && WholeSecond(start) < end) start = WholeSecond(end, up: true);
            // When the time now is inside an earlier block of this same account, such as one added or pasted ahead of time, pressing play carries that block on
            // instead of starting a second one on top of it. It is reopened from its own start, and stopping ends it where the recording stops.
            ContinuedBlock = null;
            if (FutureBlockOf(account) is { } block) { Sessions.Remove(block); start = block.From; ContinuedBlock = block; }
            Active = new(account, start);
        }
    }
    /// <summary>The block that the last start carried on instead of starting a new one, or null when it started a new one.</summary>
    public SessionRow? ContinuedBlock { get; private set; }
    /// <summary>Lets the caller keep some blocks out of that, such as ones in a group, which the engine knows nothing about.</summary>
    public Func<SessionRow, bool> CanReopen { get; set; } = _ => true;
    /// <summary>
    /// The block of this account that the time now falls inside and that runs on past it, as long as carrying it on would not overlap other
    /// recorded time. A break counts too, so starting one inside a break block already there carries that block on.
    /// </summary>
    public SessionRow? FutureBlockOf(string account)
    {
        var now = Now;
        var block = Sessions.FirstOrDefault(s => s.Account.Equals(account, StringComparison.OrdinalIgnoreCase) && s.From <= now && s.To > now);
        if (block is null || !CanReopen(block)) return null;
        return Sessions.Any(s => !ReferenceEquals(s, block) && s.Duration > TimeSpan.Zero && s.From < now && s.To > block.From) ? null : block;
    }
    private static DateTime WholeSecond(DateTime time, bool up = false)
    {
        var whole = new DateTime(time.Ticks - time.Ticks % TimeSpan.TicksPerSecond, time.Kind);
        return up && whole < time ? whole.AddSeconds(1) : whole;
    }
    public void StopAccount()
    {
        if (Active is { } active) AddSession(active.Account, active.Start, Now);
        Active = null;
    }
    /// <summary>Where the last block written by <see cref="AddSession"/> ends, which can be a little after the moment it was stopped.</summary>
    public DateTime? LastStoredEnd { get; private set; }

    /// <summary>
    /// How short a block and how recent a switch back count as a slip of the finger, for continuing the earlier block.
    /// </summary>
    public static readonly TimeSpan MistakeWindow = TimeSpan.FromMinutes(3);
    /// <summary>
    /// After going from one account to another and back again within <see cref="MistakeWindow"/>, the earlier block of the account now running
    /// and the short block between, or null when the recent history does not look like that. The short block can be a break.
    /// </summary>
    public (SessionRow Earlier, SessionRow Between)? ContinuableBlocks()
    {
        if (Active is not { } active || BreakAccount.Is(active.Account) || Now - active.Start > MistakeWindow) return null;
        var between = Sessions.LastOrDefault(s => s.Duration > TimeSpan.Zero && s.Duration <= MistakeWindow && Math.Abs((s.To - active.Start).TotalSeconds) <= 2
            && !s.Account.Equals(active.Account, StringComparison.OrdinalIgnoreCase));
        if (between is null) return null;
        var earlier = Sessions.LastOrDefault(s => s.Duration > TimeSpan.Zero && s.Account.Equals(active.Account, StringComparison.OrdinalIgnoreCase) && s.Day == between.Day
            && Math.Abs((s.To - between.From).TotalSeconds) <= 2);
        return earlier is null ? null : (earlier, between);
    }
    /// <summary>
    /// Takes back the switch: the short block between is dropped and the earlier block is reopened, so the account carries on as one block
    /// that includes the time of the slip. The earlier block's note, which is kept by its start, stays with it. Returns the two blocks removed.
    /// </summary>
    public (SessionRow Earlier, SessionRow Between)? ContinuePrevious()
    {
        if (ContinuableBlocks() is not { } blocks || Active is not { } active) return null;
        Sessions.Remove(blocks.Between); Sessions.Remove(blocks.Earlier);
        Active = new(active.Account, blocks.Earlier.From);
        return blocks;
    }
    public void Recover(ActiveSession? active, DateTime? lastSeen)
    {
        if (active is not null && lastSeen > active.Start) AddSession(active.Account, active.Start, lastSeen.Value);
        Active = null;
        for (var i = 0; i < Away.Count; i++)
            if (Away[i].End is null && AwayBoundary(Away[i]) <= Now) Away[i] = Away[i] with { End = AwayBoundary(Away[i]) };
    }
    public void AddSession(string account, DateTime start, DateTime end)
    {
        // Match the established midnight split and minimum three-second recording threshold.
        while (start < end)
        {
            var cap = end < start.Date.AddDays(1) ? end : start.Date.AddDays(1);
            var minutes = (cap - start).TotalMinutes;
            if (minutes >= .05)
            {
                var row = new SessionRow(start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), account, start.ToString("HH:mm:ss", CultureInfo.InvariantCulture), cap.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                    Math.Round(minutes, 2, MidpointRounding.ToEven).ToString(CultureInfo.InvariantCulture));
                Sessions.Add(row); LastStoredEnd = row.To;
            }
            start = cap;
        }
    }
    public TimeSpan Credited(string account, DateTime from, DateTime until)
    {
        var credited = Sessions.Where(s => s.Account.Equals(account, StringComparison.OrdinalIgnoreCase) && s.Day >= from.Date && s.Day < until.Date)
            .Aggregate(TimeSpan.Zero, (sum, s) => sum + s.Duration);
        if (Active is { } active && active.Account.Equals(account, StringComparison.OrdinalIgnoreCase))
            credited += Intersection(active.Start, Now, from.Date, until.Date);
        return credited;
    }
    /// <summary>How much of the recording that is running now falls on this day, or nothing when it is a break or nothing is running. Saved entries do not include it yet.</summary>
    public TimeSpan ActiveElapsedOn(DateTime day, bool includeBreak = false)
        => Active is { } active && (includeBreak || !BreakAccount.Is(active.Account)) ? Intersection(active.Start, Now, day.Date, day.Date.AddDays(1)) : TimeSpan.Zero;
    /// <summary>Whether an overnight shift touches this calendar day: it starts on it, or one from the day before runs into it.</summary>
    private bool OvernightTouches(DateTime day) => Schedule.Tail(day) is not null || WorkSchedule.IsOvernight(Plan(day));

    /// <summary>
    /// A calendar day's totals. With no overnight shift about, a day is its own shift. With one, the day holds two pieces, the tail of last night's shift
    /// (midnight to its end) and the start of tonight's (its start to midnight), each measured as its own working time and overtime.
    /// </summary>
    public DayTotals Totals(DateTime day)
    {
        day = day.Date;
        if (!OvernightTouches(day)) return DayOnlyTotals(day);
        var allocated = AllocatedIn(day, day.AddDays(1));
        var work = TimeSpan.Zero; var overtime = TimeSpan.Zero;
        if (Schedule.Tail(day) is not null) { var (w, o) = MeasureShift(day.AddDays(-1), day, day.AddDays(1)); work += w; overtime += o; }
        var own = Plan(day);
        if (own.Working) { var (w, o) = MeasureShift(day, day, day.AddDays(1)); work += w; overtime += o; }
        if (work == TimeSpan.Zero && overtime == TimeSpan.Zero && !own.Working && Schedule.Tail(day) is null) return new(allocated, TimeSpan.Zero, allocated, TimeSpan.Zero);
        return new(work, overtime, allocated, Positive(work + overtime - allocated));
    }
    /// <summary>
    /// The totals of a working day as a shift: what the clock shows. It is the calendar day, except for an overnight shift, which runs from its start
    /// across midnight to its end and its overtime; a day shift that follows a night shift leaves out the night shift's tail.
    /// </summary>
    public DayTotals ShiftTotals(DateTime key)
    {
        key = key.Date;
        if (!OvernightTouches(key)) return DayOnlyTotals(key);
        var plan = Plan(key);
        var (from, until) = ShiftWindow(key);
        var allocated = AllocatedIn(from, until);
        if (!plan.Working) return new(allocated, TimeSpan.Zero, allocated, TimeSpan.Zero);
        var (work, overtime) = MeasureShift(key, from, until);
        return new(work, overtime, allocated, Positive(work + overtime - allocated));
    }
    /// <summary>
    /// The span of clock time a working day owns, for adding up its allocated time and its credit: the calendar day, or for an overnight shift from its start
    /// through its end and the overtime tail, with a day shift after a night shift starting where the night shift's tail and overtime stop.
    /// </summary>
    public (DateTime From, DateTime Until) ShiftWindow(DateTime key)
    {
        key = key.Date; var plan = Plan(key);
        var from = key;
        if (Schedule.Tail(key) is { } tail) from = plan.Working ? Min(tail.End + OvertimeTail, plan.Start) : tail.End + OvertimeTail;
        var until = WorkSchedule.IsOvernight(plan) ? plan.End + OvertimeTail : key.AddDays(1);
        // The next shift starts where this one's overtime tail stops, so the two windows never overlap.
        if (WorkSchedule.IsOvernight(plan) && Plan(key.AddDays(1)) is { Working: true } next && next.Start < until && next.Start > plan.End) until = next.Start;
        return (from, until);
    }
    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
    /// <summary>The credited time on accounts (breaks left out) in a span of clock time, including the recording running now.</summary>
    private TimeSpan AllocatedIn(DateTime from, DateTime until)
    {
        var total = Sessions.Where(s => !BreakAccount.Is(s.Account)).Aggregate(TimeSpan.Zero, (sum, s) => sum + (s.From >= from && s.To <= until ? s.Duration : Intersection(s.From, s.To, from, until)));
        if (Active is { } active && !BreakAccount.Is(active.Account)) total += Intersection(active.Start, Now, from, until);
        return total;
    }
    /// <summary>The working time and overtime of one shift that fall inside a span of clock time (a calendar day, or the whole shift).</summary>
    private (TimeSpan Work, TimeSpan Overtime) MeasureShift(DateTime key, DateTime clipFrom, DateTime clipUntil)
    {
        var plan = Plan(key); if (!plan.Working) return (TimeSpan.Zero, TimeSpan.Zero);
        var now = Now;
        // Where the shift's overtime may run to: up to now for the shift under way, but never past the tail (or midnight, for a day shift).
        var limit = WorkSchedule.IsOvernight(plan) ? plan.End + OvertimeTail : key.AddDays(1);
        if (WorkSchedule.IsOvernight(plan) && Plan(key.AddDays(1)) is { Working: true } next && next.Start < limit && next.Start > plan.End) limit = next.Start;
        var cap = now < limit ? now : limit;
        // A shift that is over has no "now" to run overtime up to: its overtime stops at the last recorded activity, or the end of the shift if that came later.
        if (limit <= now || ShiftKey(now) != key)
        {
            var last = Sessions.Where(s => s.Duration > TimeSpan.Zero && s.To > plan.Start && s.From < limit).Select(s => s.To).DefaultIfEmpty(DateTime.MinValue).Max();
            var end = plan.End > last ? plan.End : last; cap = end < limit ? end : limit;
            if (ShiftKey(now) != key && now < plan.Start) cap = now; // A shift still to come has run for no time at all.
        }
        var blocked = Sessions.Where(s => BreakAccount.Is(s.Account) && s.Duration > TimeSpan.Zero && s.To > plan.Start && s.From < limit).Select(s => (s.From, s.To)).ToList();
        blocked.AddRange(Away.Select(a => (a.Start, AwayEnd(a, now))));
        if (Active is { } lunch && BreakAccount.Is(lunch.Account)) blocked.Add((lunch.Start, now));
        var start = Max(plan.Start, clipFrom);
        if (cap <= start) return (TimeSpan.Zero, TimeSpan.Zero);
        var regularEnd = Min(Min(cap, plan.End), clipUntil);
        var work = regularEnd > start ? Positive(regularEnd - start - Blocked(blocked, start, regularEnd)) : TimeSpan.Zero;
        var overtimeFrom = Max(plan.End, clipFrom); var overtimeTo = Min(cap, clipUntil);
        var overtime = overtimeTo > overtimeFrom ? Positive(overtimeTo - overtimeFrom - Blocked(blocked, overtimeFrom, overtimeTo)) : TimeSpan.Zero;
        return (work, overtime);
    }
    /// <summary>The credited time of an account in a span of clock time, partial blocks cut at the edges, including the recording running now.</summary>
    public TimeSpan CreditedIn(string account, DateTime from, DateTime until)
    {
        var total = Sessions.Where(s => s.Account.Equals(account, StringComparison.OrdinalIgnoreCase)).Aggregate(TimeSpan.Zero, (sum, s) => sum + (s.From >= from && s.To <= until ? s.Duration : Intersection(s.From, s.To, from, until)));
        if (Active is { } active && active.Account.Equals(account, StringComparison.OrdinalIgnoreCase)) total += Intersection(active.Start, Now, from, until);
        return total;
    }
    private DayTotals DayOnlyTotals(DateTime day)
    {
        day = day.Date;
        var plan = Plan(day);
        var now = Now;
        var cap = now < day.AddDays(1) ? now : day.AddDays(1);
        // A past day has no "now" to run overtime up to. Without this it counted everything after the shift ended, right up to
        // midnight, as overtime. Its overtime stops at the last recorded activity, or the end of the shift if that came later.
        if (day < now.Date)
        {
            var lastActivity = Sessions.Where(s => s.Day == day && s.Duration > TimeSpan.Zero).Select(s => s.To).DefaultIfEmpty(DateTime.MinValue).Max();
            var end = plan.Working && plan.End > lastActivity ? plan.End : lastActivity;
            cap = end < cap ? end : cap;
        }
        var allocated = Sessions.Where(s => s.Day == day && !BreakAccount.Is(s.Account)).Aggregate(TimeSpan.Zero, (sum, s) => sum + s.Duration);
        if (Active is { } active && !BreakAccount.Is(active.Account)) allocated += Intersection(active.Start, now, day, day.AddDays(1));
        // On a day that is off in the schedule there is no working day to measure against, so the day is just the time actually tracked.
        if (!Schedule.Scheduled(day).Working) return new(allocated, TimeSpan.Zero, allocated, TimeSpan.Zero);
        if (!plan.Working || cap <= plan.Start) return new(TimeSpan.Zero, TimeSpan.Zero, allocated, TimeSpan.Zero);
        var blocked = Sessions.Where(s => s.Day == day && BreakAccount.Is(s.Account) && s.Duration > TimeSpan.Zero)
            .Select(s => (s.From, s.To)).ToList();
        blocked.AddRange(Away.Select(a => (a.Start, AwayEnd(a, now))));
        if (Active is { } lunch && BreakAccount.Is(lunch.Account)) blocked.Add((lunch.Start, now));
        var regularEnd = cap < plan.End ? cap : plan.End;
        var work = Positive(regularEnd - plan.Start - Blocked(blocked, plan.Start, regularEnd));
        var overtime = cap > plan.End ? Positive(cap - plan.End - Blocked(blocked, plan.End, cap)) : TimeSpan.Zero;
        return new(work, overtime, allocated, Positive(work + overtime - allocated));
    }
    private static TimeSpan Blocked(IEnumerable<(DateTime Start, DateTime End)> spans, DateTime from, DateTime until)
    {
        // Union exclusions before subtraction, so overlapping lunch/away cannot be counted twice.
        var intervals = spans.Select(s => (Start: s.Start < from ? from : s.Start, End: s.End > until ? until : s.End))
            .Where(s => s.End > s.Start).OrderBy(s => s.Start).ToArray();
        var total = TimeSpan.Zero;
        var end = from;
        foreach (var span in intervals)
        {
            var start = span.Start < end ? end : span.Start;
            if (span.End > start) total += span.End - start;
            if (span.End > end) end = span.End;
        }
        return total;
    }
    private static TimeSpan Intersection(DateTime start, DateTime end, DateTime from, DateTime until)
        => Positive((end < until ? end : until) - (start > from ? start : from));
    private static TimeSpan Positive(TimeSpan span) => span < TimeSpan.Zero ? TimeSpan.Zero : span;
}

/// <summary>
/// A break is a pause of any kind. In memory it is an entry under the reserved name Break, which no account may use.
/// The database stores it as its own kind of entry with no account.
/// </summary>
public static class BreakAccount
{
    public const string Name = "Break";
    public const string Label = "Break";
    public static bool Is(string? account) => string.Equals(account, Name, StringComparison.OrdinalIgnoreCase);
    public static string Display(string account) => Is(account) ? Label : account;
    /// <summary>Turns a name chosen from a list back into the stored name, so picking "Break" means the break account.</summary>
    public static string Stored(string account) => string.Equals(account, Label, StringComparison.OrdinalIgnoreCase) ? Name : account;
}
