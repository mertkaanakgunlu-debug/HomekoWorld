using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace HomekoWorld.Services.Capture;

public static class ScreenCapture
{
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    // ── BitBlt P/Invoke (CopyFromScreen'den ~2× hızlı, sıfır ek alloc) ────────
    [DllImport("gdi32.dll")] private static extern bool BitBlt(
        nint hdcDst, int xDst, int yDst, int w, int h,
        nint hdcSrc, int xSrc, int ySrc, uint rop);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hWnd);
    [DllImport("user32.dll")] private static extern int  ReleaseDC(nint hWnd, nint hDC);
    private const uint SRCCOPY = 0x00CC0020;

    // Capture the primary monitor into a Bitmap using physical pixel dimensions.
    // SystemParameters.PrimaryScreenWidth returns DIP units which misalign with
    // CopyFromScreen (physical pixels) on high-DPI displays.
    public static Bitmap CaptureScreen()
    {
        int w = GetSystemMetrics(0); // SM_CXSCREEN — physical pixels
        int h = GetSystemMetrics(1); // SM_CYSCREEN — physical pixels
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g   = Graphics.FromImage(bmp);
        nint hdcDst   = g.GetHdc();
        nint hdcSrc   = GetDC(nint.Zero);
        try   { BitBlt(hdcDst, 0, 0, w, h, hdcSrc, 0, 0, SRCCOPY); }
        finally
        {
            g.ReleaseHdc(hdcDst);
            ReleaseDC(nint.Zero, hdcSrc);
        }
        return bmp;
    }

    // Crop a region and split into barCount columns × slotCount rows of cell bitmaps
    public static Bitmap[,] SplitGrid(Bitmap source, System.Drawing.Rectangle region,
                                      int barCount, int slotCount)
    {
        float cellW = (float)region.Width  / barCount;
        float cellH = (float)region.Height / slotCount;

        var cells = new Bitmap[barCount, slotCount];
        for (int bar = 0; bar < barCount; bar++)
        for (int slot = 0; slot < slotCount; slot++)
        {
            var x = region.X + (int)(bar  * cellW);
            var y = region.Y + (int)(slot * cellH);
            var w = Math.Max(1, (int)cellW);
            var h = Math.Max(1, (int)cellH);

            var cell = new Bitmap(w, h);
            using var g = Graphics.FromImage(cell);
            g.DrawImage(source,
                new System.Drawing.Rectangle(0, 0, w, h),
                new System.Drawing.Rectangle(x, y, w, h),
                GraphicsUnit.Pixel);
            cells[bar, slot] = cell;
        }
        return cells;
    }

    // Convert a System.Drawing.Bitmap to a WPF BitmapSource for display
    public static BitmapSource ToBitmapSource(Bitmap bmp)
    {
        using var stream = new MemoryStream();
        bmp.Save(stream, ImageFormat.Png);
        stream.Position = 0;
        var bs = new BitmapImage();
        bs.BeginInit();
        bs.CacheOption  = BitmapCacheOption.OnLoad;
        bs.StreamSource = stream;
        bs.EndInit();
        bs.Freeze();
        return bs;
    }
}
