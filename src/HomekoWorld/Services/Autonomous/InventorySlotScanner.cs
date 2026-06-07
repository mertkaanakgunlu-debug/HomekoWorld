namespace HomekoWorld.Services.Autonomous;

/// <summary>
/// Faz 39 — Envanter yuvası doluluk tespiti (paylaşılan tek karar kaynağı).
/// InventoryReader (doluluk oranı) ve MerchantTrader (dolu yuva merkezleri)
/// aynı mantığı kullanır; ikisi ayrışmasın diye burada toplandı.
///
/// Eski yöntem yalnızca ortalama HSV doygunluğuna bakıyordu ve KARANLIK boş
/// yuvalarda hatalıydı: koyu piksellerde doygunluk = (max-min)/max, küçük mutlak
/// kanal farklarıyla bile büyük çıkar (örn. RGB 12,16,24 → 0.5). Böylece boş
/// yuvalar "dolu" sanılıp oran %100'e fırlıyordu. Düzeltme: önce PARLAKLIK kapısı
/// (koyu piksel = boş zemin, elenir), sonra renk/parlaklık ile "içerik" pikseli
/// say; yuvada yeterli içerik pikseli varsa dolu kabul et.
/// </summary>
internal static class InventorySlotScanner
{
    /// <summary>İçerik sayılması için min parlaklık (V=max kanal). Koyu boş zemini eler — asıl düzeltme.</summary>
    private const float ValueMin     = 0.30f;
    /// <summary>İçerik için renklilik eşiği (yüksek V'de stabil; ikon pikselleri).</summary>
    private const float SatMin       = 0.18f;
    /// <summary>Renksiz ama parlak (beyaz miktar yazısı, metal eşya) pikselleri de içerik say.</summary>
    private const float BrightMin    = 0.60f;
    /// <summary>Yuvanın dolu sayılması için min içerik piksel oranı (boş ≈ %0, dolu ikon &gt; %40).</summary>
    public  const float FillCoverage = 0.15f;

    /// <summary>
    /// Yuvanın iç bölgesindeki (kenar payı çağıran tarafça verilmiş) "içerik" (parlak/renkli)
    /// piksel oranını [0,1] döndürür. <paramref name="scan0"/> kilitli BGRA (Format32bppArgb)
    /// tampon başlangıcı olmalı; bitmap çağrı boyunca kilitli kalır.
    /// </summary>
    public static unsafe float SlotFillFraction(byte* scan0, int stride, int x0, int y0, int x1, int y1)
    {
        int content = 0, count = 0;
        for (int y = y0; y <= y1; y += 3)
        {
            byte* row = scan0 + y * stride;
            for (int x = x0; x <= x1; x += 3)
            {
                byte* px = row + x * 4; // BGRA
                float b = px[0] / 255f, g = px[1] / 255f, r = px[2] / 255f;
                float mx = MathF.Max(r, MathF.Max(g, b));
                count++;
                if (mx < ValueMin) continue;              // koyu → boş zemin (elendi)
                float mn  = MathF.Min(r, MathF.Min(g, b));
                float sat = (mx - mn) / mx;               // V >= ValueMin olduğundan stabil
                if (sat >= SatMin || mx >= BrightMin) content++;
            }
        }
        return count > 0 ? (float)content / count : 0f;
    }

    /// <summary>Yuva içerik oranı <see cref="FillCoverage"/> eşiğini aşıyorsa dolu.</summary>
    public static unsafe bool IsSlotFilled(byte* scan0, int stride, int x0, int y0, int x1, int y1)
        => SlotFillFraction(scan0, stride, x0, y0, x1, y1) >= FillCoverage;
}
