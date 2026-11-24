using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace KompasTime.Plugin;

internal sealed class ReportPathPicker : Form
{
    private readonly TextBox address = new() { Dock = DockStyle.Fill };
    private readonly ListView entries = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = true, HideSelection = false
    };
    private readonly Button up = new() { Text = "Вверх", AutoSize = true };
    private readonly Button go = new() { Text = "Перейти", AutoSize = true };
    private readonly Button add = new() { Text = "Добавить выбранное", AutoSize = true, Enabled = false };
    private readonly Button addCurrent = new() { Text = "Добавить эту папку", AutoSize = true, Enabled = false };
    private readonly Label status = new() { AutoSize = true };
    private string? directory;
    private bool loading;

    public string[] SelectedPaths { get; private set; } = Array.Empty<string>();

    public ReportPathPicker()
    {
        Text = "Добавить файлы и папки";
        Font = new Font("Segoe UI", 10);
        ClientSize = new Size(730, 440);
        MinimumSize = new Size(545, 320);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        entries.Columns.Add("Имя", 490);
        entries.Columns.Add("Тип", 130);

        var navigation = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3 };
        navigation.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        navigation.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        navigation.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        navigation.Controls.Add(up, 0, 0);
        navigation.Controls.Add(address, 1, 0);
        navigation.Controls.Add(go, 2, 0);
        var cancel = new Button { Text = "Отмена", AutoSize = true };
        cancel.Click += (_, _) => Close();
        CancelButton = cancel;

        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        footer.Controls.AddRange(new Control[] { add, addCurrent, cancel });
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(12) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(navigation, 0, 0);
        layout.Controls.Add(entries, 0, 1);
        layout.Controls.Add(status, 0, 2);
        layout.Controls.Add(footer, 0, 3);
        Controls.Add(layout);

        Shown += async (_, _) => await Navigate(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        go.Click += async (_, _) => await Navigate(address.Text.Trim());
        address.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter)
                return;
            e.SuppressKeyPress = true;
            await Navigate(address.Text.Trim());
        };
        up.Click += async (_, _) => await Navigate(directory == null ? null : Directory.GetParent(directory)?.FullName);
        entries.SelectedIndexChanged += (_, _) => UpdateButtons();
        entries.DoubleClick += async (_, _) =>
        {
            if (entries.SelectedItems.Count != 1 || entries.SelectedItems[0].Tag is not Entry entry)
                return;
            if (entry.IsDirectory)
                await Navigate(entry.Path);
            else
                Finish(new[] { entry.Path });
        };
        add.Click += (_, _) => Finish(entries.SelectedItems.Cast<ListViewItem>().Select(item => ((Entry)item.Tag).Path).ToArray());
        addCurrent.Click += (_, _) =>
        {
            if (directory != null)
                Finish(new[] { directory });
        };
    }

    private async Task Navigate(string? path)
    {
        if (loading)
            return;
        loading = true;
        entries.Enabled = false;
        status.Text = "Чтение папки…";
        UpdateButtons();
        try
        {
            var requested = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
            var contents = await Task.Run(() => ReadEntries(requested));
            if (IsDisposed)
                return;
            directory = requested;
            address.Text = directory ?? "";
            entries.BeginUpdate();
            try
            {
                entries.Items.Clear();
                foreach (var entry in contents)
                    entries.Items.Add(new ListViewItem(new[] { entry.Name, entry.IsDirectory ? "Папка" : "Документ КОМПАС" }) { Tag = entry });
            }
            finally { entries.EndUpdate(); }
            status.Text = "Выделите файлы и папки. Двойной щелчок по папке открывает её.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            if (!IsDisposed)
                status.Text = "Не удалось открыть папку. Проверьте путь и доступ.";
        }
        finally
        {
            loading = false;
            if (!IsDisposed)
            {
                entries.Enabled = true;
                UpdateButtons();
            }
        }
    }

    private static Entry[] ReadEntries(string? folder)
    {
        if (folder == null)
            return Environment.GetLogicalDrives().Select(path => new Entry(path, path, true)).ToArray();

        var directory = new DirectoryInfo(folder);
        return directory.EnumerateDirectories().Select(item => new Entry(item.FullName, item.Name, true))
            .Concat(directory.EnumerateFiles().Where(item => AssemblyReport.IsKompasFile(item.Name))
                .Select(item => new Entry(item.FullName, item.Name, false)))
            .OrderByDescending(item => item.IsDirectory)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private void UpdateButtons()
    {
        up.Enabled = !loading && directory != null;
        go.Enabled = !loading;
        add.Enabled = !loading && entries.SelectedItems.Count != 0;
        addCurrent.Enabled = !loading && directory != null;
    }

    private void Finish(string[] paths)
    {
        SelectedPaths = paths;
        Close();
    }

    private sealed class Entry
    {
        public Entry(string path, string name, bool isDirectory)
        {
            Path = path;
            Name = name;
            IsDirectory = isDirectory;
        }

        public string Path { get; }
        public string Name { get; }
        public bool IsDirectory { get; }
    }
}
