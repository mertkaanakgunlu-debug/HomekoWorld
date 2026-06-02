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
        if (IsLocalMode) return; // button is hidden in local mode; guard defensively
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
        if (IsLocalMode) return; // local mode has no network reconnect
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

    [RelayCommand]
    private async Task ToggleConnectionModeAsync()
    {
        _reconnectCts?.Cancel();

        if (!IsLocalMode && !IsUsbMode)
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
        else
        {
            // Local → WiFi
            _router.SwitchToNet(TransportMode.Wifi);
            IsLocalMode   = false;
            IsConnected   = false;
            ConnectStatus = "Bağlı değil";
            PhoneHost     = _savedWifiHost;
        }
        SaveState();
    }
}
