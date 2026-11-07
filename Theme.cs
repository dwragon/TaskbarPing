using System.Drawing;

namespace TaskbarPing;

/// <summary>Цвета для тёмной и светлой темы Windows.</summary>
internal static class Theme
{
    public static Color Green(bool light) => light ? Color.FromArgb(16, 130, 60) : Color.FromArgb(80, 220, 110);
    public static Color Yellow(bool light) => light ? Color.FromArgb(170, 115, 0) : Color.FromArgb(255, 205, 60);
    public static Color Red(bool light) => light ? Color.FromArgb(196, 30, 30) : Color.FromArgb(255, 85, 85);
    public static Color Neutral(bool light) => light ? Color.FromArgb(110, 110, 110) : Color.FromArgb(150, 150, 150);

    public static Color Text(bool light) => light ? Color.FromArgb(32, 32, 32) : Color.FromArgb(242, 242, 242);
    public static Color Sub(bool light) => light ? Color.FromArgb(96, 96, 96) : Color.FromArgb(172, 172, 172);
    public static Color CardBg(bool light) => light ? Color.FromArgb(250, 250, 250, 250) : Color.FromArgb(250, 36, 36, 36);
    public static Color Border(bool light) => light ? Color.FromArgb(206, 206, 206) : Color.FromArgb(72, 72, 72);
    public static Color ChartBg(bool light) => light ? Color.FromArgb(238, 238, 238) : Color.FromArgb(46, 46, 46);
}
