using System.Globalization;
using MPXJ.Net;

namespace Horizun.ProjectMcp.Writes;

/// <summary>
/// The working week and the working hours of a calendar: which days are worked and when.
/// </summary>
/// <remarks>
/// The calendar decides every date, and a site that works Saturday mornings, or a recovery that
/// tries full Saturdays, could not be expressed at all: only whole non-working exceptions could.
/// Days are named in English or Spanish (mon-sat, lun-sab) and hours as "08:00-12:00,13:00-17:00";
/// no hours means the day is not worked.
/// </remarks>
public static class CalendarEdits
{
    private static readonly Dictionary<string, DayOfWeek> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mon"] = DayOfWeek.Monday, ["monday"] = DayOfWeek.Monday, ["lun"] = DayOfWeek.Monday, ["lunes"] = DayOfWeek.Monday,
        ["tue"] = DayOfWeek.Tuesday, ["tuesday"] = DayOfWeek.Tuesday, ["mar"] = DayOfWeek.Tuesday, ["martes"] = DayOfWeek.Tuesday,
        ["wed"] = DayOfWeek.Wednesday, ["wednesday"] = DayOfWeek.Wednesday, ["mie"] = DayOfWeek.Wednesday,
        ["mié"] = DayOfWeek.Wednesday, ["miercoles"] = DayOfWeek.Wednesday, ["miércoles"] = DayOfWeek.Wednesday,
        ["thu"] = DayOfWeek.Thursday, ["thursday"] = DayOfWeek.Thursday, ["jue"] = DayOfWeek.Thursday, ["jueves"] = DayOfWeek.Thursday,
        ["fri"] = DayOfWeek.Friday, ["friday"] = DayOfWeek.Friday, ["vie"] = DayOfWeek.Friday, ["viernes"] = DayOfWeek.Friday,
        ["sat"] = DayOfWeek.Saturday, ["saturday"] = DayOfWeek.Saturday, ["sab"] = DayOfWeek.Saturday,
        ["sáb"] = DayOfWeek.Saturday, ["sabado"] = DayOfWeek.Saturday, ["sábado"] = DayOfWeek.Saturday,
        ["sun"] = DayOfWeek.Sunday, ["sunday"] = DayOfWeek.Sunday, ["dom"] = DayOfWeek.Sunday, ["domingo"] = DayOfWeek.Sunday,
    };

    // Monday first, as a working week is read.
    private static readonly DayOfWeek[] Week =
    {
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday,
    };

    /// <summary>"mon-fri", "sat", "lun,mie,vie", "mon-fri,sat". Null when a name is not a day.</summary>
    public static IReadOnlyList<DayOfWeek>? ParseDays(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var days = new List<DayOfWeek>();
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var range = part.Split('-', StringSplitOptions.TrimEntries);
            if (range.Length == 1 && Names.TryGetValue(range[0], out var single))
            {
                days.Add(single);
            }
            else if (range.Length == 2 && Names.TryGetValue(range[0], out var from) && Names.TryGetValue(range[1], out var to))
            {
                var i = Array.IndexOf(Week, from);
                var j = Array.IndexOf(Week, to);
                if (i > j)
                {
                    return null;
                }

                days.AddRange(Week[i..(j + 1)]);
            }
            else
            {
                return null;
            }
        }

        return days.Distinct().ToList();
    }

    /// <summary>"08:00-12:00,13:00-17:00"; empty or "none" is no working time. Null when malformed or
    /// when the ranges overlap or run backwards.</summary>
    public static IReadOnlyList<(TimeOnly From, TimeOnly To)>? ParseHours(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<(TimeOnly, TimeOnly)>();
        }

        var ranges = new List<(TimeOnly, TimeOnly)>();
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var ends = part.Split('-', StringSplitOptions.TrimEntries);
            if (ends.Length != 2 || !TimeOnly.TryParse(ends[0], CultureInfo.InvariantCulture, out var from))
            {
                return null;
            }

            // 24:00 (or 00:00 as an end) is how a planner writes the end of the day; TimeOnly cannot
            // hold 24:00, so it is read as the last moment of the day.
            TimeOnly to;
            if (ends[1] is "24:00" || ends[1] is "00:00" && from > TimeOnly.MinValue)
            {
                to = TimeOnly.MaxValue;
            }
            else if (!TimeOnly.TryParse(ends[1], CultureInfo.InvariantCulture, out to))
            {
                return null;
            }

            if (to <= from || ranges.Count > 0 && from < ranges[^1].Item2)
            {
                return null;
            }

            ranges.Add((from, to));
        }

        return ranges;
    }

    /// <summary>Sets the working time of the given days, replacing what they had.</summary>
    public static void SetWeek(ProjectCalendar calendar, IEnumerable<DayOfWeek> days, IReadOnlyList<(TimeOnly From, TimeOnly To)> hours)
    {
        foreach (var day in days)
        {
            calendar.RemoveCalendarHours(day);
            calendar.SetCalendarDayType(day, hours.Count > 0 ? DayType.Working : DayType.NonWorking);
            if (hours.Count == 0)
            {
                continue;
            }

            var ranges = calendar.AddCalendarHours(day);
            foreach (var (from, to) in hours)
            {
                ranges.Add(new TimeOnlyRange(from, to));
            }
        }
    }

    /// <summary>Copies a calendar's working week, as it resolves through its own base, and its
    /// exceptions into another.</summary>
    public static void CopyInto(ProjectCalendar source, ProjectCalendar target)
    {
        foreach (var day in Week)
        {
            var hours = ParseHours(Describe(source, day)) ?? Array.Empty<(TimeOnly, TimeOnly)>();
            SetWeek(target, new[] { day }, hours);
        }

        for (var from = source; from is not null; from = from.Parent)
        {
            foreach (var exception in from.CalendarExceptions)
            {
                if (exception.FromDate is not { } start || target.GetException(start) is not null)
                {
                    continue;
                }

                var copy = target.AddCalendarException(start, exception.ToDate ?? start);
                foreach (var range in exception)
                {
                    copy.Add(range);
                }
            }
        }
    }

    /// <summary>What a day of the week works on this calendar, following its base calendar — as
    /// "08:00-12:00,13:00-17:00", or "" for a day off.</summary>
    public static string Describe(ProjectCalendar calendar, DayOfWeek day)
    {
        try
        {
            if (!calendar.IsWorkingDay(day))
            {
                return string.Empty;
            }

            var hours = calendar.GetHours(day);
            return hours is null ? string.Empty : Format(hours);
        }
        catch
        {
            return string.Empty;
        }
    }

    public static string Describe(IReadOnlyList<(TimeOnly From, TimeOnly To)> hours) =>
        string.Join(",", hours.Select(h => $"{h.From:HH\\:mm}-{End(h.To)}"));

    public static string Format(IEnumerable<TimeOnlyRange> hours) =>
        string.Join(",", hours.Where(h => h.Start is not null && h.End is not null)
            .Select(h => $"{h.Start!.Value:HH\\:mm}-{End(h.End!.Value)}"));

    private static string End(TimeOnly to) => to >= new TimeOnly(23, 59) || to == TimeOnly.MinValue ? "24:00" : to.ToString("HH\\:mm");
}
