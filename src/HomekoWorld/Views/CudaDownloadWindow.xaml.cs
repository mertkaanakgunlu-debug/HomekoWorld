using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;

namespace HomekoWorld.Views;

public partial class CudaDownloadWindow : Window
{
    private readonly string[] DownloadUrls = new[] 
    {
        "https://github.com/mertkaanakgunlu-debug/HomekoWorld/releases/download/libs/cuda_core.zip",
        "https://github.com/mertkaanakgunlu-debug/HomekoWorld/releases/download/libs/cudnn_core.zip"
    };
    
    public CudaDownloadWindow()
    {
        InitializeComponent();
        Loaded += Window_Loaded;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            string baseDir = AppContext.BaseDirectory;
            using var client = new HttpClient();
            
            for (int i = 0; i < DownloadUrls.Length; i++)
            {
                string url = DownloadUrls[i];
                StatusText.Text = $"Dosya {i + 1}/{DownloadUrls.Length} bağlantıya geçiliyor...";
                
                string tempZip = Path.Combine(baseDir, $"temp_cuda_{i}.zip");
                
                using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();
                    
                    var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                    using var stream = await response.Content.ReadAsStreamAsync();
                    using var fileStream = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);
                    
                    var buffer = new byte[8192];
                    long totalRead = 0;
                    int bytesRead;
                    
                    while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await fileStream.WriteAsync(buffer, 0, bytesRead);
                        totalRead += bytesRead;
                        
                        if (totalBytes != -1)
                        {
                            int progress = (int)((totalRead * 100) / totalBytes);
                            Dispatcher.Invoke(() => 
                            {
                                DownloadProgress.Value = progress;
                                StatusText.Text = $"Dosya {i + 1}/{DownloadUrls.Length} indiriliyor: %{progress} ({(totalRead / 1024 / 1024)}MB / {(totalBytes / 1024 / 1024)}MB)";
                            });
                        }
                    }
                }
                
                StatusText.Text = $"Dosya {i + 1}/{DownloadUrls.Length} arşivden çıkarılıyor...";
                DownloadProgress.IsIndeterminate = true;
                
                await Task.Run(() => 
                {
                    using var archive = ZipFile.OpenRead(tempZip);
                    foreach (var entry in archive.Entries)
                    {
                        string destPath = Path.Combine(baseDir, entry.FullName);
                        if (!string.IsNullOrEmpty(entry.Name)) // ignore directories
                        {
                            entry.ExtractToFile(destPath, overwrite: true);
                        }
                    }
                });
                
                DownloadProgress.IsIndeterminate = false;
                File.Delete(tempZip);
            }
            
            MessageBox.Show("NVIDIA hızlandırma dosyaları başarıyla kuruldu! Uygulama GPU modunda başlatılacak.", 
                          "Kurulum Tamamlandı", MessageBoxButton.OK, MessageBoxImage.Information);
            
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"İndirme sırasında bir hata oluştu:\n{ex.Message}\n\nUygulama CPU modunda devam edecek.", 
                          "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            DialogResult = false;
        }
        finally
        {
            Close();
        }
    }
}
