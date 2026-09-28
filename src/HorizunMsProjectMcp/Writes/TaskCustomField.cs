using System.Globalization;
using System.Text.RegularExpressions;

namespace Horizun.ProjectMcp.Writes;

/// <summary>
/// A task field tasks_write sets through <c>custom</c>: Text1-30, Number1-20, Date1-10, Flag1-20, WBS.
/// </summary>
/// <remarks>
/// Every key used to be read as a text slot by its digits, so Number1 landed in Text1 and WBS in Text1
/// too — reported as written, and wrong. A key now either names a field of its own kind or is refused.
/// </remarks>
public sealed partial record TaskCustomField(string Kind, int Slot)
{
    [GeneratedRegex(@"^\s*(text|number|date|flag)\s*(\d{1,2})\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex Slotted();

    public static TaskCustomField? Parse(string field)
    {
        if (field.Trim().Equals("wbs", StringComparison.OrdinalIgnoreCase))
        {
            return new TaskCustomField("wbs", 0);
        }

        var match = Slotted().Match(field);
        if (!match.Success)
        {
            return null;
        }

        var kind = match.Groups[1].Value.ToLowerInvariant();
        var slot = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var max = kind switch { "text" => 30, "number" => 20, "date" => 10, _ => 20 };
        return slot >= 1 && slot <= max ? new TaskCustomField(kind, slot) : null;
    }

    public bool TryConvert(string raw, out object? value)
    {
        value = null;
        switch (Kind)
        {
            case "number":
                if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                {
                    value = number;
                    return true;
                }

                return false;

            case "flag":
                var flag = raw.Trim().ToLowerInvariant();
                if (flag is "true" or "1" or "yes" or "sí" or "si") { value = true; return true; }
                if (flag is "false" or "0" or "no") { value = false; return true; }
                return false;

            case "date":
                value = Tools.QueryTools.ParseDate(raw);
                return value is not null;

            default:
                value = raw;
                return true;
        }
    }

    public void Write(MPXJ.Net.Task task, object? value)
    {
        switch (Kind)
        {
            case "text": task.SetText(Slot, (string?)value); break;
            case "number": task.SetNumber(Slot, (double?)value); break;
            case "flag": task.SetFlag(Slot, (bool)value!); break;
            case "date": task.SetDate(Slot, (DateTime)value!); break;
            default: task.WBS = (string?)value; break;
        }
    }

    public object? Read(MPXJ.Net.Task task)
    {
        try
        {
            return Kind switch
            {
                "text" => task.GetText(Slot),
                "number" => task.GetNumber(Slot) is { } n ? Convert.ToDouble(n, CultureInfo.InvariantCulture) : null,
                "flag" => task.GetFlag(Slot),
                "date" => task.GetDate(Slot),
                _ => task.WBS,
            };
        }
        catch
        {
            return null;
        }
    }

    public bool Same(object? expected, object? actual) => (expected, actual) switch
    {
        (double a, double b) => Math.Abs(a - b) < 1e-9,
        (DateTime a, DateTime b) => Math.Abs((a - b).TotalMinutes) < 1,
        _ => Equals(expected, actual),
    };
}
