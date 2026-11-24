using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace KompasTime.Plugin;

internal sealed class HelpForm : Form
{
    public HelpForm()
    {
        Text = "KompasTime — Справка";
        Font = new Font("Segoe UI", 10);
        ClientSize = new Size(780, 560);
        MinimumSize = new Size(500, 320);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;

        using var reader = new StreamReader(typeof(HelpForm).Assembly
            .GetManifestResourceStream("KompasTime.README.md")!);
        var document = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = SystemColors.Window,
            DetectUrls = false,
            Text = reader.ReadToEnd()
        };
        document.Select(0, 0);

        var close = new Button { Text = "Закрыть", AutoSize = true, DialogResult = DialogResult.OK };
        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft
        };
        footer.Controls.Add(close);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(12) };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(document, 0, 0);
        layout.Controls.Add(footer, 0, 1);
        Controls.Add(layout);
        CancelButton = close;
    }
}
