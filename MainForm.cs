using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows.Forms;
using KompasAPI7;

namespace KompasTime;

internal sealed class MainForm : Form
{
    private static readonly Color Ink = Color.FromArgb(28, 41, 59);
    private static readonly Color Accent = Color.FromArgb(27, 86, 167);
    private static readonly Color Muted = Color.FromArgb(79, 96, 118);
    private static readonly Color Surface = Color.White;

    private readonly Dictionary<object, Session> sessions = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly Timer timer = new() { Interval = 500 };
    private readonly Label connection = MakeLabel("", 10, Muted);
    private readonly Label document = MakeLabel("", 18, Ink, true);
    private readonly Label details = MakeLabel("", 10, Muted);
    private readonly Label state = MakeLabel("", 11, Accent, true);
    private readonly Label historyStatus = MakeLabel("", 10, Muted);
    private readonly Label total = MakeLabel("00:00:00", 32, Ink, true);
    private readonly Label savedTime = MakeLabel("—", 32, Ink, true);
    private readonly Button showHistory = new() { Text = "История", AutoSize = true, Enabled = false };
    private readonly Button showReport = new() { Text = "Сводный отчёт", AutoSize = true, Visible = false };
    private readonly NumericUpDown idleMinutes = new() { Minimum = 1, Maximum = 120, Value = 3, Width = 55 };

    private readonly object demoKey = new();
    private readonly bool demo;
    private Session? current;
    private bool activeReadOnly;
    private string? reportPath;
    private bool previouslyEligible;
    private TimeSpan previousSample;
    private readonly string authorName;
    private readonly string authorSid;

    public MainForm(bool demo)
    {
        this.demo = demo;
        Text = "Трудозатраты · КОМПАС-3D";
        Font = new Font("Segoe UI", 10);
        ForeColor = Ink;
        BackColor = Color.FromArgb(240, 244, 250);
        ClientSize = new Size(670, 380);
        MinimumSize = new Size(580, 385);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;

        using var identity = WindowsIdentity.GetCurrent();
        authorName = identity.Name;
        authorSid = identity.User?.Value ?? identity.Name;
        var author = MakeLabel($"Автор  {authorName}", 10, Ink);
        author.Margin = new Padding(0, 9, 0, 0);
        new ToolTip().SetToolTip(author, identity.User?.Value ?? identity.Name);

        var page = Stack();
        page.Dock = DockStyle.Fill;
        page.AutoScroll = true;
        page.Padding = new Padding(24, 18, 24, 12);
        Controls.Add(page);

        var heading = Stack();
        heading.Controls.Add(MakeLabel("Трудозатраты", 26, Ink, true));
        heading.Controls.Add(connection);
        AddRow(page, heading);
        AddRow(page, Card(document, details, state, historyStatus));

        var metrics = new TableLayoutPanel { AutoSize = true, ColumnCount = 2 };
        for (var index = 0; index < metrics.ColumnCount; index++)
            metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / metrics.ColumnCount));
        metrics.Controls.Add(Card(MakeLabel("К сохранению", 11, Muted), total), 0, 0);
        metrics.Controls.Add(Card(MakeLabel("Сохранено в файле", 11, Muted), savedTime), 1, 0);
        AddRow(page, metrics);

        if (!demo)
            AddRow(page, MakeLabel("Время записывается при сохранении документа в КОМПАС.", 10, Muted));

        var settings = Flow();
        settings.Controls.AddRange(new Control[] {
            MakeLabel("Порог бездействия, мин", 10, Ink), idleMinutes, showHistory, showReport
        });
        AddRow(page, settings);
        AddRow(page, author);

        details.AutoEllipsis = true;
        details.AutoSize = false;
        details.Height = 25;
        details.Dock = DockStyle.Fill;
        details.MinimumSize = new Size(0, 25);
        showHistory.Click += (_, _) =>
        {
            if (current?.History is not { } history)
                return;
            using var report = new HistoryForm(document.Text, history, activeReadOnly);
            report.ShowDialog(this);
        };
        showReport.Click += (_, _) => ShowAssemblyReport();
        timer.Tick += (_, _) => RefreshState();
        Shown += (_, _) => { RefreshState(); timer.Start(); };
        FormClosed += (_, _) =>
        {
            timer.Dispose();
            foreach (var session in sessions.Values)
                session.History?.Dispose();
        };
    }

    private void RefreshState()
    {
        DocumentSnapshot? active;
        var foreground = WindowsActivity.IsForeground(demo ? "KompasTime" : "KOMPAS");
        var now = clock.Elapsed;
        var elapsed = now - previousSample;
        previousSample = now;
        var idle = WindowsActivity.IdleTime;
        var threshold = TimeSpan.FromMinutes((double)idleMinutes.Value);
        var old = current;
        current = null;
        string? issue = null;
        bool connected = demo;

        try
        {
            if (demo)
                active = new DocumentSnapshot(demoKey, "Корпус.m3d", "Демонстрационная деталь", false);
            else
            {
                connected = KompasReader.TryRead(out active, out var open);
                foreach (var key in sessions.Keys.Where(key => !open.Contains(key)).ToArray())
                {
                    sessions[key].History?.Dispose();
                    sessions.Remove(key);
                }
            }
        }
        catch (COMException ex)
        {
            active = null;
            issue = $"КОМПАС временно недоступен · 0x{ex.ErrorCode:X8}";
        }

        var eligible = foreground && active is { ReadOnly: false };
        if (active != null)
        {
            if (!sessions.TryGetValue(active.Key, out current))
            {
                current = new Session();
                sessions.Add(active.Key, current);
                if (!demo)
                    current.History = new DocumentHistory((IKompasDocument)active.Key, current, authorSid, authorName);
            }

            if (ReferenceEquals(old, current) && previouslyEligible && eligible &&
                elapsed <= TimeSpan.FromSeconds(2))
            {
                var excess = idle > threshold ? idle - threshold : TimeSpan.Zero;
                if (elapsed > excess)
                    current.Tick(elapsed - excess);
            }
        }

        previouslyEligible = eligible;

        connection.Text = issue ?? (demo ? "Демонстрационный режим · КОМПАС не требуется" :
            connected ? "КОМПАС-3D подключён · API 7" : "Ожидание КОМПАС-3D");
        document.Text = active == null ? "Нет активного документа" :
            string.IsNullOrWhiteSpace(active.Name) ? "Документ без имени" : active.Name;
        details.Text = active == null ? "Откройте или создайте документ в КОМПАС-3D" :
            string.IsNullOrWhiteSpace(active.Path) ? "Новый документ" :
            active.Path + (active.ReadOnly ? "  ·  Только чтение" : "");

        state.Text = active switch
        {
            null => "Таймер остановлен",
            { ReadOnly: true } => "Таймер остановлен · только чтение",
            _ when !foreground => "Учёт приостановлен · другое окно",
            _ when idle >= threshold => "Учёт приостановлен · бездействие",
            _ => "Идёт учёт"
        };

        total.Text = Format(TimeSpan.FromSeconds(current?.PendingWorkSeconds ?? 0));
        var history = current?.History;
        activeReadOnly = active?.ReadOnly == true;
        showHistory.Enabled = history != null;
        reportPath = !demo && string.Equals(Path.GetExtension(active?.Path), ".a3d",
            StringComparison.OrdinalIgnoreCase) ? active!.Path : null;
        showReport.Visible = reportPath != null;
        savedTime.Text = history?.HasHistory == true ? Format(TimeSpan.FromSeconds(history.SavedSeconds)) : "—";
        historyStatus.Text = history?.Issue ?? (history?.HasPendingRemovals == true
            ? "Удаление из истории ожидает сохранения в КОМПАС."
            : activeReadOnly ? "История открыта только для чтения" : "");
        historyStatus.Visible = historyStatus.Text.Length > 0;
    }

    private void ShowAssemblyReport()
    {
        if (reportPath == null)
            return;

        var saved = new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in sessions)
        {
            if (pair.Value.History is not { } history || pair.Key is not IKompasDocument document)
                continue;

            try
            {
                var path = document.PathName;
                if (!string.IsNullOrWhiteSpace(path))
                    saved[path] = history.HasHistory ? history.SavedSeconds : null;
            }
            catch (COMException)
            {
                // The document may close before the worker snapshots the open files.
            }
        }

        timer.Stop();
        try
        {
            using var report = new AssemblyReportForm(reportPath, saved);
            report.Finished += timer.Start;
            report.ShowDialog(this);
        }
        finally
        {
            previouslyEligible = false;
            RefreshState();
            timer.Start();
        }
    }

    internal static string Format(TimeSpan time) =>
        $"{(long)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}";

    private static Label MakeLabel(string text, float size, Color color, bool bold = false) => new()
    {
        Text = text, AutoSize = true, ForeColor = color,
        Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
        Margin = new Padding(0, 2, 0, 2)
    };

    private static TableLayoutPanel Stack() => new()
    {
        AutoSize = true, ColumnCount = 1, GrowStyle = TableLayoutPanelGrowStyle.AddRows,
        Padding = Padding.Empty, Margin = Padding.Empty
    };

    private static FlowLayoutPanel Flow() => new()
    {
        AutoSize = true, WrapContents = false, Padding = Padding.Empty, Margin = Padding.Empty
    };

    private static TableLayoutPanel Card(params Control[] children)
    {
        var panel = Stack();
        panel.Dock = DockStyle.Fill;
        panel.BackColor = Surface;
        panel.Padding = new Padding(16, 10, 16, 10);
        panel.Margin = new Padding(0, 0, 8, 0);
        foreach (var child in children)
            panel.Controls.Add(child);
        return panel;
    }

    private static void AddRow(TableLayoutPanel target, Control control)
    {
        control.Dock = DockStyle.Fill;
        control.Margin = new Padding(0, 0, 0, 10);
        target.Controls.Add(control);
    }
}
