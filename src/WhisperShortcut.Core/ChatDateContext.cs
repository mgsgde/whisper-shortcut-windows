using System.Globalization;
using System.Text.RegularExpressions;

namespace WhisperShortcut.Core;

/// <summary>
/// The one "when is it?" line for chat prompts (Mac ChatDateContext). The wording stays English;
/// weekday and month names follow the locale (German by default, like the app). The date and zone
/// go at the top of a system prompt, the clock time at the end, so a provider can keep caching the
/// prefix for the whole day.
/// </summary>
public static class ChatDateContext
{
    /// <summary>Label of the full line (date and clock together).</summary>
    public const string CurrentLabel = "Current date and time";

    /// <summary>German names, Gregorian year, so a Buddhist locale cannot date the prompt 543 years ahead.</summary>
    public static readonly CultureInfo German = WithGregorian(CultureInfo.GetCultureInfo("de-DE"));

    // Meeting-YYYY-MM-DD-HHMMSS, plus the "-2" Windows adds when that folder already exists.
    private static readonly Regex StemTimestamp = new(
        "^Meeting-(\\d{4}-\\d{2}-\\d{2}-\\d{6})(?:-\\d+)?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Weekday, full date, clock and zone, e.g. "Current date and time: Mittwoch, 4. November 2026, 18:42 (Europe/Berlin)."
    /// </summary>
    public static string Line(DateTimeOffset now, TimeZoneInfo timeZone, CultureInfo locale) =>
        Line(CurrentLabel, now, timeZone, locale);

    /// <summary>Day and zone, no clock: the cacheable top of a system prompt.</summary>
    public static string StableLine(DateTimeOffset now, TimeZoneInfo timeZone, CultureInfo locale) =>
        $"Current date: {DateText(now, timeZone, locale)} ({ZoneId(timeZone)}).";

    /// <summary>Clock and zone, for the end of a system prompt.</summary>
    public static string TimeOfDayLine(DateTimeOffset now, TimeZoneInfo timeZone, CultureInfo locale) =>
        $"Current time: {TimeText(now, timeZone, locale)} ({ZoneId(timeZone)}).";

    /// <summary>Same line with another label, e.g. a meeting that started at 14:30.</summary>
    public static string Line(string label, DateTimeOffset at, TimeZoneInfo timeZone, CultureInfo locale) =>
        $"{label}: {DateText(at, timeZone, locale)}, {TimeText(at, timeZone, locale)} ({ZoneId(timeZone)}).";

    /// <summary>
    /// Dates a saved meeting from its folder name. Null when the name does not parse — no invented date.
    /// The wall clock in the name is the machine's local time, the way the folder was created.
    /// </summary>
    public static string? MeetingLine(string stem, string label = "Meeting date and time", TimeZoneInfo? timeZone = null, CultureInfo? locale = null)
    {
        var started = DateFromStem(stem);
        if (started == null) return null;
        return Line(label, started.Value, timeZone ?? TimeZoneInfo.Local, locale ?? German);
    }

    /// <summary>The start time stored in a Meeting-YYYY-MM-DD-HHMMSS name, in the local zone. Null when it does not parse.</summary>
    public static DateTimeOffset? DateFromStem(string stem)
    {
        var match = StemTimestamp.Match(stem);
        if (!match.Success) return null;
        if (!DateTime.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var wall))
            return null;
        return new DateTimeOffset(wall, TimeZoneInfo.Local.GetUtcOffset(wall));
    }

    /// <summary>IANA name when .NET knows one ("Europe/Berlin"), otherwise the zone's own id.</summary>
    public static string ZoneId(TimeZoneInfo zone) =>
        zone.HasIanaId ? zone.Id
        : TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana
        : zone.Id;

    private static string DateText(DateTimeOffset instant, TimeZoneInfo zone, CultureInfo locale) =>
        TimeZoneInfo.ConvertTime(instant, zone).ToString("D", WithGregorian(locale));

    // 24-hour clock: "17:42" cannot be read as "7:42" in a prompt the model only sees as text.
    private static string TimeText(DateTimeOffset instant, TimeZoneInfo zone, CultureInfo locale)
    {
        _ = locale;
        return TimeZoneInfo.ConvertTime(instant, zone).ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    private static CultureInfo WithGregorian(CultureInfo culture)
    {
        var copy = (CultureInfo)culture.Clone();
        copy.DateTimeFormat.Calendar = new GregorianCalendar();
        return copy;
    }
}
