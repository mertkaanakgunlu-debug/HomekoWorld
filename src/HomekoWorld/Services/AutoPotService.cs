using HomekoWorld.Hardware;
using HomekoWorld.Models;
using HomekoWorld.Services.Capture;
using HomekoWorld.Services.Farm;

namespace HomekoWorld.Services;

/// <summary>
/// Global oto-pot servisi. F12 (Active) açıkken bağımsız döngü çalıştırır.
/// FarmEngine'dan ayrıdır; combo veya farm çalışırken de paralel pot basar.
/// </summary>
public sealed class AutoPotService
{
    private readonly IKeyDeviceTransport _transport;
    private readonly AppState            _state;

    private CancellationTokenSource? _cts;
    private DateTime _lastHpTap = DateTime.MinValue;
    private DateTime _lastMpTap = DateTime.MinValue;

    public AutoPotService(IKeyDeviceTransport transport, AppState state)
    {
        _transport = transport;
        _state     = state;
    }

    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        Task.Run(() => LoopAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_state.AutoPot.TickMs, ct);
                if (!_state.AutoPot.Enabled) continue;

                using var frame = ScreenCapture.CaptureScreen();
                var farmSettings = _state.Farm;

                if (_state.AutoPot.HpPotEnabled)
                {
                    var hp = StatBarReader.ReadHpPercent(frame, farmSettings);
                    var threshold = _state.AutoPot.HpPotPercent / 100f;
                    if (hp < threshold && hp > 0f) // 0f = kalibre edilmemiş
                    {
                        var now = DateTime.UtcNow;
                        if ((now - _lastHpTap).TotalMilliseconds >= _state.AutoPot.CooldownMs)
                        {
                            _lastHpTap = now;
                            await TapKeyAsync(_state.AutoPot.HpPotKey, ct);
                        }
                    }
                }

                if (_state.AutoPot.MpPotEnabled)
                {
                    var mp = StatBarReader.ReadMpPercent(frame, farmSettings);
                    var threshold = _state.AutoPot.MpPotPercent / 100f;
                    if (mp < threshold && mp > 0f)
                    {
                        var now = DateTime.UtcNow;
                        if ((now - _lastMpTap).TotalMilliseconds >= _state.AutoPot.CooldownMs)
                        {
                            _lastMpTap = now;
                            await TapKeyAsync(_state.AutoPot.MpPotKey, ct);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch { /* ekran yakalama veya transport hatası — devam et */ }
        }
    }

    private async Task TapKeyAsync(string key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        await _transport.KeyDownAsync(key, ct);
        await Task.Delay(50, ct);
        await _transport.KeyUpAsync(key, ct);
    }
}
