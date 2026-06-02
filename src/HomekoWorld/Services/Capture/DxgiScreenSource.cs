using System.Drawing;
using System.Drawing.Imaging;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using D3D11 = Vortice.Direct3D11.D3D11;

namespace HomekoWorld.Services.Capture;

/// <summary>
/// DXGI Desktop Duplication ile GPU-hızlandırmalı tam ekran yakalama (~1-2ms vs GDI ~15-30ms).
/// Birincil çıkışı ve onun adapter'ını seçer (Optimus/hibrit GPU dizüstülerde — Intel iGPU + RTX —
/// doğru cihaz şart). Bir staging texture + tek Bitmap yeniden kullanılır (kare başına alloc yok).
/// Ctor başarısız olursa fırlatır → FarmEngine.CreateScreenSource GDI'ye düşer.
/// TEK thread'den (DetectionLoop) kullanılır.
/// </summary>
public sealed class DxgiScreenSource : IScreenSource
{
    private readonly ID3D11Device         _device;
    private readonly ID3D11DeviceContext  _context;
    private readonly IDXGIOutput1         _output1;
    private          IDXGIOutputDuplication _dupl;
    private readonly ID3D11Texture2D      _staging;
    private readonly Bitmap               _buffer;
    private readonly int _w, _h;
    private bool _needReinit;

    public string BackendName => "DXGI";

    public DxgiScreenSource()
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

        // ── Birincil çıkışı + onun adapter'ını bul (DesktopCoordinates top-left = 0,0) ──
        IDXGIAdapter1? primAdapter = null;
        IDXGIOutput?   primOutput  = null;

        for (uint ai = 0; factory.EnumAdapters1(ai, out var adapter).Success && adapter is not null; ai++)
        {
            bool keepAdapter = false;
            for (uint oi = 0; adapter.EnumOutputs(oi, out var output).Success && output is not null; oi++)
            {
                var od = output.Description;
                if (od.AttachedToDesktop &&
                    od.DesktopCoordinates.Left == 0 && od.DesktopCoordinates.Top == 0)
                {
                    primAdapter = adapter;
                    primOutput  = output;
                    keepAdapter = true;
                    break;
                }
                output.Dispose();
            }
            if (keepAdapter) break;
            adapter.Dispose();
        }

        if (primAdapter is null || primOutput is null)
            throw new InvalidOperationException("Birincil DXGI çıkışı bulunamadı.");

        try
        {
            // ── Device, birincil çıkışın adapter'ında (adapter verilince DriverType=Unknown şart) ──
            var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
            D3D11.D3D11CreateDevice(
                primAdapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, levels,
                out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
            _device  = device;
            _context = context;

            var coords = primOutput.Description.DesktopCoordinates;
            _w = coords.Right - coords.Left;
            _h = coords.Bottom - coords.Top;

            _output1 = primOutput.QueryInterface<IDXGIOutput1>();
            _dupl    = _output1.DuplicateOutput(_device);

            _staging = CreateStaging(_w, _h);
            _buffer  = new Bitmap(_w, _h, PixelFormat.Format32bppArgb);
        }
        finally
        {
            primOutput.Dispose();
            primAdapter.Dispose();
        }
    }

    private ID3D11Texture2D CreateStaging(int w, int h) =>
        _device.CreateTexture2D(new Texture2DDescription
        {
            Width             = (uint)w,
            Height            = (uint)h,
            MipLevels         = 1,
            ArraySize         = 1,
            Format            = Format.B8G8R8A8_UNorm,   // SDR masaüstü; BGRA = Format32bppArgb byte sırası
            SampleDescription = new SampleDescription(1, 0),
            Usage             = ResourceUsage.Staging,
            BindFlags         = BindFlags.None,
            CPUAccessFlags    = CpuAccessFlags.Read,
            MiscFlags         = ResourceOptionFlags.None,
        });

    public Bitmap Capture()
    {
        if (_needReinit) Reinit();

        Result r = _dupl.AcquireNextFrame(100, out OutduplFrameInfo _, out var desktop);

        if (r == Vortice.DXGI.ResultCode.WaitTimeout)
            return _buffer;                       // yeni kare yok → son kareyi ver
        if (r == Vortice.DXGI.ResultCode.AccessLost)
        {
            _needReinit = true;                   // mod değişimi/UAC → bir sonraki çağrıda yeniden kur
            return _buffer;
        }
        r.CheckError();

        if (desktop is null) { SafeReleaseFrame(); return _buffer; }

        try
        {
            using (desktop)
            using (var tex = desktop.QueryInterface<ID3D11Texture2D>())
                _context.CopyResource(_staging, tex);
        }
        finally { SafeReleaseFrame(); }

        CopyStagingToBuffer();
        return _buffer;
    }

    private void CopyStagingToBuffer()
    {
        MappedSubresource map = _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        var data = _buffer.LockBits(new Rectangle(0, 0, _w, _h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            int dstStride = data.Stride;
            int srcStride = (int)map.RowPitch;
            int rowBytes  = _w * 4;
            unsafe
            {
                byte* src = (byte*)map.DataPointer;
                byte* dst = (byte*)data.Scan0;
                for (int y = 0; y < _h; y++)
                    Buffer.MemoryCopy(src + (long)y * srcStride, dst + (long)y * dstStride, rowBytes, rowBytes);
            }
        }
        finally
        {
            _buffer.UnlockBits(data);
            _context.Unmap(_staging, 0);
        }
    }

    private void SafeReleaseFrame() { try { _dupl.ReleaseFrame(); } catch { } }

    private void Reinit()
    {
        _needReinit = false;
        try { _dupl.Dispose(); } catch { }
        _dupl = _output1.DuplicateOutput(_device); // AccessLost sonrası yeni duplication
    }

    public void Dispose()
    {
        SafeReleaseFrame();
        _dupl.Dispose();
        _staging.Dispose();
        _output1.Dispose();
        _context.Dispose();
        _device.Dispose();
        _buffer.Dispose();
    }
}
