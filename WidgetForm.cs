using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TaskbarPing;

/// <summary>
/// Прозрачное окно без рамки поверх панели задач (per-pixel alpha).
/// Две строки на сервис: имя (серым) и задержка (цветом). Тянется мышью, правый клик - меню.
/// </summary>
internal sealed class WidgetForm : Form
{
    private AppConfig _cfg;
    private PingMonitor _monitor;
    private DateTime _cfgStamp;

    private readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer { Interval = 500 };
    private readonly ContextMenuStrip _menu = new ContextMenuStrip();
    private readonly ToolStripMenuItem _miAutostart;

    private int _tick;
    private bool _lightTheme;
    private bool _hiddenFullscreen;

    private bool _dragging;
    private int _grabDx;

    private Point _loc;
    private Size _bmpSize;
    private int _trayLeft;
    private float _scale = 1f;

    private Bitmap? _bmp;
    private string _renderKey = "";
    private bool _needPresent = true;

    private readonly string _fontFamily;
    private Font? _nameFont;
    private Font? _valueFont;
    private float _fontScale = -1f;

    public WidgetForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Text = "TaskbarPing";

        _fontFamily = PickFont();

        AppConfig.TryLoad(out _cfg, out bool created);
        _cfgStamp = SafeStamp();
        _monitor = new PingMonitor(_cfg);
        _lightTheme = IsLightTaskbar();

        // первый запуск = автозапуск включаем сразу (потом можно выключить в меню)
        if (created) Autostart.Set(true);

        _menu.Items.Add("Перезагрузить конфиг", null, (_, _) => ReloadConfig());
        _menu.Items.Add("Открыть конфиг", null, (_, _) => OpenConfig());
        _miAutostart = new ToolStripMenuItem("Запускать вместе с Windows");
        _miAutostart.Click += (_, _) => Autostart.Set(!Autostart.IsEnabled());
        _menu.Items.Add(_miAutostart);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Выход", null, (_, _) => Close());
        _menu.Opening += (_, _) => _miAutostart.Checked = Autostart.IsEnabled();

        _timer.Tick += OnTick;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // рисуем только через UpdateLayeredWindow
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        UpdateLayout();
        _timer.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _monitor.Dispose();
        _bmp?.Dispose();
        _nameFont?.Dispose();
        _valueFont?.Dispose();
        _menu.Dispose();
        base.OnFormClosed(e);
    }

    // ---------- таймер ----------

    private void OnTick(object? sender, EventArgs e)
    {
        _tick++;
        try
        {
            if (_tick % 10 == 0) _lightTheme = IsLightTaskbar();
            if (_tick % 4 == 0 && SafeStamp() != _cfgStamp) ReloadConfig();

            bool hide = ShouldHide();
            if (hide != _hiddenFullscreen)
            {
                _hiddenFullscreen = hide;
                Native.ShowWindow(Handle, hide ? Native.SW_HIDE : Native.SW_SHOWNOACTIVATE);
                _needPresent = true;
            }

            if (hide) return;

            UpdateLayout();

            // панель задач тоже topmost - периодически возвращаем себя наверх
            if (_tick % 2 == 0)
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
        catch
        {
            // виджет не должен падать из-за разовой ошибки
        }
    }

    private static bool ShouldHide()
    {
        // полноэкранное приложение / игра / презентация -> прячемся
        if (Native.SHQueryUserNotificationState(out int state) != 0) return false;
        return state == 2 || state == 3 || state == 4;
    }

    // ---------- раскладка и отрисовка ----------

    private bool TryGetTaskbar(out Native.RECT tb)
    {
        tb = default;
        IntPtr tray = Native.FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero || !Native.GetWindowRect(tray, out tb)) return false;

        _trayLeft = tb.Right - (int)(320 * _scale); // запасной вариант
        IntPtr notify = Native.FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
        if (notify != IntPtr.Zero && Native.GetWindowRect(notify, out Native.RECT nr) && nr.Right - nr.Left > 20)
            _trayLeft = nr.Left;

        return true;
    }

    private void UpdateLayout()
    {
        _scale = DeviceDpi / 96f;
        if (!TryGetTaskbar(out Native.RECT tb)) return;

        int tbH = tb.Bottom - tb.Top;
        if (tbH <= 0 || (tb.Right - tb.Left) < tbH) return; // вертикальная панель не поддерживается

        var items = new List<(string Name, string Value, Color Color)>();
        foreach (ServiceState st in _monitor.States)
        {
            long r = st.Rtt;
            string text;
            Color color;
            if (r == ServiceState.Unknown)
            {
                text = "…";
                color = Neutral();
            }
            else if (r == ServiceState.Lost)
            {
                text = "lost";
                color = Red();
            }
            else
            {
                text = r < 1 ? "<1 ms" : r + " ms";
                color = r <= _cfg.GoodMs ? Green() : Yellow();
            }
            items.Add((st.Name, text, color));
        }

        string key = tbH + "|" + _scale + "|" + _lightTheme;
        foreach (var it in items) key += "|" + it.Name + "=" + it.Value + "=" + it.Color.ToArgb();

        if (key != _renderKey || _bmp == null)
        {
            Bitmap newBmp = RenderBitmap(items, tbH, _scale);
            _bmp?.Dispose();
            _bmp = newBmp;
            _bmpSize = newBmp.Size;
            _renderKey = key;
            _needPresent = true;
        }

        var loc = new Point(
            _trayLeft - _bmpSize.Width - (int)Math.Round(_cfg.OffsetX * _scale),
            tb.Top + (tbH - _bmpSize.Height) / 2);

        if (_needPresent || loc != _loc)
        {
            _loc = loc;
            Present(_bmp, loc);
            _needPresent = false;
        }
    }

    private Bitmap RenderBitmap(List<(string Name, string Value, Color Color)> items, int height, float scale)
    {
        EnsureFonts(scale);

        using var mb = new Bitmap(1, 1);
        using Graphics mg = Graphics.FromImage(mb);
        mg.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var fmt = (StringFormat)StringFormat.GenericTypographic.Clone();
        fmt.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;

        float minValW = mg.MeasureString("999 ms", _valueFont!, PointF.Empty, fmt).Width;
        float nameH = mg.MeasureString("Ag", _nameFont!, PointF.Empty, fmt).Height;
        float valH = mg.MeasureString("Ag", _valueFont!, PointF.Empty, fmt).Height;
        float padX = 6 * scale;
        float gap = 16 * scale;

        var colW = new float[items.Count];
        float total = padX * 2;
        for (int i = 0; i < items.Count; i++)
        {
            float nw = mg.MeasureString(items[i].Name, _nameFont!, PointF.Empty, fmt).Width;
            float vw = mg.MeasureString(items[i].Value, _valueFont!, PointF.Empty, fmt).Width;
            colW[i] = Math.Max(Math.Max(nw, vw), minValW); // фиксированный минимум - чтобы виджет не "дышал"
            total += colW[i];
            if (i > 0) total += gap;
        }

        int w = Math.Max(1, (int)Math.Ceiling(total));
        var bmp = new Bitmap(w, Math.Max(1, height), PixelFormat.Format32bppArgb);
        using Graphics g = Graphics.FromImage(bmp);
        // почти невидимая подложка (alpha=1): благодаря ей окно ловит мышь для перетаскивания и меню
        g.Clear(Color.FromArgb(1, 0, 0, 0));
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        float top = (height - (nameH + valH)) / 2f;
        float x = padX;
        using var nameBrush = new SolidBrush(_lightTheme ? Color.FromArgb(96, 96, 96) : Color.FromArgb(190, 190, 190));
        for (int i = 0; i < items.Count; i++)
        {
            g.DrawString(items[i].Name, _nameFont!, nameBrush, x, top, fmt);
            using var valBrush = new SolidBrush(items[i].Color);
            g.DrawString(items[i].Value, _valueFont!, valBrush, x, top + nameH, fmt);
            x += colW[i] + gap;
        }

        return bmp;
    }

    private void Present(Bitmap bmp, Point loc)
    {
        IntPtr screenDc = Native.GetDC(IntPtr.Zero);
        IntPtr memDc = Native.CreateCompatibleDC(screenDc);
        IntPtr hBmp = IntPtr.Zero;
        IntPtr old = IntPtr.Zero;
        try
        {
            hBmp = bmp.GetHbitmap(Color.FromArgb(0));
            old = Native.SelectObject(memDc, hBmp);

            var size = new Native.SIZE { cx = bmp.Width, cy = bmp.Height };
            var src = new Native.POINT { X = 0, Y = 0 };
            var dst = new Native.POINT { X = loc.X, Y = loc.Y };
            var blend = new Native.BLENDFUNCTION
            {
                BlendOp = Native.AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = Native.AC_SRC_ALPHA
            };

            Native.UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, Native.ULW_ALPHA);
        }
        finally
        {
            if (hBmp != IntPtr.Zero)
            {
                Native.SelectObject(memDc, old);
                Native.DeleteObject(hBmp);
            }
            Native.DeleteDC(memDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private void EnsureFonts(float scale)
    {
        if (Math.Abs(_fontScale - scale) < 0.001f && _nameFont != null && _valueFont != null) return;
        _nameFont?.Dispose();
        _valueFont?.Dispose();
        _nameFont = new Font(_fontFamily, 11f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        _valueFont = new Font(_fontFamily, 14f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
        _fontScale = scale;
    }

    private static string PickFont()
    {
        foreach (string name in new[] { "Segoe UI Variable Text", "Segoe UI" })
        {
            try
            {
                using var f = new FontFamily(name);
                return name;
            }
            catch
            {
                // нет такого шрифта
            }
        }
        return "Arial";
    }

    // ---------- цвета (тёмная и светлая панель задач) ----------

    private Color Green() => _lightTheme ? Color.FromArgb(16, 130, 60) : Color.FromArgb(80, 220, 110);
    private Color Yellow() => _lightTheme ? Color.FromArgb(170, 115, 0) : Color.FromArgb(255, 205, 60);
    private Color Red() => _lightTheme ? Color.FromArgb(196, 30, 30) : Color.FromArgb(255, 85, 85);
    private Color Neutral() => _lightTheme ? Color.FromArgb(110, 110, 110) : Color.FromArgb(150, 150, 150);

    private static bool IsLightTaskbar()
    {
        try
        {
            using RegistryKey? k = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("SystemUsesLightTheme") is int v && v == 1;
        }
        catch
        {
            return false;
        }
    }

    // ---------- конфиг ----------

    private static DateTime SafeStamp()
    {
        try { return File.GetLastWriteTimeUtc(AppConfig.ConfigPath); }
        catch { return DateTime.MinValue; }
    }

    private void ReloadConfig()
    {
        _cfgStamp = SafeStamp();
        if (!AppConfig.TryLoad(out AppConfig fresh, out _)) return; // файл битый - оставляем текущие настройки

        PingMonitor old = _monitor;
        _cfg = fresh;
        _monitor = new PingMonitor(_cfg);
        old.Dispose();
        _renderKey = "";
    }

    private static void OpenConfig()
    {
        try
        {
            Process.Start(new ProcessStartInfo("notepad.exe", "\"" + AppConfig.ConfigPath + "\"") { UseShellExecute = true });
        }
        catch
        {
            // ignore
        }
    }

    // ---------- мышь: перетаскивание и меню ----------

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            _dragging = true;
            _grabDx = Cursor.Position.X - _loc.X;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;

        int newX = Cursor.Position.X - _grabDx;
        double px = _trayLeft - _bmpSize.Width - newX;
        _cfg.OffsetX = (int)Math.Clamp(Math.Round(px / _scale), -20, 4000);
        UpdateLayout();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left && _dragging)
        {
            _dragging = false;
            _cfg.Save();
            _cfgStamp = SafeStamp();
        }
        else if (e.Button == MouseButtons.Right)
        {
            Native.SetForegroundWindow(Handle);
            _menu.Show(Cursor.Position);
        }
    }
}
