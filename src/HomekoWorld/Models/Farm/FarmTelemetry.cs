namespace HomekoWorld.Models.Farm;

/// <summary>Bir farm oturumunun anlık istatistikleri.</summary>
public sealed class FarmTelemetry
{
    public int    Kills        { get; set; }
    public int    HpPotsUsed   { get; set; }
    public int    MpPotsUsed   { get; set; }
    public int    LoopFps      { get; set; }   // FarmEngine tick FPS
    public int    InferenceFps { get; set; }   // YOLO detection FPS (yalnız BAŞARILI inference sayılır)
    // ── Gecikme bütçesi (A3): saniyelik ortalama, başarılı inference başına ─────
    /// <summary>Ekran yakalama süresi (ms/kare ortalaması) — DXGI ~1-3ms, GDI ~15-30ms.</summary>
    public int    CaptureMs    { get; set; }
    /// <summary>Toplam inference süresi (preprocess+GPU+postprocess, ms/kare ort) = Prep+Gpu+Post.</summary>
    public int    InferenceMs  { get; set; }
    /// <summary>FPS-cap için beklenen süre (ms/kare ortalaması) — yüksekse GPU boşta, cap'i gevşetebilirsin.</summary>
    public int    WaitMs       { get; set; }
    // ── P0: inference alt-zamanlaması (darboğaz teşhisi) — TensorRT yalnız GpuRunMs'i hızlandırır ──
    /// <summary>Preprocess (CPU letterbox+tensor) ms/kare ort. Yüksekse darboğaz CPU → TensorRT YARDIM ETMEZ.</summary>
    public int    PreprocessMs { get; set; }
    /// <summary>GPU Run (saf inference) ms/kare ort. Yüksekse darboğaz GPU → FP16/TensorRT yardım eder.</summary>
    public int    GpuRunMs     { get; set; }
    /// <summary>Postprocess (CPU parse+NMS) ms/kare ort.</summary>
    public int    PostprocessMs { get; set; }
    /// <summary>Faz B: son tıklamada kullanılan tespitin yaşı (ms) = NowMs - CapturedAtMs. Yüksekse bayat
    /// kutuya tıklanıyor demektir (capture→inference→karar gecikmesi). Objektif "stale-box" ölçümü.</summary>
    public int    FrameAgeAtClickMs { get; set; }
    public long   SessionMs    { get; set; }   // ms since Start()
    public string CurrentMob   { get; set; } = "";
    /// <summary>Farm engine'in son gönderdiği tuş (TapKeyAsync'den güncellenir).</summary>
    public string LastKeyTapped { get; set; } = "";

    // ── Oturum-özeti sayaçları (2026-07-03 telemetri) — yalnız FarmLoop task zincirinden mutasyona
    // uğrar (ScanningTick/TargetAsync/EngageAsync/TickAsync sıralı await'ler) → kilit gerekmez. ──
    /// <summary>Angajman-sonu Lost sayısı (pencere kayboldu, hasar görülmedi).</summary>
    public int Losts          { get; set; }
    /// <summary>Angajman-sonu LostFast sayısı (hızlı-terk: iz kayıp / yapı hiç görünmedi).</summary>
    public int LostFasts      { get; set; }
    /// <summary>Angajman-sonu Timeout sayısı (safetyMs doldu).</summary>
    public int Timeouts       { get; set; }
    /// <summary>Hedef-alma (TargetAsync) deneme sayısı.</summary>
    public int AcqAttempts    { get; set; }
    /// <summary>Hedef-alma başarı sayısı (angajmana ulaşan).</summary>
    public int AcqSuccesses   { get; set; }
    /// <summary>Guardian nedeniyle reddedilen hedef sayısı.</summary>
    public int GuardianBlocks { get; set; }

    // ── 14.tur (Faz 4.1): ayrık tıklama sayaçları — dış denetim P1-8 düzeltmesi. Eski AcqAttempts/
    // AcqSuccesses "başarısız tıklamanın tersi" DEĞİLDİ: AcqAttempts tık atılmadan (taze-karede-yok,
    // kapı-vazgeç) da artıyordu; guardian-red'de tıklama+HP onayı BAŞARILIYKEN AcqSuccesses artmıyordu
    // (2026-07-13 canlı kanıt: 63 denemenin 35'i guardian-red — ham "%9" iken gerçek tık-isabeti %84
    // idi). Yeni sayaçlar "tıklama fiziksel olarak isabet etti mi" sorusunu guardian kararından AYRIK
    // tutar; AcqAttempts/AcqSuccesses geriye-dönük kıyas için KORUNUR (davranışları değişmedi). ──
    /// <summary>Gerçekten gönderilen (ClickAsync çağrılan) tıklama sayısı.</summary>
    public int ClicksIssued { get; set; }
    /// <summary>HP bar doğrulaması BAŞARILI olan tıklama sayısı — guardian kararından BAĞIMSIZ
    /// ("tıklama isabet etti mi" sorusunun saf cevabı). HUD'un ana yüzdesi bu/ClicksIssued'dır.</summary>
    public int ClicksConfirmed { get; set; }
    /// <summary>Tıklama hiç atılmadan çıkılan deneme sayısı (yalnız kamera-kapısı vazgeç — 15.tur'da
    /// taze-karede-yok artık tıklamayı iptal etmiyor, son-bilinen konuma tıklıyor) —
    /// AcqAttempts içinde ama fiilen hiç tık gitmemiş alt-küme.</summary>
    public int NoClickFreshMiss { get; set; }
    /// <summary>Kamera-hareket kapısı bütçesi dolup vazgeçilen hedef sayısı (oturum-özetindeki
    /// "kapı: vazgeç=" ile aynı kaynak — HUD'da da görünür olsun diye telemetriye taşınır).</summary>
    public int GateGiveUps { get; set; }
    /// <summary>Tıklandı ama HP doğrulanamadı sınıfı (iz-titremesi + HP-bar-kalibrasyonsuz +
    /// guardian-offset-teyitsiz) — "tık gitti, sonuç belirsiz kaldı" grubu.</summary>
    public int HpValidationFailed { get; set; }
    /// <summary>Ceset-varsayımı sayısı (2 tık HP üretmedi → muhtemel ceset/seçilemez hedef).</summary>
    public int CorpseAssumptions { get; set; }
    /// <summary>MobTracker hareket-dirilişi sayısı. 7.tur (2026-07-04): diriliş mekanizması KALDIRILDI —
    /// bu alan artık hep 0 (oturum-özeti formatı bozulmasın diye tutuluyor; 0 = mekanizma kapalı doğrulaması).</summary>
    public int Resurrections  { get; set; }
    /// <summary>Klan bankası çalışma sayısı.</summary>
    public int BankRuns       { get; set; }
    /// <summary>Klan bankasına taşınan toplam eşya.</summary>
    public int BankItemsMoved { get; set; }
    /// <summary>Kill→sonraki-angajman geçiş süreleri (ms; banka içeren geçişler hariç, 2000 kayıt sınırı).</summary>
    public System.Collections.Generic.List<int> SwitchTimesMs { get; } = new();
    /// <summary>Geçiş sürelerinin koşan ortalaması (ms). HUD bunu okur — SwitchTimesMs listesi motor
    /// thread'inde büyürken UI'nın listeye dokunması yarış olur; tek int okuma güvenli.</summary>
    public int SwitchAvgMs   { get; set; }

    /// <summary>Sıfırlar (yeni oturum başında çağır).</summary>
    public void Reset()
    {
        Kills = HpPotsUsed = MpPotsUsed = LoopFps = 0;
        InferenceFps = CaptureMs = InferenceMs = WaitMs = FrameAgeAtClickMs = 0;
        PreprocessMs = GpuRunMs = PostprocessMs = 0;
        SessionMs   = 0;
        CurrentMob  = "";
        LastKeyTapped = "";
        Losts = LostFasts = Timeouts = 0;
        AcqAttempts = AcqSuccesses = GuardianBlocks = Resurrections = 0;
        ClicksIssued = ClicksConfirmed = NoClickFreshMiss = GateGiveUps = 0;
        HpValidationFailed = CorpseAssumptions = 0;
        BankRuns = BankItemsMoved = 0;
        SwitchTimesMs.Clear();
        SwitchAvgMs = 0;
    }
}
