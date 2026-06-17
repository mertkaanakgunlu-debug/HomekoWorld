using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace HomekoWorld.Views;

public partial class CudaDownloadWindow : Window
{
    private readonly string[] DownloadUrls = new[]
    {
        "https://github.com/mertkaanakgunlu-debug/HomekoWorld/releases/download/cuda-libs/cuda_core.zip",
        "https://github.com/mertkaanakgunlu-debug/HomekoWorld/releases/download/libs/cudnn_core.zip"
    };

    private const int StallTimeoutSec = 30;          // tek bir ReadAsync bu kadar ilerlemezse → bağlantı takıldı
    private const int BufferSize      = 512 * 1024;  // büyük dosyalar için iri buffer (8KB değil)

    /// <summary>İndirme + kurulum başarılı oldu mu? <see cref="DialogResult"/> yerine kullanılır:
    /// Loaded async void içinde DialogResult atamak (modal durum henüz/halen geçerli olmayabilir)
    /// InvalidOperationException fırlatıp async-void istisnası olarak uygulamayı çökertiyordu. Çağıran
    /// (App.xaml.cs) dönüş değerini kullanmıyor; gerekirse bu property okunur.</summary>
    public bool Success { get; private set; }

    private CancellationTokenSource? _cts;
    private bool _closeRequested; // kullanıcı "İptal / CPU ile devam" dedi → hata ekranı gösterme, kapat

    public CudaDownloadWindow()
    {
        InitializeComponent();
        Loaded += Window_Loaded;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await StartAsync();

    /// <summary>Bir indirme denemesi yürütür. Başarı → kapat. İptal (kullanıcı) → kapat (CPU). Hata/stall →
    /// hata mesajı + "Yeniden Dene" sun (modal açık kalır, kullanıcı tekrar dener veya CPU ile devam eder).</summary>
    private async Task StartAsync()
    {
        RetryButton.Visibility   = Visibility.Collapsed;
        DownloadProgress.IsIndeterminate = false;
        DownloadProgress.Value   = 0;

        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        bool ok;
        try
        {
            ok = await RunDownloadAsync(_cts.Token);
        }
        catch (OperationCanceledException) { ok = false; }      // kullanıcı iptali
        catch (Exception ex)
        {
            StatusText.Text = $"İndirme hatası: {ex.Message}";
            ok = false;
        }

        Success = ok;

        // Başarı VEYA kullanıcı kapatmak istedi → pencereyi kapat (App CPU/GPU ile devam eder).
        if (ok || _closeRequested) { Close(); return; }

        // Hata/stall (kullanıcı kapatmadı) → tekrar deneme imkânı sun. Pencere açık kalır.
        if (string.IsNullOrEmpty(StatusText.Text) || !StatusText.Text.Contains("hata", StringComparison.OrdinalIgnoreCase))
            StatusText.Text = "İndirme tamamlanamadı. Yeniden deneyebilir veya CPU ile devam edebilirsiniz.";
        DownloadProgress.IsIndeterminate = false;
        RetryButton.Visibility = Visibility.Visible;
    }

    private async Task<bool> RunDownloadAsync(CancellationToken ct)
    {
        string baseDir = AppContext.BaseDirectory;
        using var client = new HttpClient();
        var buffer = new byte[BufferSize];

        for (int i = 0; i < DownloadUrls.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            string url     = DownloadUrls[i];
            string tempZip = Path.Combine(baseDir, $"temp_cuda_{i}.zip");
            try
            {
                StatusText.Text = $"Dosya {i + 1}/{DownloadUrls.Length} bağlantıya geçiliyor...";

                using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    response.EnsureSuccessStatusCode();
                    long totalBytes = response.Content.Headers.ContentLength ?? -1L;

                    using var stream     = await response.Content.ReadAsStreamAsync(ct);
                    using var fileStream = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true);

                    long totalRead = 0;
                    int  lastPct   = -1;
                    while (true)
                    {
                        // Stall watchdog: bu ReadAsync StallTimeoutSec içinde ilerlemezse iptal et.
                        // (HttpClient.Timeout yalnız header'a kadar geçerli → büyük dosyada takılma sonsuza dek sürerdi.)
                        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        readCts.CancelAfter(TimeSpan.FromSeconds(StallTimeoutSec));

                        int bytesRead;
                        try
                        {
                            bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, readCts.Token);
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            // Kullanıcı değil → stall timeout. Bu denemeyi hata say (retry sunulur).
                            throw new TimeoutException($"Bağlantı {StallTimeoutSec} sn yanıt vermedi (takıldı).");
                        }

                        if (bytesRead == 0) break;
                        await fileStream.WriteAsync(buffer, 0, bytesRead, ct);
                        totalRead += bytesRead;

                        if (totalBytes > 0)
                        {
                            int pct = (int)(totalRead * 100 / totalBytes);
                            if (pct != lastPct) // her 8KB'de değil, yalnız yüzde değişince UI güncelle
                            {
                                lastPct = pct;
                                DownloadProgress.Value = pct;
                                StatusText.Text = $"Dosya {i + 1}/{DownloadUrls.Length} indiriliyor: %{pct} " +
                                                  $"({totalRead / 1024 / 1024}MB / {totalBytes / 1024 / 1024}MB)";
                            }
                        }
                        else
                        {
                            StatusText.Text = $"Dosya {i + 1}/{DownloadUrls.Length} indiriliyor: {totalRead / 1024 / 1024}MB";
                        }
                    }
                }

                StatusText.Text = $"Dosya {i + 1}/{DownloadUrls.Length} arşivden çıkarılıyor...";
                DownloadProgress.IsIndeterminate = true;
                await Task.Run(() => ExtractZipSafe(tempZip, baseDir), ct);
                DownloadProgress.IsIndeterminate = false;
            }
            finally
            {
                // temp zip her durumda (başarı/hata/iptal) silinir → kurulum dizininde artık kalmaz.
                try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { /* kilitli/silinemezse yok say */ }
            }
        }

        MessageBox.Show("NVIDIA hızlandırma dosyaları başarıyla kuruldu! Uygulama GPU modunda başlatılacak.",
                      "Kurulum Tamamlandı", MessageBoxButton.OK, MessageBoxImage.Information);
        return true;
    }

    /// <summary>Zip'i <paramref name="destRoot"/> altına güvenli çıkarır: alt-dizinleri oluşturur
    /// (eski kod oluşturmuyordu → klasörlü zip'te DirectoryNotFoundException) ve Zip-Slip (../) girişlerini
    /// reddeder (kök dışına yazmayı engeller).</summary>
    private static void ExtractZipSafe(string zipPath, string destRoot)
    {
        string fullRoot = Path.GetFullPath(destRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; // dizin girişi → atla
            string destPath = Path.GetFullPath(Path.Combine(destRoot, entry.FullName));
            if (!destPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"Güvenli olmayan arşiv girişi (kök dışı): {entry.FullName}");
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            entry.ExtractToFile(destPath, overwrite: true);
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _closeRequested = true;
        _cts?.Cancel();
        // Hata ekranındaysak (indirme beklemiyor) → doğrudan kapat. Aktif indirmede StartAsync iptali yakalayıp kapatır.
        if (RetryButton.Visibility == Visibility.Visible) Close();
    }

    private async void RetryButton_Click(object sender, RoutedEventArgs e) => await StartAsync();
}
