using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using HomekoWorld.Models.Farm;
using HomekoWorld.Services.Yolo;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace HomekoWorld.Services.Vision;

/// <summary>
/// HP bar varlık/yokluk binary classifier (ONNX).
/// Model: 2 sınıf softmax; çıktı[0] = bar_visible olasılığı.
/// Giriş: 96×32 RGB, normalize edilmiş [0,1].
/// Model yoksa IsLoaded = false; FarmEngine fallback olarak renk taramayı kullanır.
/// </summary>
public sealed class HpBarPresenceClassifier : IDisposable
{
    private InferenceSession? _session;
    private const int W = 96, H = 32;

    public bool IsLoaded => _session is not null;

    public void Load(string onnxPath, InferenceBackend backend = InferenceBackend.Auto)
    {
        _session?.Dispose();
        _session = null;
        if (!System.IO.File.Exists(onnxPath)) return;
        try
        {
            // T4: EP seçimi tek noktadan (Auto = GPU varsa DirectML, yoksa CPU).
            var opts = OrtEpFactory.Create(backend, out _);
            _session = new InferenceSession(onnxPath, opts);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HpBarClassifier] Yüklenemedi: {ex.Message}");
        }
    }

    /// <summary>
    /// ROI bitmap'i 96×32'ye ölçekler, normalize eder ve modeli çalıştırır.
    /// Döner: bar_visible probability (0.0 – 1.0). Model yoksa -1f.
    /// </summary>
    public float Predict(Bitmap roi)
    {
        if (_session is null) return -1f;
        try
        {
            using var scaled = new Bitmap(W, H, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(scaled))
            {
                g.InterpolationMode = InterpolationMode.Bilinear;
                g.DrawImage(roi, 0, 0, W, H);
            }

            var tensor = new DenseTensor<float>(new[] { 1, 3, H, W });
            var rect   = new Rectangle(0, 0, W, H);
            var data   = scaled.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var buf    = new byte[W * H * 4];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buf, 0, buf.Length);
            scaled.UnlockBits(data);

            // BGRA → CHW RGB, Normalize([0.5,0.5,0.5],[0.5,0.5,0.5]) → [-1,1]
            // Eğitim: transforms.Normalize([0.5]*3,[0.5]*3) = (x/255 - 0.5) / 0.5
            for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = (y * W + x) * 4;
                tensor[0, 0, y, x] = (buf[i + 2] / 255f - 0.5f) / 0.5f; // R
                tensor[0, 1, y, x] = (buf[i + 1] / 255f - 0.5f) / 0.5f; // G
                tensor[0, 2, y, x] = (buf[i + 0] / 255f - 0.5f) / 0.5f; // B
            }

            var inputs  = new[] { NamedOnnxValue.CreateFromTensor("input", tensor) };
            using var results = _session.Run(inputs);
            var output = results.First().AsTensor<float>();

            // class_to_idx: bar_absent=0, bar_visible=1
            // Gerçek softmax ile P(bar_visible) = exp(v1) / (exp(v0) + exp(v1))
            float v0 = output[0, 0], v1 = output[0, 1];
            float e0 = MathF.Exp(v0 - MathF.Max(v0, v1)); // overflow önleme
            float e1 = MathF.Exp(v1 - MathF.Max(v0, v1));
            return e1 / (e0 + e1);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HpBarClassifier] Predict hata: {ex.Message}");
            return -1f;
        }
    }

    public void Dispose() => _session?.Dispose();
}
