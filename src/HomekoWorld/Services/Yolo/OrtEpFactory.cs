using HomekoWorld.Models.Farm;
using Microsoft.ML.OnnxRuntime;
using Vortice.DXGI;

namespace HomekoWorld.Services.Yolo;

/// <summary>
/// ONNX Runtime execution provider (EP) seçimini tek noktadan yapar (T4).
///
/// • Evrensel build (varsayılan): <b>DirectML → CPU</b>. DirectML her DX12 GPU'da (NVIDIA/AMD/Intel)
///   sıfır ek bağımlılıkla çalışır → çok-cihaz dağıtımı için doğru taban.
/// • NVIDIA performans build'i (<c>HOMEKO_CUDA</c> derleme sabiti, ayrı csproj configuration):
///   <b>CUDA → CPU</b>. Ağır native bağımlılık (CUDA/cuDNN) gerektirir. (TensorRT şu an proje DIŞI —
///   ileride ayrı sidecar engine-builder + fallback olarak gelecek; planlanan Faz E.)
///
/// <see cref="InferenceBackend.Auto"/>: birincil donanım GPU'su var mı diye DXGI ile bakar
/// (T2'de eklenen Vortice yeniden kullanılır) → varsa GPU EP, yoksa CPU.
/// </summary>
public static class OrtEpFactory
{
    /// <summary>
    /// Birincil donanım GPU'sunu DXGI ile tespit eder (Microsoft Basic Render / WARP yazılım adaptörü hariç).
    /// Döner: (var mı, adı, vendorId). NVIDIA=0x10DE, AMD=0x1002, Intel=0x8086.
    /// </summary>
    public static (bool hasGpu, string name, uint vendorId) DetectPrimaryGpu()
    {
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            (bool hasGpu, string name, uint vendorId) bestGpu = (false, "", 0);

            for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success && adapter is not null; i++)
            {
                AdapterDescription1 desc = adapter.Description1;
                adapter.Dispose();
                
                if ((desc.Flags & AdapterFlags.Software) != 0) continue; // yazılım adaptörü → atla
                
                string name = string.IsNullOrWhiteSpace(desc.Description) ? "GPU" : desc.Description;
                uint vendor = (uint)desc.VendorId;

                // NVIDIA (0x10DE) veya AMD (0x1002) bulursak direkt onu seç ve dön (harici GPU önceliği)
                if (vendor == 0x10DE || vendor == 0x1002)
                {
                    return (true, name, vendor);
                }

                // Aksi takdirde (Intel vs.) ilk bulduğumuzu yedekte tutalım
                if (!bestGpu.hasGpu)
                {
                    bestGpu = (true, name, vendor);
                }
            }

            return bestGpu;
        }
        catch { /* DXGI yoksa/başarısızsa GPU yok say */ }
        return (false, "", 0);
    }

    /// <summary>
    /// Verilen tercihe göre <see cref="SessionOptions"/> üretir; gerçekte kurulan EP adını <paramref name="epUsed"/>'a yazar.
    /// Hiçbir koşulda fırlatmaz (GPU EP eklenemezse CPU'ya düşer).
    /// </summary>
    public static SessionOptions Create(InferenceBackend backend, out string epUsed)
    {
        var opts = new SessionOptions 
        { 
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_EXTENDED,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            EnableMemoryPattern = false
        };
        epUsed = "CPU";

#if HOMEKO_CUDA
        // ── NVIDIA performans build'i: CUDA → CPU (TensorRT ertelendi — planlanan Faz E sidecar) ──
        if (backend != InferenceBackend.Cpu)
        {
            try { opts.AppendExecutionProvider_CUDA(0); epUsed = "CUDA"; return opts; }
            catch (Exception ex) { HomekoWorld.Program.Log($"[EP] CUDA yok: {ex.Message}"); }
        }
#else
        // ── Evrensel build: DirectML → CPU ──
        bool wantGpu = backend switch
        {
            InferenceBackend.Cpu      => false,
            InferenceBackend.DirectML => true,
            _                         => DetectPrimaryGpu().hasGpu,   // Auto
        };
        if (wantGpu)
        {
            try { opts.AppendExecutionProvider_DML(0); epUsed = "DirectML"; return opts; }
            catch (Exception ex) { HomekoWorld.Program.Log($"[EP] DirectML başlatılamadı, CPU'ya düşülüyor: {ex.Message}"); }
        }
#endif

        epUsed = "CPU";
        return opts;
    }
}
