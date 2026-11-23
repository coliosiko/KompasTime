using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Security;
using System.Threading;
using KompasAPI7;
using Kompas6Constants;

namespace KompasTime;

internal sealed class ReportNode
{
    public string Path { get; }
    public List<ReportNode> Children { get; } = new();
    public long? Seconds { get; set; }
    public bool Unavailable { get; set; }
    public bool LinksUnavailable { get; set; }
    public bool Repeat { get; set; }

    public ReportNode(string path) => Path = path;
}

internal sealed class AssemblyReport
{
    public ReportNode Root { get; }
    public long TotalSeconds { get; private set; }
    public bool Incomplete { get; private set; }

    private AssemblyReport(string root) => Root = new ReportNode(root);

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".a3d", ".m3d", ".cdw", ".spw", ".frw", ".kdw"
    };

    public static AssemblyReport Build(string rootPath, IReadOnlyDictionary<string, long?> savedInOpenDocuments,
        CancellationToken cancellation, Action<int, int> progress)
    {
        cancellation.ThrowIfCancellationRequested();
        var app = (IApplication)Marshal.GetActiveObject("KOMPAS.Application.7");
        var documents = app.Documents;
        var open = new Dictionary<string, IKompasDocument>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in (IEnumerable)documents)
        {
            cancellation.ThrowIfCancellationRequested();
            if (item is not IKompasDocument document)
                continue;
            try
            {
                var path = document.PathName;
                if (!string.IsNullOrWhiteSpace(path))
                    open[Path.GetFullPath(path)] = document;
            }
            catch (Exception ex) when (IsReadError(ex))
            {
                // An unrelated document can disappear during enumeration.
            }
        }

        var result = new AssemblyReport(Path.GetFullPath(rootPath));
        var seen = new Dictionary<string, ReportNode>(StringComparer.OrdinalIgnoreCase);
        var todo = new Stack<ReportNode>();
        todo.Push(result.Root);
        var processed = 0;

        while (todo.Count > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            var node = todo.Pop();
            if (seen.TryGetValue(node.Path, out var original))
            {
                node.Repeat = true;
                node.Seconds = original.Seconds;
                node.Unavailable = original.Unavailable;
                node.LinksUnavailable = original.LinksUnavailable;
            }
            else
            {
                seen.Add(node.Path, node);
                Read(node, documents, open, savedInOpenDocuments, cancellation);
                result.TotalSeconds = checked(result.TotalSeconds + (node.Seconds ?? 0));
                result.Incomplete |= node.Seconds == null || node.Unavailable || node.LinksUnavailable;
                for (var index = node.Children.Count - 1; index >= 0; index--)
                    todo.Push(node.Children[index]);
            }

            processed++;
            progress(processed, processed + todo.Count);
        }
        cancellation.ThrowIfCancellationRequested();
        return result;
    }

    private static void Read(ReportNode node, IDocuments documents,
        Dictionary<string, IKompasDocument> open, IReadOnlyDictionary<string, long?> savedInOpenDocuments,
        CancellationToken cancellation)
    {
        IKompasDocument? owned = null;
        try
        {
            if (!open.TryGetValue(node.Path, out var document))
            {
                if (!File.Exists(node.Path))
                {
                    node.Unavailable = true;
                    return;
                }

                document = documents.Open(node.Path, false, true);
                if (document == null)
                {
                    node.Unavailable = true;
                    return;
                }
                // Open can return a document that was already opened by the user.
                if (!document.Visible && document.ReadOnly)
                    owned = document;
            }

            cancellation.ThrowIfCancellationRequested();
            try
            {
                node.Seconds = savedInOpenDocuments.TryGetValue(node.Path, out var saved)
                    ? saved : DocumentHistory.ReadSavedSeconds(document);
            }
            catch (Exception ex) when (IsReadError(ex))
            {
                node.Unavailable = true;
            }

            cancellation.ThrowIfCancellationRequested();
            if (document is not IKompasDocument1 links ||
                !links.GetExternalFilesNamesEx(false, out var paths, out _))
            {
                node.LinksUnavailable = true;
                return;
            }

            if (paths is not Array files)
                return;

            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var directory = Path.GetDirectoryName(node.Path)!;
            foreach (var file in files)
            {
                cancellation.ThrowIfCancellationRequested();
                if (file is not string path || string.IsNullOrWhiteSpace(path))
                    continue;

                try
                {
                    if (!Extensions.Contains(Path.GetExtension(path)))
                        continue;
                    var fullPath = Path.GetFullPath(Path.Combine(directory, path));
                    if (unique.Add(fullPath))
                        node.Children.Add(new ReportNode(fullPath));
                }
                catch (Exception ex) when (IsReadError(ex))
                {
                    node.LinksUnavailable = true;
                }
            }
        }
        catch (Exception ex) when (IsReadError(ex))
        {
            node.LinksUnavailable = true;
            node.Unavailable |= node.Seconds == null;
        }
        finally
        {
            if (owned != null)
            {
                try
                {
                    if (!owned.Close(DocumentCloseOptions.kdDoNotSaveChanges))
                        node.LinksUnavailable = true;
                }
                catch (COMException)
                {
                    node.LinksUnavailable = true;
                }
            }
        }
    }

    private static bool IsReadError(Exception ex) =>
        ex is COMException or SerializationException or InvalidOperationException or ArgumentException
            or IOException or NotSupportedException or SecurityException or InvalidCastException
            or UnauthorizedAccessException;
}
