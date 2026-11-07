using System;
using System.Drawing;

namespace TaskbarPing;

/// <summary>Вывод ARGB-картинки в layered-окно (попиксельная прозрачность).</summary>
internal static class Layered
{
    public static void Present(IntPtr hwnd, Bitmap bmp, Point loc)
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

            Native.UpdateLayeredWindow(hwnd, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, Native.ULW_ALPHA);
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
}
