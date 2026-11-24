using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows.Forms;
using KompasAPI7;
using Kompas6Constants;

namespace KompasTime.Plugin;

internal sealed class OverlayTracker : IDisposable
{
    private readonly IApplication application;
    private readonly Dictionary<IKompasDocument, Session> sessions = new();
    private readonly Timer timer = new() { Interval = 350 };
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly uint processId = (uint)Process.GetCurrentProcess().Id;
    private readonly string author;
    private readonly string sid;
    private readonly OverlayWindow overlay;
    private IKompasDocument? active;
    private Session? current;
    private IntPtr owner;
    private TimeSpan previous;
    private bool previouslyEligible;
    private bool dialogOpen;
    private bool disposed;
    private FileReportForm? fileReport;
    private AssemblyReportForm? fileReportResult;

    public bool Tracking { get; private set; }
    public bool HasDocument => active != null;
    public string? AssemblyPath { get; private set; }
    public bool AssemblyOpen { get; private set; }
    private IWin32Window? Owner => owner == IntPtr.Zero ? null : new WindowOwner(owner);

    public OverlayTracker(IApplication application, bool autoStart)
    {
        this.application = application;
        Tracking = autoStart;
        using var identity = WindowsIdentity.GetCurrent();
        author = identity.Name;
        sid = identity.User?.Value ?? author;
        overlay = new OverlayWindow(ShowHistory, ShowReport);
        timer.Tick += (_, _) => Refresh();
        timer.Start();
    }

    public void ToggleTracking()
    {
        Refresh();
        Tracking = !Tracking;
        previouslyEligible = false;
    }

    public void ToggleIcon()
    {
        SettingsForm.IconVisible = !SettingsForm.IconVisible;
        Refresh();
    }

    private void Refresh()
    {
        if (disposed)
            return;

        var now = clock.Elapsed;
        var elapsed = now - previous;
        previous = now;
        try
        {
            var open = new HashSet<IKompasDocument>();
            foreach (var item in (IEnumerable)application.Documents)
                if (item is IKompasDocument document)
                    open.Add(document);
            foreach (var document in sessions.Keys.Where(document => !open.Contains(document)).ToArray())
            {
                sessions[document].History?.Dispose();
                sessions.Remove(document);
            }

            active = application.ActiveDocument;
            var old = current;
            current = null;
            AssemblyPath = null;
            AssemblyOpen = active?.DocumentType == DocumentTypeEnum.ksDocumentAssembly;
            if (active == null)
            {
                overlay.Suspend();
                previouslyEligible = false;
                return;
            }

            var path = active.PathName;
            if (string.Equals(Path.GetExtension(path), ".a3d", StringComparison.OrdinalIgnoreCase))
                AssemblyPath = path;

            if (!sessions.TryGetValue(active, out current))
            {
                current = new Session();
                current.History = new DocumentHistory(active, current, sid, author);
                sessions.Add(active, current);
            }

            var foregroundWindow = GetForegroundWindow();
            GetWindowThreadProcessId(foregroundWindow, out var foreground);
            var rootOwner = GetAncestor(foregroundWindow, 3); // GA_ROOTOWNER
            var kompasFocused = foreground == processId &&
                !OwnsForeground(fileReport, rootOwner) && !OwnsForeground(fileReportResult, rootOwner);
            var eligible = Tracking && !dialogOpen && kompasFocused && !active.ReadOnly;
            var idle = WindowsActivity.IdleTime;
            var threshold = TimeSpan.FromMinutes(SettingsForm.IdleMinutes);
            if (ReferenceEquals(current, old) && previouslyEligible && eligible && elapsed <= TimeSpan.FromSeconds(2))
            {
                var excess = idle > threshold ? idle - threshold : TimeSpan.Zero;
                if (elapsed > excess)
                    current.Tick(elapsed - excess);
            }
            previouslyEligible = eligible;

            UpdateOverlay(active, current, kompasFocused);
        }
        catch (COMException)
        {
            previouslyEligible = false;
            overlay.Suspend();
        }
    }

    private static bool OwnsForeground(Form? form, IntPtr rootOwner) =>
        form?.IsHandleCreated == true && form.Handle == rootOwner;

    private void UpdateOverlay(IKompasDocument document, Session session, bool kompasFocused)
    {
        IntPtr handle;
        try
        {
            var frame = ActiveFrame(document);
            handle = frame == null ? IntPtr.Zero : new IntPtr(frame.GetHWND());
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            // Frame access can fail independently of the document API used for tracking.
            handle = IntPtr.Zero;
        }

        owner = handle == IntPtr.Zero ? IntPtr.Zero : GetAncestor(handle, 2); // GA_ROOT
        Rectangle area = Rectangle.Empty;
        if (handle != IntPtr.Zero && kompasFocused && GetWindowRect(handle, out var rect))
            area = Workspace(handle, rect);

        var history = session.History;
        overlay.Update(area, owner, !dialogOpen && SettingsForm.IconVisible && kompasFocused && owner != IntPtr.Zero,
            WorkDuration.Format(session.PendingWorkSeconds),
            history?.HasHistory == true ? WorkDuration.Format(history.SavedSeconds) : "—",
            AssemblyOpen);
    }

    private static Rectangle Workspace(IntPtr frame, NativeRect rect)
    {
        var middle = rect.Top + (rect.Bottom - rect.Top) / 2;
        var left = rect.Left;
        var right = rect.Right;
        // The native frame rectangle can extend under docked KOMPAS panels.
        while (left + 16 < right && !HitsFrame(frame, left + 8, middle))
            left += 16;
        while (right - 16 > left && !HitsFrame(frame, right - 8, middle))
            right -= 16;
        if (right - left < 100)
        {
            left = rect.Left;
            right = rect.Right;
        }
        return Rectangle.FromLTRB(left, rect.Top, right, rect.Bottom);
    }

    private static bool HitsFrame(IntPtr frame, int x, int y)
    {
        var hit = WindowFromPoint(new Point(x, y));
        return hit == frame || IsChild(frame, hit);
    }

    private static IDocumentFrame? ActiveFrame(IKompasDocument document)
    {
        var frames = document.DocumentFrames;
        if (frames == null)
            return null;
        for (var index = 0; index < frames.Count; index++)
            if (frames[index] is IDocumentFrame frame && frame.Active)
                return frame;
        return frames.Count == 0 ? null : (IDocumentFrame?)frames[0];
    }

    public void ShowHistory()
    {
        Refresh();
        if (active == null || current?.History is not { } history)
            return;
        using var form = new HistoryForm(active.Name, history, active.ReadOnly);
        ShowDialog(() => form.ShowDialog(Owner));
    }

    public void ShowReport()
    {
        Refresh();
        if (AssemblyPath is not { } path)
        {
            if (AssemblyOpen)
                ShowDialog(() => MessageBox.Show(Owner, "Сохраните сборку перед расчётом сводных данных.", "KompasTime"));
            return;
        }
        using var form = new AssemblyReportForm(path, SavedTotals(), application);
        ShowDialog(() => RunReport(form, Owner));
    }

    public void ShowFileReport()
    {
        if (fileReport is { } existing)
        {
            if (existing.WindowState == FormWindowState.Minimized)
                existing.WindowState = FormWindowState.Normal;
            existing.Activate();
            return;
        }

        var form = new FileReportForm(ShowFileReportResult);
        form.SetReportOpen(fileReportResult != null);
        form.FormClosed += (_, _) => fileReport = null;
        fileReport = form;
        form.Show();
    }

    private void ShowFileReportResult(IReadOnlyList<string> paths)
    {
        if (fileReportResult != null)
            return;

        var result = new AssemblyReportForm(paths, SavedTotals(), application);
        result.FormClosed += (_, _) =>
        {
            fileReportResult = null;
            fileReport?.SetReportOpen(false);
        };
        fileReportResult = result;
        fileReport?.SetReportOpen(true);
        result.Show();
    }

    private Dictionary<string, long?> SavedTotals()
    {
        var saved = new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in sessions)
        {
            var filename = pair.Key.PathName;
            if (!string.IsNullOrWhiteSpace(filename) && pair.Value.History is { } history)
                saved[filename] = history.HasHistory ? history.SavedSeconds : null;
        }

        return saved;
    }

    private void RunReport(AssemblyReportForm form, IWin32Window? reportOwner)
    {
        timer.Stop();
        try { form.ShowDialog(reportOwner); }
        finally { if (!disposed) timer.Start(); }
    }

    public void ShowSettings()
    {
        using var form = new SettingsForm();
        ShowDialog(() => form.Edit(Owner));
    }

    public void ShowHelp()
    {
        using var form = new HelpForm();
        ShowDialog(() => form.ShowDialog(Owner));
    }

    private void ShowDialog(Action show)
    {
        dialogOpen = true;
        overlay.Suspend();
        try { show(); }
        finally
        {
            dialogOpen = false;
            previouslyEligible = false;
            Refresh();
        }
    }

    public void Dispose()
    {
        disposed = true;
        timer.Dispose();
        fileReport?.Close();
        fileReportResult?.Close();
        overlay.Dispose();
        foreach (var session in sessions.Values)
            session.History?.Dispose();
        sessions.Clear();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr child);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
}
