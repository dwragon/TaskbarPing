using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Net;
using System.Windows.Forms;

namespace TaskbarPing;

/// <summary>
/// Всплывающая карточка над панелью задач: адрес, текущая/средняя/мин/макс задержка,
/// процент потерь и график за последние N минут. Окно "прозрачное для мыши" и не берёт фокус.
/// </summary>
internal sealed class HistoryPopup : Form
{
    private readonly string _family;
    private Bitmap? _bmp;

    private float _fontScale = -1f;
    private Font? _fTitle, _fHost, _fBig, _fSmall, _fStat;
    private StringFormat? _fmtNear, _fmtFar;

    public HistoryPopup(string fontFamily)
    {
        _family = fontFamily;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Text = "TaskbarPing popup";
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // рисуем только через UpdateLayeredWindow
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _bmp?.Dispose();
        _fTitle?.Dispose();
        _fHost?.Dispose();
        _fBig?.Dispose();
        _fSmall?.Dispose();
        _fStat?.Dispose();
        _fmtNear?.Dispose();
        _fmtFar?.Dispose();
        base.OnFormClosed(e);
    }

    public void ShowFor(ServiceState st, AppConfig cfg, bool light, float scale, int anchorX, Rectangle taskbar, Rectangle screen)
    {
        Bitmap bmp = Render(st, cfg, light, scale);
        _bmp?.Dispose();
        _bmp = bmp;

        int x = anchorX - bmp.Width / 2;
        x = Math.Max(screen.Left, Math.Min(x, screen.Right - bmp.Width));
        int gap = (int)Math.Round(2 * scale);
        int y = taskbar.Top - bmp.Height - gap;
        if (y < screen.Top) y = taskbar.Bottom + gap; // панель вверху - показываем под ней

        if (!Visible) Show();
        Layered.Present(Handle, bmp, new Point(x, y));
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    public void HidePopup()
    {
        if (Visible) Hide();
    }

    // ---------- отрисовка ----------

    private void EnsureFonts(float s)
    {
        if (Math.Abs(_fontScale - s) < 0.001f && _fTitle != null) return;

        _fTitle?.Dispose();
        _fHost?.Dispose();
        _fBig?.Dispose();
        _fSmall?.Dispose();
        _fStat?.Dispose();
        _fmtNear?.Dispose();
        _fmtFar?.Dispose();

        _fTitle = new Font(_family, 14f * s, FontStyle.Bold, GraphicsUnit.Pixel);
        _fHost = new Font(_family, 11f * s, FontStyle.Regular, GraphicsUnit.Pixel);
        _fBig = new Font(_family, 26f * s, FontStyle.Bold, GraphicsUnit.Pixel);
        _fSmall = new Font(_family, 10.5f * s, FontStyle.Regular, GraphicsUnit.Pixel);
        _fStat = new Font(_family, 14f * s, FontStyle.Bold, GraphicsUnit.Pixel);

        _fmtNear = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Near,
            Trimming = StringTrimming.EllipsisCharacter
        };
        _fmtNear.FormatFlags |= StringFormatFlags.NoWrap;
        _fmtFar = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Far,
            Trimming = StringTrimming.EllipsisCharacter
        };
        _fmtFar.FormatFlags |= StringFormatFlags.NoWrap;

        _fontScale = s;
    }

    private static GraphicsPath Rounded(RectangleF r, float rad)
    {
        float d = Math.Max(0.1f, Math.Min(rad * 2f, Math.Min(r.Width, r.Height)));
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private static string WindowText(int minutes)
    {
        if (minutes >= 60 && minutes % 60 == 0) return (minutes / 60) + " h";
        return minutes + " min";
    }

    private static string HostText(ServiceState st)
    {
        string? addr = st.Address;
        if (IPAddress.TryParse(st.Host, out _)) return st.Host;
        return string.IsNullOrEmpty(addr) ? st.Host : st.Host + " · " + addr;
    }

    private Bitmap Render(ServiceState st, AppConfig cfg, bool light, float s)
    {
        EnsureFonts(s);

        int sh = (int)Math.Ceiling(10 * s);                 // место под тень
        int cardW = (int)Math.Round(300 * s);
        int cardH = (int)Math.Round(222 * s);
        var bmp = new Bitmap(cardW + sh * 2, cardH + sh * 2, PixelFormat.Format32bppArgb);

        using Graphics g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);

        float radius = 10 * s;
        var card = new RectangleF(sh, sh, cardW, cardH);

        // мягкая тень
        for (int i = sh; i >= 1; i--)
        {
            RectangleF r = RectangleF.Inflate(card, i, i);
            r.Offset(0, 2 * s);
            using GraphicsPath sp = Rounded(r, radius + i);
            using var sb = new SolidBrush(Color.FromArgb(5, 0, 0, 0));
            g.FillPath(sb, sp);
        }

        using (GraphicsPath cp = Rounded(card, radius))
        using (var bg = new SolidBrush(Theme.CardBg(light)))
        using (var border = new Pen(Theme.Border(light), Math.Max(1f, s)))
        {
            g.FillPath(bg, cp);
            g.DrawPath(border, cp);
        }

        // ---- статистика за окно ----
        long now = Environment.TickCount64;
        long window = cfg.HistoryMinutes * 60_000L;
        List<PingSample> samples = st.Snapshot(now - window);

        int ok = 0, lost = 0, min = int.MaxValue, max = 0;
        long sum = 0;
        foreach (PingSample sm in samples)
        {
            if (sm.Rtt >= 0)
            {
                ok++;
                sum += sm.Rtt;
                if (sm.Rtt < min) min = sm.Rtt;
                if (sm.Rtt > max) max = sm.Rtt;
            }
            else
            {
                lost++;
            }
        }

        int total = ok + lost;
        string avgText = ok > 0 ? Math.Round(sum / (double)ok) + " ms" : "—";
        string minText = ok > 0 ? min + " ms" : "—";
        string maxText = ok > 0 ? max + " ms" : "—";
        string lossText;
        if (total == 0) lossText = "—";
        else
        {
            double pct = lost * 100.0 / total;
            lossText = pct <= 0 ? "0%" : (pct < 10 ? pct.ToString("0.0", CultureInfo.InvariantCulture) : Math.Round(pct).ToString(CultureInfo.InvariantCulture)) + "%";
        }

        using var textBrush = new SolidBrush(Theme.Text(light));
        using var subBrush = new SolidBrush(Theme.Sub(light));
        Color redColor = Theme.Red(light);
        Color greenColor = Theme.Green(light);
        Color yellowColor = Theme.Yellow(light);

        float x0 = sh + 14 * s;
        float cw = cardW - 28 * s;
        float y0 = sh;

        // ---- заголовок: имя слева, адрес справа ----
        g.DrawString(HostText(st), _fHost!, subBrush, new RectangleF(x0 + cw * 0.40f, y0 + 14 * s, cw * 0.60f, 18 * s), _fmtFar!);
        g.DrawString(st.Name, _fTitle!, textBrush, new RectangleF(x0, y0 + 11 * s, cw * 0.40f, 22 * s), _fmtNear!);

        // ---- текущее значение ----
        long cur = st.Rtt;
        string curText;
        Color curColor;
        if (cur == ServiceState.Unknown) { curText = "…"; curColor = Theme.Neutral(light); }
        else if (cur == ServiceState.Lost) { curText = "lost"; curColor = redColor; }
        else
        {
            curText = cur < 1 ? "<1 ms" : cur + " ms";
            curColor = cur <= cfg.GoodMs ? greenColor : yellowColor;
        }

        using (var curBrush = new SolidBrush(curColor))
            g.DrawString(curText, _fBig!, curBrush, new RectangleF(x0, y0 + 34 * s, cw * 0.62f, 36 * s), _fmtNear!);
        g.DrawString("last " + WindowText(cfg.HistoryMinutes), _fSmall!, subBrush,
            new RectangleF(x0 + cw * 0.55f, y0 + 50 * s, cw * 0.45f, 16 * s), _fmtFar!);

        // ---- график ----
        var chart = new RectangleF(x0, y0 + 76 * s, cw, 72 * s);
        using (GraphicsPath chartPath = Rounded(chart, 6 * s))
        using (var chartBg = new SolidBrush(Theme.ChartBg(light)))
        {
            g.FillPath(chartBg, chartPath);
            g.SetClip(chartPath);
            DrawChart(g, chart, samples, cfg, light, s, now, window, ok, max, lost, greenColor, yellowColor, redColor, subBrush);
            g.ResetClip();
        }

        g.DrawString(WindowText(cfg.HistoryMinutes) + " ago", _fSmall!, subBrush,
            new RectangleF(x0, chart.Bottom + 3 * s, cw / 2, 16 * s), _fmtNear!);
        g.DrawString("now", _fSmall!, subBrush,
            new RectangleF(x0 + cw / 2, chart.Bottom + 3 * s, cw / 2, 16 * s), _fmtFar!);

        // ---- статистика ----
        string[] labels = { "AVG", "MIN", "MAX", "LOSS" };
        string[] values = { avgText, minText, maxText, lossText };
        float colW = cw / 4f;
        float sy = chart.Bottom + 26 * s;
        using var redBrush = new SolidBrush(redColor);
        for (int i = 0; i < 4; i++)
        {
            float x = x0 + i * colW;
            g.DrawString(labels[i], _fSmall!, subBrush, new RectangleF(x, sy, colW, 14 * s), _fmtNear!);
            Brush vb = (i == 3 && lost > 0) ? redBrush : textBrush;
            g.DrawString(values[i], _fStat!, vb, new RectangleF(x, sy + 14 * s, colW, 20 * s), _fmtNear!);
        }

        return bmp;
    }

    private void DrawChart(Graphics g, RectangleF chart, List<PingSample> samples, AppConfig cfg, bool light, float s,
        long now, long window, int ok, int max, int lost, Color green, Color yellow, Color red, Brush subBrush)
    {
        RectangleF plot = RectangleF.Inflate(chart, -6 * s, -8 * s);
        int cols = Math.Max(2, (int)plot.Width);
        double colMs = window / (double)cols;
        long t0 = now - window;

        var sum = new double[cols];
        var cnt = new int[cols];
        var lostC = new int[cols];
        foreach (PingSample sm in samples)
        {
            int c = (int)((sm.T - t0) / colMs);
            if (c < 0) c = 0;
            if (c >= cols) c = cols - 1;
            if (sm.Rtt >= 0) { sum[c] += sm.Rtt; cnt[c]++; }
            else lostC[c]++;
        }

        double ymax = ok > 0
            ? Math.Max(max * 1.15, Math.Max(cfg.GoodMs * 1.25, 20))
            : Math.Max(cfg.GoodMs * 2.0, 40);

        float PlotY(double v) => plot.Bottom - (float)(v / ymax) * plot.Height;
        float colW = plot.Width / cols;

        // порог "хорошо"
        if (cfg.GoodMs < ymax)
        {
            float gy = PlotY(cfg.GoodMs);
            using var gridPen = new Pen(Color.FromArgb(90, green), Math.Max(1f, s)) { DashStyle = DashStyle.Dash };
            g.DrawLine(gridPen, plot.Left, gy, plot.Right, gy);
        }

        // потери: красные полосы
        using (var lostFull = new SolidBrush(Color.FromArgb(70, red)))
        using (var lostTick = new SolidBrush(Color.FromArgb(210, red)))
        {
            for (int c = 0; c < cols; c++)
            {
                if (lostC[c] == 0) continue;
                float x = plot.Left + c * colW;
                float w = Math.Max(1f, colW);
                if (cnt[c] == 0) g.FillRectangle(lostFull, x, chart.Top, w, chart.Height);
                else g.FillRectangle(lostTick, x, plot.Bottom - 5 * s, w, 5 * s);
            }
        }

        // линия: цвет сегмента зависит от значения
        int maxGap = Math.Max(2, (int)Math.Ceiling(2.5 * cfg.IntervalMs / colMs));
        var connected = new bool[cols];
        int prev = -1;
        bool brk = false;
        using (var linePen = new Pen(Color.White, Math.Max(1.5f, 1.6f * s)))
        {
            linePen.LineJoin = LineJoin.Round;
            linePen.StartCap = LineCap.Round;
            linePen.EndCap = LineCap.Round;

            for (int c = 0; c < cols; c++)
            {
                if (cnt[c] == 0)
                {
                    if (lostC[c] > 0) brk = true;
                    continue;
                }

                double v = sum[c] / cnt[c];
                if (prev >= 0 && !brk && c - prev <= maxGap)
                {
                    double pv = sum[prev] / cnt[prev];
                    var p1 = new PointF(plot.Left + (prev + 0.5f) * colW, PlotY(pv));
                    var p2 = new PointF(plot.Left + (c + 0.5f) * colW, PlotY(v));
                    Color segColor = v <= cfg.GoodMs ? green : yellow;

                    using (var fill = new SolidBrush(Color.FromArgb(40, segColor)))
                        g.FillPolygon(fill, new[] { p1, p2, new PointF(p2.X, plot.Bottom), new PointF(p1.X, plot.Bottom) });

                    linePen.Color = segColor;
                    g.DrawLine(linePen, p1, p2);
                    connected[prev] = true;
                    connected[c] = true;
                }

                prev = c;
                brk = false;
            }
        }

        // одиночные точки (когда соседей нет)
        float dot = 1.8f * s;
        for (int c = 0; c < cols; c++)
        {
            if (cnt[c] == 0 || connected[c]) continue;
            double v = sum[c] / cnt[c];
            using var db = new SolidBrush(v <= cfg.GoodMs ? green : yellow);
            g.FillEllipse(db, plot.Left + (c + 0.5f) * colW - dot, PlotY(v) - dot, dot * 2, dot * 2);
        }

        // подпись верхней границы шкалы
        g.DrawString(((int)Math.Ceiling(ymax)) + " ms", _fSmall!, subBrush,
            new RectangleF(chart.Left + 6 * s, chart.Top + 1 * s, chart.Width / 2, 14 * s), _fmtNear!);
    }
}
