using System;

namespace KompasTime;

internal sealed class Session
{
    private TimeSpan elapsed;
    private long savedWorkSeconds;

    public long PendingWorkSeconds => elapsed.Ticks / TimeSpan.TicksPerSecond - savedWorkSeconds;
    public DocumentHistory? History { get; set; }

    public void Tick(TimeSpan duration) => elapsed += duration;

    public void MarkSaved(long workSeconds) => savedWorkSeconds += workSeconds;
}
