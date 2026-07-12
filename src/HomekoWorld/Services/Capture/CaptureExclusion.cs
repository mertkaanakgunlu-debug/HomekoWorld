using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace HomekoWorld.Services.Capture;

/// <summary>
/// Overlay/HUD pencerelerini ekran yakalamasından (DXGI duplication + GDI BitBlt) hariç tutar.
/// Gerekçe (2026-07-11 replay analizi): DetectionOverlayWindow'un kutuları DXGI karelerine
/// giriyordu — model KENDİ çizdiği kutuları girdi olarak görüyor, replay kareleri de
/// eğitim/benchmark için kirleniyordu. WDA_EXCLUDEFROMCAPTURE pencereyi kullanıcı ekranında
/// görünür bırakır ama yakalanan içerikten çıkarır.
///
/// Kısıt: Win10 2004+ gerektirir; daha eski Windows'ta çağrı başarısız olur → sessiz devam
/// (davranış eskisi gibi: pencere yakalamaya girer). Yan etki: hariç tutulan pencereler
/// kullanıcının KENDİ ekran kayıtlarında (OBS vb.) da görünmez — sürüm notuna yazılmalı.
/// </summary>
internal static class CaptureExclusion
{
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    /// <summary>
    /// Pencereyi yakalamadan hariç tutmayı dener. Pencere Loaded olduktan SONRA çağrılmalı
    /// (hwnd hazır olmalı). Asla fırlatmaz; başarısızlık bir kez loglanır.
    /// </summary>
    public static void TryExclude(Window window, string name)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                Program.Log($"[Capture] {name}: hwnd hazır değil — capture-hariç atlandı");
                return;
            }
            if (SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE))
                Program.Log($"[Capture] {name}: yakalamadan hariç tutuldu (WDA_EXCLUDEFROMCAPTURE)");
            else
                Program.Log($"[Capture] {name}: SetWindowDisplayAffinity başarısız (err={Marshal.GetLastWin32Error()}) " +
                            "— eski Windows olabilir; pencere yakalamaya girmeye devam eder");
        }
        catch (Exception ex)
        {
            Program.Log($"[Capture] {name}: capture-hariç hata: {ex.Message}");
        }
    }
}
