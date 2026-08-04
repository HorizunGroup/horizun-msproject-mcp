using MPXJ.Net;

namespace Horizun.ProjectMcp.Analysis;

/// <summary>
/// Working-day arithmetic over a project calendar, with a Monday-to-Friday fallback when the
/// schedule carries no usable calendar.
/// </summary>
/// <remarks>
/// Every date the scheduler produces goes through here, so a site shutdown or a rainy-season
/// exception added with calendars_write actually moves the dates rather than being decorative.
/// </remarks>
public sealed class WorkingCalendar
{
    private readonly ProjectCalendar? _calendar;
    private readonly HashSet<DateOnly> _nonWorking = new();

    public const int StartHour = 8;
    public const int FinishHour = 17;

    public WorkingCalendar(ProjectFile project)
        : this(ResolveDefault(project))
    {
    }

    public WorkingCalendar(ProjectCalendar? calendar)
    {
        _calendar = calendar;

        if (_calendar is null)
        {
            return;
        }

        // Materialise the exception dates once; asking the calendar per day is far too slow
        // across a forward and backward pass over thousands of tasks.
        try
        {
            foreach (var exception in _calendar.CalendarExceptions)
            {
                if (exception.Working)
                {
                    continue;
                }

                if (exception.FromDate is not { } from)
                {
                    continue;
                }

                var to = exception.ToDate ?? from;
                for (var day = from; day <= to; day = day.AddDays(1))
                {
                    _nonWorking.Add(day);
                    if (_nonWorking.Count > 20000)
                    {
                        return;
                    }
                }
            }
        }
        catch
        {
            // A calendar we cannot read degrades to the weekday fallback rather than failing.
        }
    }

    /// <summary>Name of the calendar actually in use, for diagnostics.</summary>
    public string? Name => _calendar?.Name;

    /// <summary>
    /// Hours in a working day according to this calendar. Durations are stored in minutes or
    /// hours and have to be turned into days somehow; assuming eight is wrong on any site
    /// running longer shifts, and the error compounds along a chain.
    /// </summary>
    public double HoursPerDay => _hoursPerDay ??= ComputeHoursPerDay();

    private double? _hoursPerDay;

    private double ComputeHoursPerDay()
    {
        {
            if (_calendar is null)
            {
                return 8.0;
            }

            foreach (var day in Enum.GetValues<DayOfWeek>())
            {
                try
                {
                    if (!_calendar.IsWorkingDay(day))
                    {
                        continue;
                    }

                    var work = _calendar.GetWork(day, TimeUnit.Hours);
                    if (work is not null && work.DurationValue > 0)
                    {
                        return work.DurationValue;
                    }
                }
                catch
                {
                    // Fall through to the default.
                }
            }

            return 8.0;
        }
    }

    /// <summary>Which weekdays this calendar treats as working.</summary>
    public IReadOnlyList<string> WorkingDaysOfWeek()
    {
        // Probe a real week rather than reading the day-type table, so exceptions and any
        // quirk of how the file stores its pattern are reflected in the answer.
        var monday = DateTime.Today;
        while (monday.DayOfWeek != DayOfWeek.Monday) monday = monday.AddDays(1);

        return Enumerable.Range(0, 7)
            .Select(i => monday.AddDays(i))
            .Where(IsWorking)
            .Select(d => d.DayOfWeek.ToString())
            .ToList();
    }

    public bool IsWorking(DateTime date)
    {
        var day = DateOnly.FromDateTime(date);
        if (_nonWorking.Contains(day))
        {
            return false;
        }

        if (_calendar is not null)
        {
            try
            {
                return _calendar.IsWorkingDate(day);
            }
            catch
            {
                // fall through to the weekday rule
            }
        }

        return date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
    }

    public DateTime NextWorkingDay(DateTime from)
    {
        var day = from.Date;
        for (var guard = 0; guard < 3650 && !IsWorking(day); guard++)
        {
            day = day.AddDays(1);
        }

        return day;
    }

    public DateTime PreviousWorkingDay(DateTime from)
    {
        var day = from.Date;
        for (var guard = 0; guard < 3650 && !IsWorking(day); guard++)
        {
            day = day.AddDays(-1);
        }

        return day;
    }

    /// <summary>
    /// The finish of a task that starts on <paramref name="start"/> and lasts
    /// <paramref name="durationDays"/> working days, counting the start day as the first.
    /// A five-day task starting Monday finishes Friday.
    /// </summary>
    public DateTime AddWorkingDays(DateTime start, double durationDays)
    {
        var day = NextWorkingDay(start);
        var remaining = (int)Math.Ceiling(durationDays);

        if (remaining <= 1)
        {
            return day;
        }

        remaining--;
        for (var guard = 0; remaining > 0 && guard < 36500; guard++)
        {
            day = day.AddDays(1);
            if (IsWorking(day))
            {
                remaining--;
            }
        }

        return day;
    }

    /// <summary>Steps back <paramref name="durationDays"/> working days from a finish date.</summary>
    public DateTime SubtractWorkingDays(DateTime finish, double durationDays)
    {
        var day = PreviousWorkingDay(finish);
        var remaining = (int)Math.Ceiling(durationDays);

        if (remaining <= 1)
        {
            return day;
        }

        remaining--;
        for (var guard = 0; remaining > 0 && guard < 36500; guard++)
        {
            day = day.AddDays(-1);
            if (IsWorking(day))
            {
                remaining--;
            }
        }

        return day;
    }

    /// <summary>Shifts by whole working days; negative values move backwards. Used for lag.</summary>
    public DateTime Shift(DateTime from, double workingDays)
    {
        var steps = (int)Math.Round(workingDays);
        var day = from.Date;

        for (var guard = 0; steps != 0 && guard < 36500; guard++)
        {
            day = day.AddDays(steps > 0 ? 1 : -1);
            if (IsWorking(day))
            {
                steps += steps > 0 ? -1 : 1;
            }
        }

        return day;
    }

    public double WorkingDaysBetween(DateTime from, DateTime to)
    {
        var sign = to >= from ? 1 : -1;
        var (start, end) = sign > 0 ? (from.Date, to.Date) : (to.Date, from.Date);

        var count = 0;
        for (var day = start; day < end; day = day.AddDays(1))
        {
            if (IsWorking(day))
            {
                count++;
            }
        }

        return count * sign;
    }

    public static DateTime AtStart(DateTime day) =>
        new(day.Year, day.Month, day.Day, StartHour, 0, 0);

    public static DateTime AtFinish(DateTime day) =>
        new(day.Year, day.Month, day.Day, FinishHour, 0, 0);

    /// <summary>
    /// The calendar the project actually runs on. Files often carry a "Standard" calendar they do
    /// not use alongside the real one, so where no default is flagged, pick the calendar the tasks
    /// themselves reference most rather than whichever happens to be first.
    /// </summary>
    private static ProjectCalendar? ResolveDefault(ProjectFile project)
    {
        if (project.DefaultCalendar is { } declared)
        {
            return declared;
        }

        var mostUsed = project.Tasks
            .Select(t => t.Calendar)
            .Where(c => c is not null)
            .GroupBy(c => c!.UniqueID)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault()?.First();

        return mostUsed ?? project.Calendars.FirstOrDefault();
    }
}

/// <summary>
/// Hands out the right working calendar for each task and caches it.
/// </summary>
/// <remarks>
/// Real construction schedules mix calendars — a six-day site week, a night shift, a 24-hour
/// calendar for curing — and a task carrying its own calendar is the normal case, not the
/// exception. Scheduling everything against one calendar was measurably wrong: on a six-day
/// schedule it moved more than half the tasks by a week or more.
/// </remarks>
public sealed class CalendarSet
{
    private readonly Dictionary<int, WorkingCalendar> _byCalendarUid = new();
    private readonly WorkingCalendar _default;

    public CalendarSet(ProjectFile project)
    {
        _default = new WorkingCalendar(project);
    }

    public WorkingCalendar Default => _default;

    public WorkingCalendar For(MPXJ.Net.Task task)
    {
        var calendar = task.Calendar;
        if (calendar?.UniqueID is not { } uid)
        {
            return _default;
        }

        if (!_byCalendarUid.TryGetValue(uid, out var working))
        {
            working = new WorkingCalendar(calendar);
            _byCalendarUid[uid] = working;
        }

        return working;
    }
}
