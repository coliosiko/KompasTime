using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace KompasTime.Plugin;

internal sealed class SettingsForm : Form
{
    private const string Key = @"Software\KompasTime";
    private readonly NumericUpDown minutes = new() { Minimum = 1, Maximum = 120, Width = 70 };
    private readonly NumericUpDown scale = new() { Minimum = 60, Maximum = 180, Increment = 10, Width = 70 };
    private readonly ComboBox corner = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly CheckBox start = new() { Text = "Подключать и включать учёт при запуске КОМПАС", AutoSize = true };

    public static int IdleMinutes => Read("IdleMinutes", 3, 1, 120);
    public static int Corner => Read("OverlayCorner", 3, 0, 3);
    public static int IconScale => Read("OverlayScale", 100, 60, 180);
    public static bool AutoConnect => Read("AutoConnect", 1, 0, 1) == 1;
    public static bool IconVisible
    {
        get => Read("OverlayVisible", 1, 0, 1) == 1;
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(Key);
            key.SetValue("OverlayVisible", value ? 1 : 0, RegistryValueKind.DWord);
        }
    }

    private static int Read(string name, int fallback, int min, int max) =>
        Registry.GetValue(@"HKEY_CURRENT_USER\" + Key, name, fallback) is int value && value >= min && value <= max
            ? value : fallback;

    public SettingsForm()
    {
        Text = "KompasTime — Настройки";
        Font = new Font("Segoe UI", 10);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        minutes.Value = IdleMinutes;
        scale.Value = IconScale;
        corner.Items.AddRange(new object[] { "Верхний левый", "Верхний правый", "Нижний левый", "Нижний правый" });
        corner.SelectedIndex = Corner;
        start.Checked = AutoConnect;

        var layout = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Padding = new Padding(12) };
        AddSetting(layout, "Порог бездействия, мин", minutes);
        AddSetting(layout, "Размещение значка", corner);
        AddSetting(layout, "Масштаб значка, %", scale);
        layout.Controls.Add(start, 0, 3);
        layout.SetColumnSpan(start, 2);
        var save = new Button { Text = "Сохранить", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Отмена", AutoSize = true, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { AutoSize = true };
        buttons.Controls.AddRange(new Control[] { save, cancel });
        layout.Controls.Add(buttons, 0, 4);
        layout.SetColumnSpan(buttons, 2);
        Controls.Add(layout);
        AcceptButton = save;
        CancelButton = cancel;
    }

    private static void AddSetting(TableLayoutPanel layout, string label, Control input)
    {
        var row = layout.RowCount++;
        layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        layout.Controls.Add(input, 1, row);
    }

    public void Edit(IWin32Window? owner)
    {
        if (ShowDialog(owner) != DialogResult.OK)
            return;
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        key.SetValue("IdleMinutes", (int)minutes.Value, RegistryValueKind.DWord);
        key.SetValue("OverlayCorner", corner.SelectedIndex, RegistryValueKind.DWord);
        key.SetValue("OverlayScale", (int)scale.Value, RegistryValueKind.DWord);
        key.SetValue("AutoConnect", start.Checked ? 1 : 0, RegistryValueKind.DWord);
    }
}
