using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Security;
using KompasAPI7;

namespace KompasTime;

internal sealed class DocumentHistory : IDisposable
{
    // ksDocumentFileNotify, as declared in the KOMPAS SDK type library.
    private static readonly Guid Notifications = new("324c1a45-67ad-41fb-be57-624f930646f1");
    private const int BeginSaveId = 3, SavedId = 4, BeginAutoSaveId = 13;
    private const string StorageName = "KompasTime.WorkHistory";
    private const string RowName = "history";
    private readonly IKompasDocument document;
    private readonly Session session;
    private readonly string sid;
    private readonly string author;
    private readonly Func<string, bool> beginSave;
    private readonly Func<bool> saved;
    private readonly Func<string, bool> beginAutoSave;
    private WorkHistory history = new();
    private WorkEntry? sessionEntry;
    private readonly HashSet<WorkEntry> removedEntries = new();
    private string? committedJson;
    private PendingSave? pending;
    private bool ready;
    private bool disposed;

    public long SavedSeconds => history.TotalSeconds;
    public bool HasHistory => committedJson != null;
    public bool HasPendingRemovals => removedEntries.Count != 0;
    public bool CanRemove => ready && !disposed;
    public IEnumerable<WorkEntry> VisibleEntries => history.Entries.Where(entry => !removedEntries.Contains(entry));
    public string? Issue { get; private set; }
    public event Action? Updated;

    public void RemoveEntry(WorkEntry entry)
    {
        RunSafely(() =>
        {
            if (!CanRemove || document.ReadOnly || !history.Entries.Contains(entry) || !removedEntries.Add(entry))
                return;

            // Deleting history is an explicit edit. Mark the in-memory document so KOMPAS
            // offers its usual save even when the geometry hasn't changed.
            if (!document.Changed && document is IKompasDocument1 editable)
                editable.SetChanged(true);
        });
        Updated?.Invoke();
    }

    public DocumentHistory(IKompasDocument document, Session session, string sid, string author)
    {
        this.document = document;
        this.session = session;
        this.sid = sid;
        this.author = author;
        beginSave = BeginSave;
        saved = Saved;
        beginAutoSave = _ => RunSafely(RollBackPending);
        try
        {
            committedJson = Read(Storage(document, create: false));
            if (committedJson != null)
                history = WorkHistory.FromJson(committedJson);

            ComEventsHelper.Combine(document, Notifications, BeginSaveId, beginSave);
            ComEventsHelper.Combine(document, Notifications, SavedId, saved);
            ComEventsHelper.Combine(document, Notifications, BeginAutoSaveId, beginAutoSave);
            ready = true;
        }
        catch (Exception ex) when (IsMetadataError(ex))
        {
            Issue = $"История недоступна · {ex.Message}";
        }
    }

    private bool BeginSave(string target) => RunSafely(() =>
    {
        if (!ready || document.ReadOnly)
            return;

        RollBackPending();
        var source = document.PathName;
        var firstSave = string.IsNullOrWhiteSpace(source);
        if (string.IsNullOrWhiteSpace(target))
            return;

        var copy = !firstSave && !string.Equals(Path.GetFullPath(source), Path.GetFullPath(target),
            StringComparison.OrdinalIgnoreCase);
        var work = session.PendingWorkSeconds;
        if (work == 0 && (copy ? !HasHistory : !HasPendingRemovals))
            return;

        var entries = copy ? new List<WorkEntry>() : VisibleEntries.ToList();
        var currentIndex = sessionEntry == null ? -1 : entries.IndexOf(sessionEntry);
        var nextEntry = currentIndex < 0 ? null : sessionEntry;
        if (work > 0)
        {
            nextEntry = new WorkEntry(sid, author, (nextEntry?.WorkSeconds ?? 0) + work);
            if (currentIndex < 0)
                entries.Add(nextEntry);
            else
                entries[currentIndex] = nextEntry;
        }
        var next = history.WithEntries(entries.ToArray(), copy);
        var json = next.ToJson();
        var storage = Storage(document, create: true) ??
            throw new InvalidOperationException("КОМПАС не предоставил хранилище документа.");
        if (!string.Equals(Read(storage), committedJson, StringComparison.Ordinal))
            throw new InvalidOperationException("Хранилище изменилось после открытия документа.");

        pending = new PendingSave(storage, next, json, work, nextEntry, removedEntries.ToArray());
        try
        {
            Write(storage, json);
        }
        catch (Exception ex) when (IsMetadataError(ex))
        {
            RollBackPending();
            throw;
        }
    });

    private bool Saved() => RunSafely(() =>
    {
        if (pending == null)
            return;

        // A write can fail without aborting the user's CAD save.
        if (pending.Written)
        {
            history = pending.History;
            committedJson = pending.Json;
            sessionEntry = pending.SessionEntry;
            removedEntries.ExceptWith(pending.Removals);
            session.MarkSaved(pending.WorkSeconds);
            Issue = null;
            pending = null;
            Updated?.Invoke();
        }
    });

    internal static long? ReadSavedSeconds(IKompasDocument document)
    {
        var json = Read(Storage(document, create: false));
        return json == null ? null : WorkHistory.FromJson(json).TotalSeconds;
    }

    private static IUserDataStorage? Storage(IKompasDocument document, bool create)
    {
        var manager = document.UserDataStoragesMng ??
            throw new InvalidOperationException("Этот тип документа не поддерживает внутреннее хранилище.");
        var group = manager[null];
        if (!create)
            return group?[StorageName];

        group ??= manager.Add(null);
        return group?[StorageName] ?? group?.Add(StorageName);
    }

    private static string? Read(IUserDataStorage? storage)
    {
        if (storage == null)
            return null;

        var label = storage.GetObject(0, out var value, out _);
        if (value == null && string.IsNullOrEmpty(label))
            return null;
        if (label != RowName || value is not string json)
            throw new SerializationException("Неизвестная структура хранилища.");
        return json;
    }

    private void Write(IUserDataStorage storage, string json)
    {
        var result = committedJson == null ? storage.AddObject("", json, RowName)
            : storage.SetObject("", 0, json, RowName);
        if (result != 0)
            throw new InvalidOperationException("КОМПАС не подтвердил запись истории.");
        pending!.Written = true;
    }

    private void RollBackPending()
    {
        if (pending == null)
            return;

        var storage = pending.Storage;
        var current = Read(storage);
        if (current == pending.Json)
        {
            var restored = committedJson == null ? storage.Delete("", 0)
                : storage.SetObject("", 0, committedJson, RowName) == 0;
            if (!restored)
                throw new InvalidOperationException("Не удалось восстановить историю после прерванного сохранения.");
        }
        else if (current != committedJson)
        {
            throw new InvalidOperationException("Хранилище изменилось во время сохранения.");
        }
        pending = null;
    }

    private bool RunSafely(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (IsMetadataError(ex))
        {
            Issue = $"История не записана · {ex.Message}";
        }
        return true;
    }

    private static bool IsMetadataError(Exception ex) =>
        ex is COMException or SerializationException or InvalidOperationException or ArgumentException
            or IOException or NotSupportedException or SecurityException or InvalidCastException;

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        RunSafely(RollBackPending);
        RunSafely(() => ComEventsHelper.Remove(document, Notifications, BeginSaveId, beginSave));
        RunSafely(() => ComEventsHelper.Remove(document, Notifications, SavedId, saved));
        RunSafely(() => ComEventsHelper.Remove(document, Notifications, BeginAutoSaveId, beginAutoSave));
        Updated?.Invoke();
    }

    private sealed class PendingSave
    {
        public IUserDataStorage Storage { get; }
        public WorkHistory History { get; }
        public string Json { get; }
        public long WorkSeconds { get; }
        public WorkEntry? SessionEntry { get; }
        public WorkEntry[] Removals { get; }
        public bool Written { get; set; }

        public PendingSave(IUserDataStorage storage, WorkHistory history, string json, long work,
            WorkEntry? sessionEntry, WorkEntry[] removals)
        {
            Storage = storage;
            History = history;
            Json = json;
            WorkSeconds = work;
            SessionEntry = sessionEntry;
            Removals = removals;
        }
    }
}
