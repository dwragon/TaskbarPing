using System;
using System.Threading;
using System.Windows.Forms;

namespace TaskbarPing;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // только один экземпляр
        using var mutex = new Mutex(true, @"Local\TaskbarPing.SingleInstance", out bool isNew);
        if (!isNew) return;

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (_, _) => { /* не показываем окна ошибок в трее */ };

        Application.Run(new WidgetForm());
    }
}
