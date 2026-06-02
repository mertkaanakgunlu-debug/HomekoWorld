using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text.Json;
using HomekoWorld.Models;

namespace HomekoWorld.Services.Skills;

public class SkillLibrary
{
    private readonly string _assetsRoot;
    private readonly List<SkillInfo> _skills = [];

    public IReadOnlyList<SkillInfo> All => _skills;

    public SkillLibrary()
    {
        _assetsRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "Skills");
    }

    public void Load()
    {
        _skills.Clear();
        if (!Directory.Exists(_assetsRoot)) return;

        var jsonPath = Path.Combine(_assetsRoot, "skills.json");
        List<SkillInfo> infos;

        if (File.Exists(jsonPath))
        {
            try
            {
                var json = File.ReadAllText(jsonPath);
                infos = JsonSerializer.Deserialize<List<SkillInfo>>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            }
            catch { infos = []; }
        }
        else
        {
            infos = ScanAndBuildManifest(jsonPath);
        }

        foreach (var info in infos)
        {
            var fullPath = Path.Combine(_assetsRoot, info.IconPath);
            if (!File.Exists(fullPath)) continue;
            try
            {
                info.Hash = ComputePHash(fullPath);
                _skills.Add(info);
            }
            catch { /* skip unreadable icons */ }
        }
    }

    public SkillInfo? GetById(string id) =>
        _skills.FirstOrDefault(s => s.Id == id);

    public IEnumerable<SkillInfo> ByClass(string classId) =>
        _skills.Where(s => string.IsNullOrEmpty(classId) || s.Class == classId);

    // Scan PNG files and auto-generate skills.json manifest
    private List<SkillInfo> ScanAndBuildManifest(string jsonPath)
    {
        var infos = new List<SkillInfo>();
        foreach (var dir in Directory.EnumerateDirectories(_assetsRoot))
        {
            var className = Path.GetFileName(dir).ToLowerInvariant();
            foreach (var png in Directory.EnumerateFiles(dir, "*.png"))
            {
                var fileName = Path.GetFileNameWithoutExtension(png);
                var id = $"{className}.{fileName}";
                var displayName = ToDisplayName(fileName);
                infos.Add(new SkillInfo
                {
                    Id       = id,
                    Class    = className,
                    NameTr   = displayName,
                    NameEn   = displayName,
                    IconPath = Path.GetRelativePath(_assetsRoot, png),
                });
            }
        }

        // Persist generated manifest so next launch is instant
        try
        {
            var json = JsonSerializer.Serialize(infos,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(jsonPath, json);
        }
        catch { /* non-critical */ }

        return infos;
    }

    // Perceptual hash (pHash): 64-bit fingerprint robust to minor color/size changes
    public static ulong ComputePHash(string imagePath)
    {
        using var original = new Bitmap(imagePath);
        return ComputePHashFromBitmap(original);
    }

    public static ulong ComputePHashFromBitmap(Bitmap original)
    {
        const int size = 32;

        using var resized = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(resized))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(original, 0, 0, size, size);
        }

        // Convert to grayscale luminance matrix
        var pixels = new double[size, size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                var c = resized.GetPixel(x, y);
                pixels[y, x] = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
            }

        // Discrete Cosine Transform — apply to rows then columns
        var dct = ApplyDct2D(pixels, size);

        // Take top-left 8×8 block (excluding [0,0] DC component)
        double sum = 0;
        int count  = 0;
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
                if (x != 0 || y != 0) { sum += dct[y, x]; count++; }
        double mean = sum / count;

        // Build 64-bit hash: 1 if coefficient > mean
        ulong hash  = 0;
        int   bit   = 0;
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                if (dct[y, x] > mean) hash |= 1UL << bit;
                bit++;
            }
        return hash;
    }

    // Hamming distance between two pHash values (lower = more similar)
    public static int HammingDistance(ulong a, ulong b)
    {
        ulong diff = a ^ b;
        int   dist = 0;
        while (diff != 0) { dist += (int)(diff & 1); diff >>= 1; }
        return dist;
    }

    private static double[,] ApplyDct2D(double[,] input, int n)
    {
        // 1D DCT on rows
        var tmp = new double[n, n];
        for (int y = 0; y < n; y++)
        {
            var row = new double[n];
            for (int x = 0; x < n; x++) row[x] = input[y, x];
            var dctRow = Dct1D(row, n);
            for (int x = 0; x < n; x++) tmp[y, x] = dctRow[x];
        }

        // 1D DCT on columns
        var result = new double[n, n];
        for (int x = 0; x < n; x++)
        {
            var col = new double[n];
            for (int y = 0; y < n; y++) col[y] = tmp[y, x];
            var dctCol = Dct1D(col, n);
            for (int y = 0; y < n; y++) result[y, x] = dctCol[y];
        }
        return result;
    }

    private static double[] Dct1D(double[] input, int n)
    {
        var output = new double[n];
        for (int k = 0; k < n; k++)
        {
            double sum = 0;
            for (int i = 0; i < n; i++)
                sum += input[i] * Math.Cos(Math.PI * k * (2 * i + 1) / (2.0 * n));
            output[k] = sum * (k == 0 ? Math.Sqrt(1.0 / n) : Math.Sqrt(2.0 / n));
        }
        return output;
    }

    private static string ToDisplayName(string fileName) =>
        string.Join(" ", fileName
            .Replace('_', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
}
