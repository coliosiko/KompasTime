using System;

namespace KompasTime;

internal static class WorkDuration
{
    public static string Format(long seconds)
    {
        var time = TimeSpan.FromSeconds(seconds);
        return $"{(long)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}";
    }
}
