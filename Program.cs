using System;
using System.Windows.Forms;

namespace KompasTime;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm(Array.IndexOf(args, "--demo") >= 0));
    }
}
