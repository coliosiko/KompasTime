using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace KompasTime.Plugin;

internal sealed class FileReportForm : Form
{
    private readonly ListBox files = new() { Dock = DockStyle.Fill, HorizontalScrollbar = true, SelectionMode = SelectionMode.MultiExtended };
    private readonly Button remove = new() { Text = "Убрать выделенное", AutoSize = true, Enabled = false };
    private readonly Button report = new() { Text = "Сформировать отчёт", AutoSize = true, Enabled = false };
    private readonly Label status = new() { AutoSize = true };
    private readonly HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource closing = new();
    private bool adding;
    private bool reportOpen;
    private ReportPathPicker? picker;

    public FileReportForm(Action<IReadOnlyList<string>> showReport)
    {
        Text = "Отчёт по файлам";
        Font = new Font("Segoe UI", 10);
        ClientSize = new Size(810, 460);
        MinimumSize = new Size(550, 310);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        var paste = new Button { Text = "Вставить", AutoSize = true };
        var add = new Button { Text = "Добавить…", AutoSize = true };
        var tools = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        tools.Controls.AddRange(new Control[] { paste, add, remove });
        var footer = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        footer.Controls.Add(report);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(12) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = "Перетащите сюда файлы КОМПАС или папки либо вставьте их из буфера.", AutoSize = true }, 0, 0);
        layout.Controls.Add(tools, 0, 1);
        layout.Controls.Add(files, 0, 2);
        layout.Controls.Add(status, 0, 3);
        layout.Controls.Add(footer, 0, 4);
        Controls.Add(layout);

        foreach (var target in new Control[] { this, layout, files })
        {
            target.AllowDrop = true;
            target.DragEnter += (_, e) =>
                e.Effect = !adding && picker == null && e.Data?.GetDataPresent(DataFormats.FileDrop) == true
                    ? e.AllowedEffect & DragDropEffects.Copy : DragDropEffects.None;
            target.DragDrop += async (_, e) =>
            {
                if (picker == null && e.Data?.GetData(DataFormats.FileDrop) is string[] dropped)
                    await AddPaths(dropped);
            };
        }
        paste.Click += async (_, _) => await Paste();
        KeyDown += async (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.V)
            {
                e.SuppressKeyPress = true;
                await Paste();
            }
        };
        add.Click += (_, _) => ShowPicker();
        remove.Click += (_, _) =>
        {
            foreach (var path in files.SelectedItems.Cast<string>().ToArray())
            {
                paths.Remove(path);
                files.Items.Remove(path);
            }
            UpdateStatus();
            UpdateButtons();
        };
        files.SelectedIndexChanged += (_, _) => UpdateButtons();
        report.Click += (_, _) => showReport(files.Items.Cast<string>().ToArray());
        UpdateStatus();
    }

    private void ShowPicker()
    {
        if (adding)
            return;
        if (picker is { } existing)
        {
            if (existing.WindowState == FormWindowState.Minimized)
                existing.WindowState = FormWindowState.Normal;
            existing.Activate();
            return;
        }

        var dialog = new ReportPathPicker();
        picker = dialog;
        dialog.FormClosed += async (_, e) =>
        {
            picker = null;
            if (e.CloseReason == CloseReason.UserClosing && dialog.SelectedPaths.Length != 0)
                await AddPaths(dialog.SelectedPaths);
        };
        dialog.Show(this);
    }

    private async Task Paste()
    {
        if (adding || picker != null)
            return;
        string[] input;
        try
        {
            input = Clipboard.ContainsFileDropList() ? Clipboard.GetFileDropList().Cast<string>().ToArray()
                : Clipboard.GetText().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        }
        catch (ExternalException)
        {
            status.Text = "Буфер обмена занят. Повторите вставку.";
            return;
        }
        await AddPaths(input);
    }

    private async Task AddPaths(string[] input)
    {
        if (adding)
            return;
        adding = true;
        UpdateButtons();
        status.Text = "Поиск документов КОМПАС…";
        try
        {
            var token = closing.Token;
            var (found, skipped) = await Task.Run(() => FindFiles(input, token), token);
            if (IsDisposed)
                return;
            files.BeginUpdate();
            try
            {
                foreach (var path in found)
                    if (paths.Add(path))
                        files.Items.Add(path);
            }
            finally { files.EndUpdate(); }
            UpdateStatus(skipped);
        }
        catch (OperationCanceledException) { }
        finally
        {
            adding = false;
            if (!IsDisposed)
                UpdateButtons();
        }
    }

    private static (List<string> Found, int Skipped) FindFiles(IEnumerable<string> input, CancellationToken token)
    {
        var found = new List<string>();
        var folders = new Stack<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skipped = 0;
        foreach (var value in input)
        {
            token.ThrowIfCancellationRequested();
            var path = value.Trim().Trim('"');
            try
            {
                if ((Path.GetPathRoot(path)?.Length ?? 0) < 3)
                    continue;
                path = Path.GetFullPath(path);
                if (Directory.Exists(path))
                    folders.Push(path);
                else if (AssemblyReport.IsKompasFile(path))
                    found.Add(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                skipped++;
            }
        }
        while (folders.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var folder = folders.Pop();
            if (!visited.Add(folder))
                continue;
            try
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(folder))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var attributes = File.GetAttributes(path);
                        if ((attributes & FileAttributes.Directory) == 0)
                        {
                            if (AssemblyReport.IsKompasFile(path))
                                found.Add(path);
                        }
                        else if ((attributes & FileAttributes.ReparsePoint) == 0)
                            folders.Push(path);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        skipped++;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped++;
            }
        }
        return (found, skipped);
    }

    private void UpdateStatus(int skipped = 0) =>
        status.Text = $"Файлов: {files.Items.Count}" + (skipped > 0 ? $" · Пропущено недоступных путей: {skipped}" : "");

    private void UpdateButtons()
    {
        report.Enabled = !adding && !reportOpen && files.Items.Count > 0;
        remove.Enabled = !adding && files.SelectedItems.Count > 0;
    }

    public void SetReportOpen(bool open)
    {
        reportOpen = open;
        UpdateButtons();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed)
        {
            closing.Cancel();
            closing.Dispose();
        }
        base.Dispose(disposing);
    }
}
