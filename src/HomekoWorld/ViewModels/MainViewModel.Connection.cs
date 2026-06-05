using System.Collections.ObjectModel;
using System.Drawing;
using System.Linq;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HomekoWorld.Engine;
using HomekoWorld.Hardware;
using HomekoWorld.Hooks;
using HomekoWorld.Models;
using HomekoWorld.Models.Farm;
using HomekoWorld.Services;
using HomekoWorld.Services.Farm;
using HomekoWorld.Services.Skills;
using HomekoWorld.Services.Vision;
using HomekoWorld.Services.Yolo;
using Microsoft.Win32;
using System.IO;

namespace HomekoWorld.ViewModels;

public partial class MainViewModel
{
    // ---- Commands ----
    [RelayCommand]
    private async Task ConnectToggleAsync()
    {
        if (IsLocalMode || IsRp2040Mode) return; // button hidden in local/rp2040 mode; guard defensively
        _reconnectCts?.Cancel();
        if (IsConnected)
        {
            _transport.Disconnect();
            IsConnected   = false;
            ConnectStatus = "Bağlı değil";
            return;
        }
        ConnectStatus = "Bağlanıyor…";
        var ok = await _transport.ConnectAsync(PhoneHost, PhonePort);
        IsConnected   = ok;
        ConnectStatus = ok ? $"Bağlı: {PhoneHost}:{PhonePort}" : "Bağlantı başarısız";
        LogMessage    = ok ? "Telefona bağlandı — test için tuşa bas" : "Bağlantı kurulamadı";
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        _reconnectCts?.Cancel();
        ConnectStatus = "Bağlanıyor…";
        var ok = await _transport.ConnectAsync(PhoneHost, PhonePort);
        IsConnected   = ok;
        ConnectStatus = ok ? $"Bağlı: {PhoneHost}:{PhonePort}" : "Bağlantı başarısız";
        LogMessage    = ok ? "Telefona bağlandı — test için tuşa bas" : "Bağlantı kurulamadı";
    }

    [RelayCommand]
    private void Disconnect()
    {
        _reconnectCts?.Cancel();
        _transport.Disconnect();
        IsConnected   = false;
        ConnectStatus = "Bağlı değil";
    }

    private void ScheduleReconnect()
    {
        if (IsLocalMode) return; // local mode: always connected
        if (IsRp2040Mode) { ScheduleRp2040Reconnect(); return; }
        _reconnectCts?.Cancel();
        _reconnectCts = new CancellationTokenSource();
        var ct   = _reconnectCts.Token;
        var host = PhoneHost;
        var port = PhonePort;
        _ = Task.Run(async () =>
        {
            for (int attempt = 1; attempt <= 12 && !ct.IsCancellationRequested; attempt++)
            {
                try { await Task.Delay(5000, ct); } catch (OperationCanceledException) { return; }
                if (ct.IsCancellationRequested) return;

                var ok = await _transport.ConnectAsync(host, port, ct);
                if (ok)
                {
                    Application.Current.Dispatcher.BeginInvoke(() =>
                    {
                        IsConnected   = true;
                        ConnectStatus = $"Bağlı: {host}:{port}";
                        LogMessage    = "Otomatik yeniden bağlandı!";
                    });
                    return;
                }
            }
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                ConnectStatus = "Bağlantı kesildi";
                LogMessage    = "Otomatik yeniden bağlantı başarısız — manuel Bağlan'a bas";
            });
        }, ct);
    }

    // RP2040 USB reset sonrası otomatik yeniden bağlantı döngüsü.
    // Cihaz 1-2 saniye içinde yeniden numaralandığından 1500ms retry yeterli.
    private void ScheduleRp2040Reconnect()
    {
        _reconnectCts?.Cancel();
        _reconnectCts = new CancellationTokenSource();
        var ct = _reconnectCts.Token;
        _ = Task.Run(async () =>
        {
            for (int attempt = 1; attempt <= 120 && !ct.IsCancellationRequested; attempt++)
            {
                try { await Task.Delay(1500, ct); } catch (OperationCanceledException) { return; }
                if (ct.IsCancellationRequested) return;

                bool ok;
                try { ok = await _router.ConnectAsync("", 0, ct); }
                catch { ok = false; }

                if (ok)
                {
                    Application.Current.Dispatcher.BeginInvoke(() =>
                    {
                        IsConnected   = true;
                        ConnectStatus = "RP2040 — bağlı";
                        LogMessage    = "RP2040 yeniden bağlandı.";
                        _pingEma = 0; PingMs = 0; PushAdaptiveSettings();
                    });
                    return;
                }
                if (attempt % 5 == 0)
                    Application.Current.Dispatcher.BeginInvoke(() =>
                        ConnectStatus = $"RP2040 aranıyor… (#{attempt})");
            }
            Application.Current.Dispatcher.BeginInvoke(() =>
                ConnectStatus = "RP2040 bulunamadı — kabloyu kontrol edin");
        }, ct);
    }

    [RelayCommand]
    private async Task ToggleConnectionModeAsync()
    {
        _reconnectCts?.Cancel();

        if (!IsLocalMode && !IsUsbMode && !IsRp2040Mode)
        {
            // WiFi → USB
            _savedWifiHost = PhoneHost;
            IsUsbMode      = true;
            PhoneHost      = _state.UsbHost;
            _router.SwitchToNet(TransportMode.Usb);
        }
        else if (IsUsbMode)
        {
            // USB → Local
            _state.UsbHost = PhoneHost;
            IsUsbMode      = false;
            IsLocalMode    = true;
            _router.SwitchToLocal();
            var ok = await _router.ConnectAsync("", 0);
            IsConnected    = ok;
            ConnectStatus  = ok ? "Local — PC" : "Local bağlantı hatası";
            if (ok)
            {
                _pingEma = 0;
                PingMs   = 0;
                PushAdaptiveSettings();
            }
        }
        else if (IsLocalMode)
        {
            // Local → RP2040 (USB-HID köprüsü)
            IsLocalMode    = false;
            IsRp2040Mode   = true;
            _router.SwitchToRp2040();
            ConnectStatus  = "RP2040 aranıyor…";
            var ok = await _router.ConnectAsync("", 0);
            IsConnected    = ok;
            ConnectStatus  = ok ? "RP2040 — bağlı" : "RP2040 aranıyor…";
            if (ok) { _pingEma = 0; PingMs = 0; PushAdaptiveSettings(); }
            else    { ScheduleRp2040Reconnect(); }   // takılı değilse arka planda ara
        }
        else
        {
            // RP2040 → WiFi
            _router.SwitchToNet(TransportMode.Wifi);
            IsRp2040Mode  = false;
            IsConnected   = false;
            ConnectStatus = "Bağlı değil";
            PhoneHost     = _savedWifiHost;
        }
        SaveState();
    }

    // ── Faz 9: app-içi firmware güncelleme (BOOTSEL + uf2 kopyala) ──────────
    [RelayCommand]
    private async Task UpdateRp2040FirmwareAsync()
    {
        if (!IsRp2040Mode) { LogMessage = "Firmware güncelleme yalnız RP2040 modunda."; return; }

        var uf2 = Path.Combine(AppContext.BaseDirectory, "rp2040_bridge.uf2");
        if (!File.Exists(uf2)) { LogMessage = $"uf2 bulunamadı: {uf2}"; return; }

        ConnectStatus = "RP2040 BOOTSEL'e geçiyor…";
        await _router.RebootRp2040ToBootloaderAsync();
        _transport.Disconnect();
        IsConnected = false;

        // RPI-RP2 mass-storage sürücüsünü bekle (~20 sn)
        string? drive = null;
        for (int i = 0; i < 40 && drive is null; i++)
        {
            await Task.Delay(500);
            drive = FindRpiRp2Drive();
        }
        if (drive is null)
        {
            ConnectStatus = "RPI-RP2 çıkmadı";
            LogMessage    = "Firmware güncelleme: BOOTSEL sürücüsü bulunamadı (BOOT ile manuel dene).";
            return;
        }

        try
        {
            ConnectStatus = "Firmware yazılıyor…";
            File.Copy(uf2, Path.Combine(drive, "rp2040_bridge.uf2"), true);
        }
        catch (IOException) { /* cihaz kopyalama biterken reset olur — beklenen */ }

        ConnectStatus = "Firmware yüklendi — cihaz yeniden başlıyor…";
        LogMessage    = "RP2040 firmware güncellendi.";
        await Task.Delay(3000);    // reboot + re-enumerate
        ConnectRp2040Async();       // otomatik yeniden bağlan
    }

    private static string? FindRpiRp2Drive()
    {
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.IsReady && d.DriveType == DriveType.Removable && d.VolumeLabel == "RPI-RP2")
                    return d.RootDirectory.FullName;
            }
            catch { /* hazır olmayan sürücü */ }
        }
        return null;
    }
}
