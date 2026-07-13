using HomekoWorld.Models.Farm;
using Microsoft.ML.OnnxRuntime;
using Vortice.DXGI;

namespace HomekoWorld.Services.Yolo;

/// <summary>
/// ONNX Runtime execution provider (EP) seçimini tek noktadan yapar (T4).
///
/// İki gerçek build variant'ı vardır (csproj <c>GpuVariant</c> MSBuild property'si ile seçilir;
/// CUDA ve DirectML aynı native onnxruntime.dll'i sağladığından ASLA birlikte paketlenemez):
/// • Evrensel build (varsayılan, <c>-p:GpuVariant=DirectML</c>): <b>DirectML → CPU</b>. DirectML her DX12
///   GPU'da (NVIDIA/AMD/Intel) sıfır ek bağımlılıkla çalışır → çok-cihaz dağıtımı için doğru taban.
/// • NVIDIA performans build'i (<c>-p:GpuVariant=Cuda</c> → <c>HOMEKO_CUDA</c> sabiti): <b>CUDA → CPU</b>.
///   Ağır native bağımlılık (CUDA/cuDNN) gerektirir; cuBLAS/cuDNN app içinde indirilir. (TensorRT proje DIŞI —
///   ileride ayrı sidecar engine-builder olarak gelecek; planlanan Faz E.)
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
    /// <param name="preferNhwc">14.tur (Faz 8, DENEYSEL): true → CUDA EP'ye prefer_nhwc=1 geçirilir.
    /// ORT 1.20+ convolution ağırlıklı modellerde tensor-core dostu NHWC yolunu tercih edebilir AMA ek
    /// transpose operatörleri oluşursa tersine de dönebilir. VARSAYILAN false (mevcut davranış birebir
    /// korunur) — bu ayar yalnız ÖLÇÜLMÜŞ bir A/B kıyası (GPU run p50/p95 + kutu eşleşmesi, aynı replay
    /// üzerinde) sonrası kalıcı açılmalı. Bu turda ölçüm aracı (replay_benchmark.py, tools/yolo_trainer/
    /// altında planlanmıştı) henüz YAZILMADI — gerçek A/B bu commit'e dahil DEĞİL, yalnız deneye izin
    /// veren anahtar eklendi (bkz HANDOFF Faz E notu).</param>
    public static SessionOptions Create(InferenceBackend backend, out string epUsed, bool preferNhwc = false)
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
            try
            {
                // P3: çıplak AppendExecutionProvider_CUDA(0) yerine AYARLI sağlayıcı. Sabit 640×640 girdide
                // model şekli değişmediğinden en hızlı cuDNN conv algoritmasını bir kez arayıp sabitlemek
                // (EXHAUSTIVE) + max workspace + varsayılan stream'de kopya → GPU Run süresini kısaltır.
                // EXHAUSTIVE'in tek-seferlik arama maliyeti WarmUpAsync (idle) tarafından soğurulur.
                // NOT (2026-07-10): arena_extend_strategy=kSameAsRequested KALDIRILDI — headless A/B'de
                // (RTX 4070 Laptop, ORT 1.20.1) kazancı yok (p50 8.7 vs 8.4ms, gürültü içinde) ve
                // EnableMemoryPattern=false ile birlikte oyun-dolu VRAM'de arena parçalanması/tekrarlanan
                // cudaMalloc riski taşır. Varsayılan kNextPowerOfTwo kalsın.
                using var cudaOpts = new OrtCUDAProviderOptions();
                cudaOpts.UpdateOptions(new Dictionary<string, string>
                {
                    ["device_id"]                    = "0",
                    ["cudnn_conv_algo_search"]       = "EXHAUSTIVE",     // sabit şekil → en hızlı conv (warmup soğurur)
                    ["cudnn_conv_use_max_workspace"] = "1",             // cuDNN daha hızlı algoritma seçebilsin
                    ["do_copy_in_default_stream"]    = "1",             // H2D/D2H kopyaları compute stream'inde → senkron
                    ["prefer_nhwc"]                  = preferNhwc ? "1" : "0", // 14.tur (Faz 8, deneysel — ölçülmeden açılmadı)
                });
                opts.AppendExecutionProvider_CUDA(cudaOpts); // ayarlar append'te kopyalanır → cudaOpts dispose edilebilir
                epUsed = "CUDA";
                return opts;
            }
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
