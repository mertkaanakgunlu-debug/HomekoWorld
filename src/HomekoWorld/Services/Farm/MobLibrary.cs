using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using HomekoWorld.Models.Farm;

namespace HomekoWorld.Services.Farm;

/// <summary>
/// mobs.json dosyasından mob tanımlarını yükler ve yönetir.
/// tools/yolo_trainer/out/mobs.json → buraya kopyalanır veya path verilir.
/// </summary>
public sealed class MobLibrary
{
    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <summary>Yüklü mob listesi (UI'da ComboBox kaynağı).</summary>
    public ObservableCollection<MobInfo> Mobs { get; } = [];

    /// <summary>Son yüklenen dosya yolu.</summary>
    public string LoadedPath { get; private set; } = "";

    /// <summary>
    /// mobs.json'ı diskten yükler. Hata olursa boş bırakır.
    /// </summary>
    public void Load(string path)
    {
        Mobs.Clear();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

        try
        {
            var json = File.ReadAllText(path);
            var list = JsonSerializer.Deserialize<List<MobInfo>>(json, _json);
            if (list is null) return;
            foreach (var m in list) Mobs.Add(m);
            LoadedPath = path;
        }
        catch
        {
            // Yükleme hatası — Mobs boş kalır, UI "Model yüklü değil" gösterir
        }
    }

    /// <summary>İsme göre mob bulur (SelectedMobName ile eşleştirir).</summary>
    public MobInfo? FindByName(string nameEn)
        => Mobs.FirstOrDefault(m =>
            string.Equals(m.NameEn, nameEn, StringComparison.OrdinalIgnoreCase));

    /// <summary>YOLO class id'sine göre mob bulur.</summary>
    public MobInfo? FindById(int classId)
        => Mobs.FirstOrDefault(m => m.Id == classId);

    /// <summary>
    /// İsim listesine karşılık gelen YOLO class id'lerini döner.
    /// Çoklu mob seçiminde kanditat filtrelemesi için kullanılır.
    /// Boş liste → tüm tespitler (filtre yok).
    /// </summary>
    public HashSet<int> GetSelectedIds(IEnumerable<string> names)
    {
        var result = new HashSet<int>();
        foreach (var n in names)
        {
            var m = FindByName(n);
            if (m is not null) result.Add(m.Id);
        }
        return result;
    }

    /// <summary>Mevcut Mobs listesini diske kaydeder.</summary>
    public void Save()
    {
        if (string.IsNullOrWhiteSpace(LoadedPath)) return;
        try
        {
            var json = JsonSerializer.Serialize(Mobs.ToList(), _json);
            File.WriteAllText(LoadedPath, json);
        }
        catch { /* Kayıt hatası sessizce yutulur */ }
    }
}
