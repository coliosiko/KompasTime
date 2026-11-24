using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace KompasTime.Plugin;

internal sealed class WindowOwner : IWin32Window
{
    public IntPtr Handle { get; }
    public WindowOwner(IntPtr handle) => Handle = handle;
}

internal sealed class OverlayWindow : Form
{
    private readonly Timer animation = new() { Interval = 15 };
    private readonly Panel badge = new() { Cursor = Cursors.Hand };
    private readonly Panel content = new();
    private readonly Label pending = new() { AutoSize = true, Location = new Point(12, 8) };
    private readonly Label saved = new() { AutoSize = true, Location = new Point(12, 30) };
    private readonly Button history = new() { Text = "История", AutoSize = true, Location = new Point(9, 55) };
    private readonly Button report = new() { Text = "Сводные данные", AutoSize = true, Location = new Point(90, 55) };
    private Rectangle workspace;
    private bool right;
    private bool bottom;
    private int size = 40;
    private float expanded;

    public OverlayWindow(Action showHistory, Action showReport)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(237, 243, 251);
        Font = new Font("Segoe UI", 9);
        DoubleBuffered = true;
        badge.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var pad = badge.Width * .24f;
            var diameter = badge.Width - 2 * pad;
            var middle = badge.Width / 2f;
            using var pen = new Pen(Color.FromArgb(33, 79, 138), Math.Max(1.5f, badge.Width / 20f));
            e.Graphics.DrawEllipse(pen, pad, pad, diameter, diameter);
            e.Graphics.DrawLine(pen, middle, middle - diameter * .29f, middle, middle);
            e.Graphics.DrawLine(pen, middle, middle, middle + diameter * .23f, middle + diameter * .13f);
        };
        content.Controls.AddRange(new Control[] { pending, saved, history, report });
        Controls.AddRange(new Control[] { content, badge });
        history.Click += (_, _) => showHistory();
        report.Click += (_, _) => showReport();
        animation.Tick += (_, _) =>
        {
            var hovered = Bounds.Contains(Cursor.Position);
            var next = Math.Max(0, Math.Min(1, expanded + (hovered ? .10f : -.085f)));
            if (next == expanded)
                return;
            expanded = next;
            Place();
        };
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= 0x08000000 | 0x80; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
            return parameters;
        }
    }

    public void Suspend()
    {
        animation.Stop();
        expanded = 0;
        Hide();
    }

    public void Update(Rectangle area, IntPtr owner, bool visible, string unsaved, string savedTime, bool assembly)
    {
        if (!visible || area.Width < 100 || area.Height < 100)
        {
            Suspend();
            return;
        }

        workspace = area;
        var corner = SettingsForm.Corner;
        right = corner % 2 == 1;
        bottom = corner >= 2;
        size = 40 * SettingsForm.IconScale / 100;
        pending.Text = "К сохранению  " + unsaved;
        saved.Text = "Сохранено в файле  " + savedTime;
        report.Visible = assembly;
        Place();
        if (!Visible)
            Show(new WindowOwner(owner));
        animation.Start();
    }

    private void Place()
    {
        var ease = expanded * expanded * (3 - 2 * expanded);
        var width = size + (int)((Math.Min(238, workspace.Width - 16) - size) * ease);
        var height = size + (int)((Math.Min(size + 91, workspace.Height - 16) - size) * ease);
        var x = right ? workspace.Right - width - 12 : workspace.Left + 12;
        var y = bottom ? workspace.Bottom - height - 12 : workspace.Top + 12;
        SetBounds(x, y, width, height);
        badge.SetBounds(right ? width - size : 0, bottom ? height - size : 0, size, size);
        content.SetBounds(0, bottom ? 0 : size, width, Math.Max(0, height - size));
        content.Visible = expanded >= .78f;
        badge.Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            animation.Dispose();
        base.Dispose(disposing);
    }
}
