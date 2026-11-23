using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace KompasTime;

internal sealed class AssemblyReportForm : Form
{
    private readonly string root;
    private readonly IReadOnlyDictionary<string, long?> savedInOpenDocuments;
    private readonly CancellationTokenSource cancellation = new();
    private readonly TreeView tree = new() { Dock = DockStyle.Fill, ShowNodeToolTips = true };
    private readonly ProgressBar progress = new() { Dock = DockStyle.Fill, Height = 18, Style = ProgressBarStyle.Marquee };
    private readonly Label status = new() { AutoSize = true, Margin = new Padding(8, 8, 0, 0) };
    private readonly Button stop = new() { Text = "Остановить", AutoSize = true };
    private readonly Button export = new() { Text = "Экспорт в Excel", AutoSize = true, Visible = false };
    private AssemblyReport? completedReport;
    private bool running = true;
    public event Action? Finished;

    public AssemblyReportForm(string root, IReadOnlyDictionary<string, long?> savedInOpenDocuments)
    {
        this.root = root;
        this.savedInOpenDocuments = savedInOpenDocuments;
        Text = $"Сводный отчёт · {Path.GetFileName(root)}";
        Font = new Font("Segoe UI", 10);
        ClientSize = new Size(740, 470);
        MinimumSize = new Size(520, 320);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        status.Text = "Чтение состава сборки…";

        var footer = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        footer.Controls.AddRange(new Control[] { stop, export, status });

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(12) };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(tree, 0, 0);
        layout.Controls.Add(progress, 0, 1);
        layout.Controls.Add(footer, 0, 2);
        Controls.Add(layout);

        tree.BeforeExpand += (_, args) => PopulateChildren(args.Node);
        stop.Click += (_, _) => RequestStop();
        export.Click += async (_, _) => await ExportReport();
        Shown += async (_, _) => await LoadReport();
        FormClosing += (_, args) =>
        {
            if (running)
            {
                args.Cancel = true;
                RequestStop();
            }
        };
        FormClosed += (_, _) => cancellation.Dispose();
    }

    private async Task LoadReport()
    {
        var updates = new Progress<(int Done, int Found)>(value =>
        {
            if (!running || cancellation.IsCancellationRequested)
                return;

            progress.Style = ProgressBarStyle.Continuous;
            progress.Maximum = value.Found;
            progress.Value = value.Done;
            status.Text = $"Проверено {value.Done} из {value.Found} найденных файлов";
        });

        try
        {
            var result = await RunOnSta(updates, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            var rootNode = MakeNode(result.Root);
            tree.Nodes.Add(rootNode);
            rootNode.Expand();
            var total = MainForm.Format(TimeSpan.FromSeconds(result.TotalSeconds));
            status.Text = result.Incomplete ? $"Итого по доступным данным: {total}" : $"Итого: {total}";
            completedReport = result;
            export.Visible = true;
        }
        catch (OperationCanceledException)
        {
            status.Text = "Формирование отчёта остановлено.";
        }
        catch (Exception ex)
        {
            status.Text = $"Не удалось прочитать сборку: {ex.Message}";
        }
        finally
        {
            running = false;
            progress.Visible = false;
            stop.Visible = false;
            Finished?.Invoke();
        }
    }

    private async Task ExportReport()
    {
        if (completedReport is not { } report)
            return;

        using var dialog = new SaveFileDialog
        {
            Filter = "Книга Excel (*.xlsx)|*.xlsx",
            DefaultExt = "xlsx",
            AddExtension = true,
            RestoreDirectory = true,
            FileName = Path.GetFileNameWithoutExtension(root) + " — трудозатраты.xlsx"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        var path = dialog.FileName;
        export.Enabled = false;
        try
        {
            await Task.Run(() => ReportExcel.Save(path, report));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or NotSupportedException or System.Xml.XmlException)
        {
            if (!IsDisposed)
                MessageBox.Show(this, ex.Message, "Не удалось экспортировать отчёт", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            if (!IsDisposed)
                export.Enabled = true;
        }
    }

    private Task<AssemblyReport> RunOnSta(IProgress<(int Done, int Found)> updates, CancellationToken token)
    {
        var completion = new TaskCompletionSource<AssemblyReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            var throttle = Stopwatch.StartNew();
            try
            {
                var report = AssemblyReport.Build(root, savedInOpenDocuments, token, (done, found) =>
                {
                    if (done == 1 || throttle.ElapsedMilliseconds >= 80)
                    {
                        updates.Report((done, found));
                        throttle.Restart();
                    }
                });
                completion.SetResult(report);
            }
            catch (OperationCanceledException)
            {
                completion.SetCanceled();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "KOMPAS assembly report"
        };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        return completion.Task;
    }

    private void RequestStop()
    {
        cancellation.Cancel();
        stop.Enabled = false;
        status.Text = "Остановка после текущего чтения…";
    }

    private static TreeNode MakeNode(ReportNode file)
    {
        var value = file.Seconds.HasValue ? MainForm.Format(TimeSpan.FromSeconds(file.Seconds.Value))
            : file.Unavailable ? "недоступен" : "нет данных";
        var tooltip = file.Path;
        if (file.Repeat)
            tooltip += Environment.NewLine + "Файл уже учтён в другой ветви.";
        if (file.LinksUnavailable)
            tooltip += Environment.NewLine + "Не удалось прочитать связанные файлы.";

        var node = new TreeNode($"{Path.GetFileName(file.Path)}  —  {value}") { Tag = file, ToolTipText = tooltip };
        if (file.Children.Count > 0)
            node.Nodes.Add(new TreeNode());
        return node;
    }

    private static void PopulateChildren(TreeNode node)
    {
        if (node.Tag is not ReportNode file || node.Nodes.Count != 1 || node.Nodes[0].Tag != null)
            return;

        node.Nodes.Clear();
        var children = new TreeNode[file.Children.Count];
        for (var index = 0; index < children.Length; index++)
            children[index] = MakeNode(file.Children[index]);
        node.Nodes.AddRange(children);
    }
}
