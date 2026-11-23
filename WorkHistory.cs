using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace KompasTime;

[DataContract]
internal sealed class WorkHistory
{
    [DataMember(IsRequired = true)] public int Version { get; private set; } = 1;
    [DataMember(IsRequired = true)] public Guid DocumentId { get; private set; } = Guid.NewGuid();
    [DataMember(IsRequired = true)] public WorkEntry[] Entries { get; private set; } = Array.Empty<WorkEntry>();

    public long TotalSeconds => Entries.Sum(entry => entry.TotalSeconds);

    public WorkHistory WithEntries(WorkEntry[] entries, bool newDocument) => new()
    {
        DocumentId = newDocument ? Guid.NewGuid() : DocumentId,
        Entries = entries
    };

    public string ToJson()
    {
        using var stream = new MemoryStream();
        new DataContractJsonSerializer(typeof(WorkHistory)).WriteObject(stream, this);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static WorkHistory FromJson(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var history = (WorkHistory?)new DataContractJsonSerializer(typeof(WorkHistory)).ReadObject(stream);
        if (history == null || history.Version != 1 || history.DocumentId == Guid.Empty ||
            history.Entries == null || history.Entries.Any(entry => entry == null ||
                entry.WorkSeconds < 0 || entry.ViewingSeconds < 0 ||
                entry.WorkSeconds > int.MaxValue || entry.ViewingSeconds > int.MaxValue ||
                (entry.WorkSeconds == 0 && entry.ViewingSeconds == 0) ||
                string.IsNullOrWhiteSpace(entry.AuthorSid) || string.IsNullOrWhiteSpace(entry.AuthorName)) ||
            history.TotalSeconds >= TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerSecond)
            throw new SerializationException("Неподдерживаемый формат истории трудозатрат.");
        return history;
    }
}

[DataContract]
internal sealed class WorkEntry
{
    [DataMember(IsRequired = true)] public string AuthorSid { get; private set; }
    [DataMember(IsRequired = true)] public string AuthorName { get; private set; }
    [DataMember(IsRequired = true)] public DateTime SavedAtUtc { get; private set; }
    [DataMember(IsRequired = true)] public long WorkSeconds { get; private set; }
    [DataMember(IsRequired = true)] public long ViewingSeconds { get; private set; }
    public long TotalSeconds => WorkSeconds + ViewingSeconds;

    public WorkEntry(string sid, string name, long work)
    {
        AuthorSid = sid;
        AuthorName = name;
        SavedAtUtc = DateTime.UtcNow;
        WorkSeconds = work;
    }
}
