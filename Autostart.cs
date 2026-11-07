using System;
using Microsoft.Win32;

namespace TaskbarPing;

/// <summary>Автозапуск через HKCU\...\Run - права администратора не нужны.</summary>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TaskbarPing";

    public static bool IsEnabled()
    {
        try
        {
            using RegistryKey? k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(ValueName) is string;
        }
        catch
        {
            return false;
        }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true)
                                  ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                string? path = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(path))
                    k.SetValue(ValueName, "\"" + path + "\"");
            }
            else
            {
                k.DeleteValue(ValueName, false);
            }
        }
        catch
        {
            // нет доступа к реестру - молча пропускаем
        }
    }
}
