using System.Globalization;

namespace SpaceMaid.App;

internal sealed record CleanupOccurrence(string Id, DateTimeOffset ScheduledForUtc, DateTime NominalLocalTime);

// The wall-clock date is the occurrence identity. Both offsets of a repeated
// local hour therefore name the same run; a missing hour advances to the first
// valid local minute, matching Windows' weekly-trigger contract.
internal static class CleanupScheduleClock
{
    internal static CleanupOccurrence OnDate(CleanupSchedule schedule, DateOnly date)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZoneId);
        var nominal = date.ToDateTime(schedule.Time, DateTimeKind.Unspecified);
        var local = nominal;
        if (zone.IsInvalidTime(local)) local = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0, DateTimeKind.Unspecified);
        for (int minute = 0; zone.IsInvalidTime(local); minute++)
        {
            if (minute >= 24 * 60) throw new ArgumentException("This schedule has no valid local time on that date.");
            local = local.AddMinutes(1);
        }
        TimeSpan offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), new DateTimeOffset(local, offset).ToUniversalTime(), nominal);
    }

    internal static CleanupOccurrence Next(CleanupSchedule schedule, DateTimeOffset after)
    {
        var local = TimeZoneInfo.ConvertTime(after, TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZoneId));
        var date = DateOnly.FromDateTime(local.DateTime);
        date = date.AddDays(((int)schedule.Day - (int)date.DayOfWeek + 7) % 7);
        var occurrence = OnDate(schedule, date);
        return occurrence.ScheduledForUtc <= after ? OnDate(schedule, date.AddDays(7)) : occurrence;
    }

    internal static CleanupOccurrence Latest(CleanupSchedule schedule, DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZoneId));
        var date = DateOnly.FromDateTime(local.DateTime);
        date = date.AddDays(-(((int)date.DayOfWeek - (int)schedule.Day + 7) % 7));
        var occurrence = OnDate(schedule, date);
        return occurrence.ScheduledForUtc > now ? OnDate(schedule, date.AddDays(-7)) : occurrence;
    }
}
