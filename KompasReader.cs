using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using KompasAPI7;

namespace KompasTime;

internal sealed class DocumentSnapshot
{
    public object Key { get; }
    public string Name { get; }
    public string Path { get; }
    public bool ReadOnly { get; }

    public DocumentSnapshot(object key, string name, string path, bool readOnly)
    {
        Key = key;
        Name = name;
        Path = path;
        ReadOnly = readOnly;
    }
}

internal static class KompasReader
{
    private const int ObjectUnavailable = unchecked((int)0x800401E3);

    public static bool TryRead(out DocumentSnapshot? active, out HashSet<object> open)
    {
        active = null;
        open = new HashSet<object>();
        IApplication app;
        try
        {
            app = (IApplication)Marshal.GetActiveObject("KOMPAS.Application.7");
        }
        catch (COMException ex) when (ex.ErrorCode == ObjectUnavailable)
        {
            return false;
        }

        var documents = app.Documents;
        foreach (object document in (IEnumerable)documents)
            open.Add(document);
        var current = app.ActiveDocument;
        if (current != null)
        {
            open.Add(current);
            active = new DocumentSnapshot(current, current.Name, current.PathName, current.ReadOnly);
        }
        return true;
    }
}
