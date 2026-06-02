using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace HomekoWorld.Services.Capture;

/// <summary>
/// GDI BitBlt ile tam ekran yakalama — DXGI kullanılamadığında (exclusive-fullscreen, RDP, sürücü
/// sorunu) güvenli fallback. Mevcut <see cref="ScreenCapture.CaptureScreen"/> ile aynı yöntem; tek
/// farkı tek bir Bitmap'i YENİDEN KULLANMASI (kare başına ~16MB alloc + GC baskısı önlenir).
/// </summary>
public sealed class GdiScreenSource : IScreenSource
{
    [DllImport("user32.dll")] private static extern int  GetSystemMetrics(int nIndex);
    [DllImport("gdi32.dll")]  private static extern bool BitBlt(
        nint hdcDst, int xDst, int yDst, int w, int h, nint hdcSrc, int xSrc, int ySrc, uint rop);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hWnd);
    [DllImport("user32.dll")] private static extern int  ReleaseDC(nint hWnd, nint hDC);
    private const uint SRCCOPY = 0x00CC0020;

    private Bitmap? _buffer;

    public string BackendName => "GDI";

    public Bitmap Capture()
    {
        int w = GetSystemMetrics(0); // SM_CXSCREEN — fiziksel piksel (DPI-bağımsız değil)
        int h = GetSystemMetrics(1); // SM_CYSCREEN
        if (_buffer is null || _buffer.Width != w || _buffer.Height != h)
        {
            _buffer?.Dispose();
            _buffer = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        }

        using var g = Graphics.FromImage(_buffer);
        nint hdcDst = g.GetHdc();
        nint hdcSrc = GetDC(nint.Zero);
        try   { BitBlt(hdcDst, 0, 0, w, h, hdcSrc, 0, 0, SRCCOPY); }
        finally
        {
            g.ReleaseHdc(hdcDst);
            ReleaseDC(nint.Zero, hdcSrc);
        }
        return _buffer;
    }

    public void Dispose()
    {
        _buffer?.Dispose();
        _buffer = null;
    }
}
