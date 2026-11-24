using System;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace KompasTime;

internal sealed class HistoryForm : Form
{
    private readonly DocumentHistory history;
    private readonly bool readOnly;
    private readonly DataGridView grid = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        BackgroundColor = Color.White
    };
    private readonly Button remove = new() { Text = "Удалить запись", AutoSize = true };
    private readonly Label status = new() { AutoSize = true, Margin = new Padding(0, 8, 0, 0) };

    public HistoryForm(string documentName, DocumentHistory history, bool readOnly)
    {
        this.history = history;
        this.readOnly = readOnly;
        Text = $"История трудозатрат · {documentName}";
        Font = new Font("Segoe UI", 10);
        ClientSize = new Size(690, 365);
        MinimumSize = new Size(535, 270);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;

        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Автор", FillWeight = 45, SortMode = DataGridViewColumnSortMode.NotSortable
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Дата и время", FillWeight = 32, SortMode = DataGridViewColumnSortMode.NotSortable
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Трудозатраты", FillWeight = 23, SortMode = DataGridViewColumnSortMode.NotSortable
        });

        var footer = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        if (!readOnly)
            footer.Controls.Add(remove);
        footer.Controls.Add(status);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(12) };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(grid, 0, 0);
        layout.Controls.Add(footer, 0, 1);
        Controls.Add(layout);

        grid.SelectionChanged += (_, _) => UpdateRemoveButton();
        remove.Click += (_, _) => RemoveSelected();
        history.Updated += RefreshRows;
        FormClosed += (_, _) => history.Updated -= RefreshRows;
        RefreshRows();
    }

    private void RefreshRows()
    {
        grid.Rows.Clear();
        foreach (var entry in history.VisibleEntries.OrderByDescending(entry => entry.SavedAtUtc))
        {
            var date = entry.SavedAtUtc.ToLocalTime().ToString("dd.MM.yy HH:mm", CultureInfo.InvariantCulture);
            var row = grid.Rows.Add(entry.AuthorName, date, WorkDuration.Format(entry.TotalSeconds));
            grid.Rows[row].Tag = entry;
        }
        grid.ClearSelection();
        status.Text = history.HasPendingRemovals ? "Удаление будет применено при сохранении в КОМПАС."
            : grid.Rows.Count == 0 ? history.Issue ?? "В истории пока нет записей."
            : readOnly ? "Только чтение" : "";
        UpdateRemoveButton();
    }

    private void UpdateRemoveButton() =>
        remove.Enabled = !readOnly && history.CanRemove && grid.SelectedRows.Count == 1;

    private void RemoveSelected()
    {
        if (!remove.Enabled || grid.SelectedRows[0].Tag is not WorkEntry entry)
            return;
        if (MessageBox.Show(this, "Удалить выбранную запись из истории трудозатрат?",
            "Удаление записи", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2)
            == DialogResult.Yes)
            history.RemoveEntry(entry);
    }
}
