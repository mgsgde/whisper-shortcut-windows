using System.Globalization;
using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

/// <summary>
/// The date line every prompt gets (Mac ChatDateContextTests). A fixed instant, zone and locale,
/// so nothing here depends on the clock or the machine's settings.
/// </summary>
public class ChatDateContextTests
{
    // Wednesday, 4 November 2026, 17:42:00 UTC.
    private static readonly DateTimeOffset Fixed = DateTimeOffset.FromUnixTimeSeconds(1_793_814_120);
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
    private static readonly TimeZoneInfo Tokyo = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    [Fact]
    public void Line_carries_weekday_date_time_and_zone()
    {
        var line = ChatDateContext.Line(Fixed, Berlin, English);

        Assert.StartsWith("Current date and time: ", line);
        Assert.Contains("Wednesday", line);
        Assert.Contains("November", line);
        Assert.Contains("4", line);
        Assert.Contains("2026", line);
        // Berlin is UTC+1 in November, so 17:42 UTC is 18:42 local.
        Assert.Contains("18:42", line);
        Assert.Contains("Europe/Berlin", line);
        Assert.EndsWith(".", line);
    }

    [Fact]
    public void A_different_zone_renders_a_different_time()
    {
        var berlinLine = ChatDateContext.Line(Fixed, Berlin, English);
        var tokyoLine = ChatDateContext.Line(Fixed, Tokyo, English);

        Assert.Contains("18:42", berlinLine);
        Assert.Contains("02:42", tokyoLine);
        Assert.Contains("Asia/Tokyo", tokyoLine);
        Assert.NotEqual(berlinLine, tokyoLine);
    }

    [Fact]
    public void A_German_locale_uses_German_weekday_and_month_names()
    {
        var englishLine = ChatDateContext.Line(Fixed, Berlin, English);
        var germanLine = ChatDateContext.Line(Fixed, Berlin, German);

        Assert.Contains("Wednesday", englishLine);
        Assert.DoesNotContain("Mittwoch", englishLine);
        Assert.Contains("Mittwoch", germanLine);
        Assert.Contains("November", germanLine);
        Assert.Contains("18:42", germanLine);
        Assert.Contains("Europe/Berlin", germanLine);
    }

    [Fact]
    public void A_custom_label_dates_a_moment_that_is_not_now()
    {
        var line = ChatDateContext.Line("Meeting started", Fixed, Berlin, English);

        Assert.StartsWith("Meeting started: Wednesday, November 4, 2026", line);
        Assert.Contains("18:42", line);
    }

    [Fact]
    public void A_meeting_stem_parses_to_the_meetings_date_and_time()
    {
        const string stem = "Meeting-2026-11-04-143000";
        var parsed = ChatDateContext.DateFromStem(stem);
        Assert.NotNull(parsed);

        // The name is read in the machine's zone, the same way the folder was written.
        var local = TimeZoneInfo.ConvertTime(parsed.Value, TimeZoneInfo.Local);
        Assert.Equal(2026, local.Year);
        Assert.Equal(11, local.Month);
        Assert.Equal(4, local.Day);
        Assert.Equal(14, local.Hour);
        Assert.Equal(30, local.Minute);
        Assert.Equal(0, local.Second);

        var line = ChatDateContext.MeetingLine(stem, timeZone: Berlin, locale: English);
        Assert.NotNull(line);
        Assert.StartsWith("Meeting date and time: Wednesday, November 4, 2026", line);
        Assert.Contains("Europe/Berlin", line);
        var shown = TimeZoneInfo.ConvertTime(parsed.Value, Berlin).ToString("HH:mm", CultureInfo.InvariantCulture);
        Assert.Contains(shown, line);

        // A second meeting in the same second keeps the "-2" Windows adds to the folder name.
        Assert.NotNull(ChatDateContext.MeetingLine(stem + "-2", timeZone: Berlin, locale: English));
    }

    [Fact]
    public void An_unparseable_stem_yields_no_date_line()
    {
        Assert.Null(ChatDateContext.DateFromStem("not-a-meeting"));
        Assert.Null(ChatDateContext.DateFromStem("Meeting-2026-13-45-999999"));
        Assert.Null(ChatDateContext.MeetingLine("not-a-meeting"));
        Assert.Null(ChatDateContext.MeetingLine("Meeting-2026-13-45-999999"));
    }

    [Fact]
    public void The_prompt_prefix_carries_the_date_but_no_clock()
    {
        var early = ChatDateContext.StableLine(Fixed, Berlin, English);
        var later = ChatDateContext.StableLine(Fixed.AddHours(3), Berlin, English);

        Assert.StartsWith("Current date: ", early);
        Assert.Contains("Wednesday", early);
        Assert.Contains("November 4, 2026", early);
        Assert.Contains("Europe/Berlin", early);
        Assert.DoesNotContain("18:42", early);
        Assert.Equal(early, later);
    }

    [Fact]
    public void The_clock_is_a_separate_line()
    {
        var line = ChatDateContext.TimeOfDayLine(Fixed, Berlin, English);

        Assert.StartsWith("Current time: ", line);
        Assert.Contains("18:42", line);
        Assert.Contains("Europe/Berlin", line);
        Assert.DoesNotContain("Wednesday", line);
    }

    [Fact]
    public void A_non_Gregorian_locale_still_renders_the_Gregorian_year()
    {
        // Thai defaults to the Buddhist calendar, where 2026 is 2569.
        var line = ChatDateContext.StableLine(Fixed, Berlin, CultureInfo.GetCultureInfo("th-TH"));

        Assert.Contains("2026", line);
        Assert.DoesNotContain("2569", line);
    }
}
