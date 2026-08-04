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
    {
        _calendar = project.DefaultCalendar ?? project.Calendars.FirstOrDefault();

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
}
