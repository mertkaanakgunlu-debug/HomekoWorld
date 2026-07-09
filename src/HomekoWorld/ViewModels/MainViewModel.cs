using System.Collections.ObjectModel;
using System.Drawing;
using System.Linq;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HomekoWorld.Engine;
using HomekoWorld.Hardware;
using HomekoWorld.Hooks;
using HomekoWorld.Models;
using HomekoWorld.Models.Farm;
using HomekoWorld.Services;
using HomekoWorld.Services.Farm;
using HomekoWorld.Services.Skills;
using HomekoWorld.Services.Vision;
using HomekoWorld.Services.Yolo;
using Microsoft.Win32;
using System.IO;

namespace HomekoWorld.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly TransportRouter     _router;
    private readonly IKeyDeviceTransport _transport; // same object as _router, kept for brevity
    private readonly BindingDispatcher   _dispatcher;
    private readonly ComboEngine         _engine;
    private readonly JsonStateStore      _store;
    private readonly SkillLibrary        _skillLibrary;
    private readonly SkillBarResolver    _skillResolver;
    private readonly GlobalKeyboardHook  _hook;
    private AppState _state;

    // ---- Connection ----
    [ObservableProperty] private string _phoneHost = "192.168.1.100";
    [ObservableProperty] private int    _phonePort = 5556;
    [ObservableProperty] private bool   _isConnected;
    [ObservableProperty] private string _connectStatus = "Bağlı değil";
    [ObservableProperty] private bool   _isUsbMode;
    [ObservableProperty] private bool   _isLocalMode;
    [ObservableProperty] private bool   _isRp2040Mode;   // Faz 30: RP2040 USB-HID köprüsü

    private string _savedWifiHost = "192.168.1.100";

    // ---- Page navigation ----
    [ObservableProperty] private string _currentPage = "combo";

    // ---- Active state ----
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FarmToggleEnabled))]
    [NotifyPropertyChangedFor(nameof(FarmStartLabel))]
    private bool _active;

    // ---- Combo list ----
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _currentProfile = "all";
    [ObservableProperty] private string _currentClass = "all";
    [ObservableProperty] private ComboViewModel? _selectedCombo;
    [ObservableProperty] private bool _isEditing;

    public ObservableCollection<ComboViewModel>         Combos             { get; } = [];
    public ObservableCollection<ComboViewModel>         Filtered           { get; } = [];
    public ObservableCollection<ComboViewModel>         FarmAvailableCombos{ get; } = [];
    public ObservableCollection<ProfileViewModel>       Profiles           { get; } = [];
    public ObservableCollection<CharacterClassViewModel> Classes           { get; } = [];
    public ComboEditorViewModel Editor { get; }

    // ---- Profile creation ----
    [ObservableProperty] private bool   _isAddingProfile;
    [ObservableProperty] private string _newProfileName = "";


    // ---- Loop ----
    [ObservableProperty] private bool _isLoopRunning;

    // ---- Log ----
    [ObservableProperty] private string _logMessage = "Hazır";
    [ObservableProperty] private string _firingComboId = string.Empty;

    // ---- Skill bar ----
    [ObservableProperty] private int    _activeBarIndex;
    [ObservableProperty] private string _calibrationStatus = "⚠ Kalibre edilmedi";
    [ObservableProperty] private bool   _isCalibrated;

    // ---- HUD telemetry (Faz 13) ----
    [ObservableProperty] private long   _pingMs      = -1;   // -1 = no data yet
    private double _pingEma = -1;                            // exponential moving average (raw, pre-round)
    private const double PingAlpha = 0.2;                    // 0.2 ≈ 5-sample window, smooths spikes
    [ObservableProperty] private string _firingComboName = string.Empty;
    [ObservableProperty] private int    _stepIndex;
    [ObservableProperty] private int    _totalSteps;

    // ---- Adaptive delay (Faz 14: ping, Faz 15: FPS) ----
    [ObservableProperty] private bool   _adaptPingEnabled;
    [ObservableProperty] private double _pingMultiplier  = 1.0;
    [ObservableProperty] private bool   _adaptFpsEnabled;
    [ObservableProperty] private int    _calibrationFps  = 60;
    [ObservableProperty] private string _currentFpsInput = "60";
    /// <summary>Live preview shown in the settings panel for a 350 ms base delay.</summary>
    [ObservableProperty] private string _adaptPreview    = "";

    private bool _isFarmCalibrating;

    // ---- Faz 17: Oto Farm ----
    [ObservableProperty] private bool   _farmEnabled;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FarmToggleEnabled))]
    [NotifyPropertyChangedFor(nameof(FarmStartLabel))]
    private bool _farmRunning;          // döngü şu an aktif mi
    [ObservableProperty] private string _farmStatus           = "Pasif";
    [ObservableProperty] private string _engineBuildStatus    = "Hazırlanıyor...";
    [ObservableProperty] private bool _isEngineBuilding       = false;
    [ObservableProperty] private string _farmCalibrationState = "";
    [ObservableProperty] private string _farmMobsJsonPath     = "";
    [ObservableProperty] private string _farmModelPath        = "";
    [ObservableProperty] private string _farmSelectedMobName  = "";
    [ObservableProperty] private bool   _farmHpPotEnabled     = true;
    [ObservableProperty] private bool   _farmMpPotEnabled     = true;
    [ObservableProperty] private int    _farmHpPotPercent     = 50;
    [ObservableProperty] private int    _farmMpPotPercent     = 30;
    [ObservableProperty] private string _farmHpPotKey         = "F1";
    [ObservableProperty] private string _farmMpPotKey         = "F2";
    [ObservableProperty] private string _farmSelectedComboId  = "";
    // ---- Farm HUD (Faz 17) ----
    [ObservableProperty] private bool   _farmHudVisible;

    // ---- Birleşik Ana HUD + Log HUD (Faz 20) ----
    [ObservableProperty] private string _farmCurrentMob = "";   // anlık hedef adı
    [ObservableProperty] private string _farmElapsed     = "00:00"; // oturum süresi
    [ObservableProperty] private int    _farmKillsPerHour;          // kill/saat
    [ObservableProperty] private int    _farmInferenceFps;          // Inference FPS (gerçek, başarılı inference)
    [ObservableProperty] private int    _farmCaptureMs;             // A3: ekran yakalama ms/kare (ort)
    [ObservableProperty] private int    _farmInferenceMs;           // A3: YOLO inference toplam ms/kare (ort)
    [ObservableProperty] private int    _farmPrepMs;                // P0: preprocess (CPU) ms
    [ObservableProperty] private int    _farmGpuMs;                 // P0: GPU Run (saf inference) ms
    [ObservableProperty] private int    _farmPostMs;                // P0: postprocess (CPU) ms
    [ObservableProperty] private int    _farmFrameAgeMs;            // B1: son tıklamada tespit yaşı (stale-box)
    [ObservableProperty] private bool   _farmPaused;                // engine duraklatıldı mı
    [ObservableProperty] private bool   _hudExpanded;               // HUD genişletildi mi
    [ObservableProperty] private bool   _logHudVisible;             // Log HUD görünür mü
    [ObservableProperty] private bool   _farmLootEnabled;           // loot adımı açık mı (varsayılan kapalı)
    [ObservableProperty] private bool   _farmShowDetectionOverlay;  // YOLO tespit kutuları overlay'i açık mı
    [ObservableProperty] private bool   _farmReplayEnabled;         // B2: replay/benchmark kaydı açık mı (OFF default)
    [ObservableProperty] private bool   _farmPipelined;             // P2: pipelined inference (yüksek FPS, ON default)
    [ObservableProperty] private bool   _farmRecordingMode;         // A4: kayıt/performans modu (GPU'ya nefes ver)
    [ObservableProperty] private bool   _farmArcherMode;            // B1: archer (yaklaş+yönel) vs archer dışı (kombo hemen)
    [ObservableProperty] private int    _farmArcherRangePx;         // B2: archer yaklaşma mesafesi (px) — UI kaydırıcı
    [ObservableProperty] private Models.HpBarDetectionMode _farmHpBarMode; // HP bar tespit yöntemi (Hsv/ColorScan)
    [ObservableProperty] private double _farmConfidence;            // T1: YOLO güven eşiği (0-1) — UI kaydırıcı
    [ObservableProperty] private double _farmIou;                   // A6: NMS IoU eşiği (0.20-0.80) — UI kaydırıcı
    [ObservableProperty] private bool   _farmDxgiCapture;           // T2: DXGI hızlı yakalama (kapalı = GDI)
    [ObservableProperty] private Models.Farm.InferenceBackend _farmInferenceBackend; // T4: ONNX EP (Auto/DirectML/Cpu)
    [ObservableProperty] private int    _farmSelectedMobCount;      // çoklu seçimde kaç mob seçili (UI badge)
    // P3: ROI yakalama — tam ekran yerine belirlenen bölge (Cap ms düşer)

    /// <summary>HP bar tespit yöntemi seçenekleri (ComboBox ItemsSource).</summary>
    public Array HpBarModeOptions { get; } = Enum.GetValues(typeof(Models.HpBarDetectionMode));

    /// <summary>ONNX execution provider seçenekleri (ComboBox ItemsSource).</summary>
    public Array InferenceBackendOptions { get; } = Enum.GetValues(typeof(Models.Farm.InferenceBackend));

    /// <summary>Log HUD canlı aktivite akışı (durum mesajları + tuşlar + kombo adımları).</summary>
    public System.Collections.ObjectModel.ObservableCollection<Models.Farm.ActivityEntry> FarmActivityLog { get; } = new();

    // ── A3: motor→UI coalescing ────────────────────────────────────────────────
    // Yüksek frekanslı motor event'leri UI thread'ini doldurup (aynı thread'deki) düşük
    // seviye hook'ları aç bırakmasın diye: event'ler veriyi yalnız volatile alanlara/
    // thread-safe kuyruğa yazar; tek bir DispatcherTimer (Background öncelik) bunları
    // toplu uygular. Hiçbir motor/hook thread'i artık UI'yı senkron beklemez.
    private readonly System.Collections.Concurrent.ConcurrentQueue<Models.Farm.ActivityEntry> _activityQueue = new();
    private volatile string? _pendingFarmStatus;
    private int _telemetryDirty; // 0/1 (Interlocked)
    private System.Windows.Threading.DispatcherTimer? _uiCoalesceTimer;

    // ---- Faz 18: App-level settings ----
    [ObservableProperty] private string _globalStartKey  = "F12";
    [ObservableProperty] private string _language        = "tr";
    [ObservableProperty] private string _keyboardTestKey = "F9";

    // ---- Global Auto-Pot (Faz 18) ----
    [ObservableProperty] private bool   _autoPotEnabled;
    [ObservableProperty] private bool   _autoPotHpEnabled  = true;
    [ObservableProperty] private int    _autoPotHpPercent  = 50;
    [ObservableProperty] private string _autoPotHpKey      = "F1";
    [ObservableProperty] private bool   _autoPotMpEnabled  = true;
    [ObservableProperty] private int    _autoPotMpPercent  = 30;
    [ObservableProperty] private string _autoPotMpKey      = "F2";
    [ObservableProperty] private string _autoPotStartKey   = "F11";

    // ---- Hedef HP bar (ML ROI önizleme + renk tarama fallback) ----
    [ObservableProperty] private string _hpBarRoiPreviewStatus = "";
    [ObservableProperty] private string _targetHpColorCalibStatus = "";
    [ObservableProperty] private System.Windows.Media.ImageSource? _hpColorScanPreview;
    [ObservableProperty] private bool _hpColorScanPreviewVisible = false;

    // ---- Koruma mobu (guardian) kalibrasyonu ----
    [ObservableProperty] private bool   _guardianDetectionEnabled = true;
    [ObservableProperty] private bool   _guardianUnknownStrict    = true; // 9.tur: Unknown = saldırma, kısa atla
    [ObservableProperty] private string _nameplateCalibStatus     = "";
    [ObservableProperty] private string _nameBandCalibStatus      = "";
    [ObservableProperty] private System.Windows.Media.ImageSource? _nameBandPreview;
    [ObservableProperty] private bool   _nameBandPreviewVisible   = false;


    // ---- Test Click (click injection diagnostic) ----
    [ObservableProperty] private string _testClickStatus  = "";
    [ObservableProperty] private string _farmTestClickKey = "F8";
    [ObservableProperty] private int    _farmKills;
    [ObservableProperty] private int    _farmHpPotsUsed;
    [ObservableProperty] private int    _farmMpPotsUsed;
    [ObservableProperty] private string _farmCurrentKey       = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FarmStartLabel))]
    private string _farmHotKey = "F9";

    public ObservableCollection<MobInfo>        FarmMobs         { get; } = [];
    public ObservableCollection<MobCardViewModel> FarmMobsFiltered { get; } = [];

    [ObservableProperty] private string _farmSearchText = "";

    private readonly List<MobCardViewModel> _farmMobCards = [];

    /// <summary>Başlat/Durdur butonu her zaman aktif (mod sistemi başlatmayı doğrular).</summary>
    public bool   FarmToggleEnabled => true;
    /// <summary>Button label showing current action + configurable hotkey. Aktif moda göre (Oto Farm / Otonom / …)
    /// ve çalışma durumuna göre (Active = herhangi bir mod çalışıyor) etiketlenir.</summary>
    public string FarmStartLabel    => Active
        ? $"{ActiveModeLabel} Durdur  ({GlobalStartKey})"
        : $"{ActiveModeLabel} Başlat  ({GlobalStartKey})";
    private readonly FarmEngine      _farmEngine;
    private readonly MobLibrary      _mobLibrary;
    private readonly AutoPotService  _autoPotService;
    private readonly AutonomousPlayerEngine _autonomousEngine;
    private readonly HomekoWorld.Services.Autonomous.CoordinateReader _coordReader;
    private readonly HomekoWorld.Services.Autonomous.WorldNavigator   _navigator;
    private readonly HomekoWorld.Services.Autonomous.InventoryReader  _inventoryReader;
    private readonly HomekoWorld.Services.Autonomous.MerchantTrader   _merchantTrader;
    private readonly HomekoWorld.Services.Autonomous.IconMatcher      _iconMatcher;
    private readonly HomekoWorld.Services.Autonomous.TownObjectDetector _townDetector;
    private readonly HomekoWorld.Services.Farm.ClanBankService        _clanBank;

    private CancellationTokenSource? _reconnectCts;

    public MainViewModel(
        TransportRouter      router,
        BindingDispatcher    dispatcher,
        ComboEngine          engine,
        JsonStateStore       store,
        ComboEditorViewModel editor,
        AppState             state,
        SkillLibrary         skillLibrary,
        SkillBarResolver     skillResolver,
        GlobalKeyboardHook   hook,
        FarmEngine           farmEngine,
        MobLibrary           mobLibrary,
        AutoPotService       autoPotService,
        AutonomousPlayerEngine autonomousEngine,
        HomekoWorld.Services.Autonomous.CoordinateReader coordinateReader,
        HomekoWorld.Services.Autonomous.WorldNavigator   worldNavigator,
        HomekoWorld.Services.Autonomous.InventoryReader  inventoryReader,
        HomekoWorld.Services.Autonomous.MerchantTrader   merchantTrader,
        HomekoWorld.Services.Autonomous.IconMatcher      iconMatcher,
        HomekoWorld.Services.Autonomous.TownObjectDetector townDetector,
        HomekoWorld.Services.Farm.ClanBankService        clanBankService)
    {
        _router        = router;
        _transport     = router;
        _dispatcher    = dispatcher;
        _engine        = engine;
        _store         = store;
        _skillLibrary  = skillLibrary;
        _skillResolver = skillResolver;
        _hook          = hook;
        _farmEngine     = farmEngine;
        _mobLibrary     = mobLibrary;
        _autoPotService = autoPotService;
        _autonomousEngine = autonomousEngine;
        _coordReader      = coordinateReader;
        _navigator        = worldNavigator;
        _inventoryReader  = inventoryReader;
        _merchantTrader   = merchantTrader;
        _iconMatcher      = iconMatcher;
        _townDetector     = townDetector;
        Editor          = editor;

        // Dev 2 — paylaşımlı FarmEngine'e Otonom nav/koord servislerini enjekte et (OtoFarm "farm lokasyonuna
        // dönüş" için). Kalibre değilse FarmEngine tarafında sessizce devre dışı kalır.
        _farmEngine.Navigator   = _navigator;
        _farmEngine.CoordReader = _coordReader;

        // Dev 3 — OtoFarm envanter boşaltma (klan bankası) servisini FarmEngine'e bağla.
        _clanBank = clanBankService;
        _farmEngine.Bank = _clanBank;

        _state         = state;

        // KÖK NEDEN (2026-06-20): master çözünürlüğü STARTUP'ta ResolutionMapper'a bildir. Eskiden SetMaster
        // YALNIZ kalibrasyon komutlarında çağrılıyordu → kalibrasyon yapılmayan oturumda MasterW=0 → ResolutionMapper
        // identity (ölçeklemiyor) → master'dan FARKLI çözünürlükteki müşteride tüm baked koordinatlar (HP bar ROI,
        // waypoint, ızgara) kayardı. Ref=0 ise (hiç kalibre yok) identity korunur — kendi makinesi etkilenmez.
        if (_state.CalibrationRefWidth > 0 && _state.CalibrationRefHeight > 0)
            Services.Capture.ResolutionMapper.SetMaster(_state.CalibrationRefWidth, _state.CalibrationRefHeight);

        // Faz 17: Farm — load persisted settings
        _farmEnabled         = _state.Farm.Enabled;
        _farmMobsJsonPath    = _state.Farm.MobsJsonPath;
        _farmModelPath       = _state.Farm.ModelPath;
        _farmSelectedMobName  = _state.Farm.SelectedMobName;
        _farmSelectedComboId  = _state.Farm.SelectedComboId;
        _farmHpPotEnabled    = _state.Farm.HpPotEnabled;
        _farmMpPotEnabled    = _state.Farm.MpPotEnabled;
        _farmHpPotPercent    = _state.Farm.HpPotPercent;
        _farmMpPotPercent    = _state.Farm.MpPotPercent;
        _farmHpPotKey        = _state.Farm.HpPotKey;
        _farmMpPotKey        = _state.Farm.MpPotKey;
        _farmHotKey          = _state.Farm.HotKey;
        _farmTestClickKey    = _state.Farm.TestClickKey;
        _farmLootEnabled     = _state.Farm.LootEnabled;
        _farmShowDetectionOverlay = _state.Farm.ShowDetectionOverlay;
        _farmReplayEnabled   = _state.Farm.ReplayEnabled;
        _farmPipelined       = _state.Farm.PipelinedInference;
        _farmRecordingMode   = _state.Farm.RecordingMode;
        _farmArcherMode      = _state.Farm.EngageMovement == Models.Farm.EngageMovement.ArcherWalkAndFace;
        _farmArcherRangePx   = _state.Farm.ArcherApproachRangePx;
        _farmHpBarMode       = _state.Wtm.HpBarMode;
        _farmConfidence      = _state.Farm.ConfidenceThreshold;
        _farmIou             = _state.Farm.IouThreshold;
        _farmDxgiCapture     = _state.Farm.CaptureBackend == Models.Farm.CaptureBackend.Dxgi;
        _farmInferenceBackend = _state.Farm.InferenceBackend;
        _farmSelectedMobCount = _state.Farm.SelectedMobNames.Count;
        _hpBarRoiPreviewStatus = _state.Wtm.IsHpBarRoiCalibrated
            ? $"✓ Mob HP barı  X={_state.Wtm.HpBarRoiX}  Y={_state.Wtm.HpBarRoiY}  {_state.Wtm.HpBarRoiW}×{_state.Wtm.HpBarRoiH}px"
            : "✗ Kalibre edilmedi (Ana kalibrasyon 3. adım)";
        _targetHpColorCalibStatus = _state.Wtm.IsTargetHpColorCalibrated
            ? $"Renk fallback: X={_state.Wtm.HpColorScanX} Y={_state.Wtm.HpColorScanY}"
            : "Renk fallback: kalibre edilmedi";
        _guardianDetectionEnabled = _state.Wtm.GuardianDetectionEnabled;
        _guardianUnknownStrict    = _state.Wtm.GuardianUnknownStrict;
        _nameplateCalibStatus     = _state.Wtm.IsNameplateCalibrated
            ? "✓ Normal + Koruma rengi kalibre"
            : "✗ Kalibre edilmedi";
        _nameBandCalibStatus      = _state.Wtm.IsNameBandCalibrated
            ? $"✓ İsim bandı  X={_state.Wtm.NameBandX}  Y={_state.Wtm.NameBandY}  {_state.Wtm.NameBandW}×{_state.Wtm.NameBandH}px"
            : "✗ İsim bandı çizilmedi";
        // Faz 18: Global settings
        _globalStartKey  = _state.GlobalStartKey;
        _language        = _state.Language;
        _keyboardTestKey = _state.KeyboardTestKey;

        // Faz 18: Global AutoPot — load persisted settings
        _autoPotEnabled   = _state.AutoPot.Enabled;
        _autoPotHpEnabled = _state.AutoPot.HpPotEnabled;
        _autoPotHpPercent = _state.AutoPot.HpPotPercent;
        _autoPotHpKey     = _state.AutoPot.HpPotKey;
        _autoPotMpEnabled = _state.AutoPot.MpPotEnabled;
        _autoPotMpPercent = _state.AutoPot.MpPotPercent;
        _autoPotMpKey     = _state.AutoPot.MpPotKey;
        _autoPotStartKey  = _state.AutoPot.StartKey;

        // Faz 17: Farm — wire engine events (A3: coalesce; motor/hook thread'i UI beklemez)
        _farmEngine.StatusChanged += (_, s) =>
        {
            _pendingFarmStatus = s;          // son durum kazanır; timer uygular
            EnqueueActivity(s, "event");
        };
        _farmEngine.TelemetryUpdated += (_, _) =>
            System.Threading.Interlocked.Exchange(ref _telemetryDirty, 1);
        _farmEngine.KeyLogged += (_, e) => EnqueueActivity(e.Text, e.Kind);

        // Faz 31/32/33/34/35: Otonom Oyuncu — kalıcı ayarları yükle (backing field ctor'da = MVVMTK0034 yok) + event'leri bağla
        _autonomousEnabled      = _state.Autonomous.Enabled;
        // Faz 33 envanter ayarları
        _inventoryKey              = _state.Autonomous.InventoryKey;
        _inventoryCheckEveryKills  = _state.Autonomous.InventoryCheckEveryKills.ToString();
        _inventoryFullThreshold    = _state.Autonomous.InventoryFullThreshold;
        _inventoryOpenDelayMs      = _state.Autonomous.InventoryOpenDelayMs.ToString();
        // Faz 34 nav ayarları
        _navCameraPixPerDeg     = _state.Autonomous.NavCameraPixPerDeg;
        _navToleranceCoords     = _state.Autonomous.NavToleranceCoords.ToString();
        _navStepMs              = _state.Autonomous.NavStepMs.ToString();
        _navCameraInvert        = _state.Autonomous.NavCameraInvert;
        // Faz 35 Town/portal ayarları
        _townTpKey              = _state.Autonomous.TownTpKey;
        _townTpWaitMs           = _state.Autonomous.TownTpWaitMs.ToString();
        _portalConfirmKey       = _state.Autonomous.PortalConfirmKey;
        _portalWaitMs           = _state.Autonomous.PortalWaitMs.ToString();
        _portalClickOffsetX     = _state.Autonomous.PortalClickOffsetX.ToString();
        _portalClickOffsetY     = _state.Autonomous.PortalClickOffsetY.ToString();
        _portalInteractDelayMs  = _state.Autonomous.PortalInteractDelayMs.ToString();
        WireAutonomous();
        LoadFarmExtrasSettings();   // Dev 2 (dönüş) + Dev 3 (klan bankası) kalıcı ayarları
        LoadTargetFrameSettings();  // V4 — hedef penceresi çerçeve şablonu kalıcı ayarları

        // Faz 17: Farm — populate mob list from persisted mobs.json
        if (!string.IsNullOrWhiteSpace(_farmMobsJsonPath))
        {
            _mobLibrary.Load(_farmMobsJsonPath);
            foreach (var m in _mobLibrary.Mobs) FarmMobs.Add(m);
            RebuildMobCards();
        }

        // Faz 17: Farm — auto-load model on startup if a path was already persisted
        if (!string.IsNullOrWhiteSpace(_farmModelPath))
            OnFarmModelPathChanged(_farmModelPath);

        // FujiMacro: tek başlat/durdur tuşu (F12) — seçili modu aç/kapat
        _hook.KeyDown += OnModeToggleHotkeyDown;
        // Test Click hotkey — oyun önde iken test tıklaması tetikle
        _hook.KeyDown += OnTestClickHotkeyDown;
        // Faz 19: AutoPot toggle hotkey (bağımsız overlay)
        _hook.KeyDown += OnAutoPotHotkeyDown;
        // Hook'u şimdi başlat — F12 + hotkey'ler her zaman dinlensin (mod çalışmasa bile)
        _hook.Start();

        _savedWifiHost = _state.SerialHost;
        PhonePort      = _state.SerialPort;
        IsLocalMode    = _state.ConnectionMode == "local";
        IsUsbMode      = _state.ConnectionMode == "usb";
        IsRp2040Mode   = _state.ConnectionMode == "rp2040";
        // eski "kernel" değeri kaydedilmişse local'e düşür (graceful migration)
        if (_state.ConnectionMode == "kernel") { _state.ConnectionMode = "local"; IsLocalMode = true; }
        PhoneHost      = IsUsbMode ? _state.UsbHost : _savedWifiHost;
        CurrentProfile    = _state.ProfileId;
        CurrentClass      = _state.ClassId;
        CurrentPage       = _state.CurrentPage;
        SyncSelectedModeFromPage();   // FujiMacro: seçili modu sayfadan türet


        // Faz 14/15: load adaptive delay settings
        AdaptPingEnabled = _state.AdaptPingEnabled;
        PingMultiplier   = _state.PingMultiplier;
        AdaptFpsEnabled  = _state.AdaptFpsEnabled;
        CalibrationFps   = _state.CalibrationFps;
        CurrentFpsInput  = _state.CurrentFpsInput;
        PushAdaptiveSettings();     // initialise ComboEngine.AdaptiveSettings

        LoadCombos();
        LoadProfiles();
        LoadClasses();
        ApplyFilter();

        // Taze/müşteri kurulumu: model yolu boş veya dosya yoksa kurulum dizininde best.onnx'i
        // otomatik bul + yükle. (Eskiden yalnız UI butonu yapardı → state.json'suz ilk açılışta model
        // HİÇ yüklenmiyordu, müşteri "model seç" ile uğraşıyordu.) Geçerli yol varsa dokunulmaz.
        if (string.IsNullOrWhiteSpace(_state.Farm.ModelPath) || !System.IO.File.Exists(_state.Farm.ModelPath))
            AutoDiscoverFarmFiles();

        // FujiMacro: AutoPot artık ANA mod (Active) açıkken çalışır — başlangıçta ASLA otomatik başlamaz.
        // (Eskiden launch'ta Enabled ise hemen başlıyordu → oyun odakta değilken masaüstü ROI'sinden
        //  düşük-ama->0 okuyup pot tuşlarına spam basıyordu.) Servis OnActiveChanged/OnAutoPotEnabledChanged
        // tarafından YALNIZ "Active && AutoPot.Enabled" iken başlatılır. Enabled = armed, Active = master gate.

        _engine.ComboFired += (_, e) =>
        {
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                FiringComboId    = e.ComboId;
                var vm = Combos.FirstOrDefault(c => c.Id == e.ComboId);
                FiringComboName  = vm?.Name ?? string.Empty;
                LogMessage       = $"Kombo tetiklendi: {vm?.Name}";
                StepIndex        = 0;
                TotalSteps       = 0;
                UpdateStats(e.ComboId, e.ElapsedMs);
                if (vm is not null) PulseIsFiring(vm);
            });
        };

        _engine.StepProgressed += (_, e) =>
        {
            // Aktivite log throttle'lı kuyruğa (motor thread'inde, UI'dan bağımsız) — loop
            // kombo her adımda ateşlese de ObservableCollection mutasyonu timer'da toplu yapılır.
            var combo = _state.Combos.FirstOrDefault(c => c.Id == e.ComboId);
            var step  = combo?.Steps.ElementAtOrDefault(e.StepIndex);
            string keyText = step is null
                ? $"adım {e.StepIndex + 1}/{e.TotalSteps}"
                : $"{step.Key} ({e.StepIndex + 1}/{e.TotalSteps})";
            EnqueueActivity(keyText, "combo");

            // Combo ilerleme HUD'u (düşük maliyet) — BeginInvoke: motor thread'i bloke etmez.
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                if (FiringComboId != e.ComboId) FiringComboId = e.ComboId;
                StepIndex  = e.StepIndex + 1; // 1-based for display
                TotalSteps = e.TotalSteps;
            });
        };

        _transport.LatencyMs += (_, ms) =>
        {
            _pingEma = _pingEma < 0 ? ms : PingAlpha * ms + (1 - PingAlpha) * _pingEma;
            var smoothed = (long)Math.Round(_pingEma);
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                PingMs = smoothed;
                PushAdaptiveSettings(); // keep engine's ping current (Faz 14)
            });
        };

        _engine.Error += (_, msg) =>
        {
            Application.Current.Dispatcher.BeginInvoke(() => LogMessage = $"Hata: {msg}");
        };

        _engine.LoopStateChanged += (_, running) =>
        {
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                IsLoopRunning = running;
                LogMessage = running ? "⟳ Loop çalışıyor — durdurmak için tuşa tekrar bas" : "Loop durduruldu";
            });
        };

        _transport.LineReceived += (_, line) =>
        {
            if (line == "BT_NOT_CONNECTED")
                Application.Current.Dispatcher.BeginInvoke(() =>
                    LogMessage = "⚠ Telefon Bluetooth HID bağlı değil! Telefonda BT durumunu kontrol et.");
        };

        _transport.Disconnected += (_, disconnectReason) =>
        {
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                IsConnected = false;
                bool isRp = disconnectReason == "rp2040";
                ConnectStatus = isRp
                    ? "RP2040 bağlantısı kesildi — yeniden bağlanılıyor…"
                    : "Bağlantı kesildi — yeniden bağlanılıyor…";
                LogMessage = isRp
                    ? "RP2040 USB bağlantısı kesildi (USB reset?) — otomatik yeniden bağlanılıyor…"
                    : "Telefon bağlantısı kesildi! Otomatik yeniden bağlanılıyor…";
                ScheduleReconnect();
            });
        };

        Editor.Saved     += (_, _) => CommitEdit();
        Editor.Cancelled += (_, _) => IsEditing = false;

        // Track active F-bar via global keyboard hook (F1-F8)
        _hook.KeyDown += OnHookKeyDown;

        // Initialise calibration status badge
        RefreshCalibrationStatus();

        // A3: motor→UI coalescing timer'ını başlat (UI thread; app ömrü boyunca, ~sıfır boşta maliyet)
        StartUiCoalesceTimer();

        // Apply persisted transport mode (must come after all event subscriptions)
        if (IsLocalMode)
        {
            _router.SwitchToLocal();
            _pingEma = 0;
            PingMs   = 0;
            PushAdaptiveSettings();
            IsConnected   = true;
            ConnectStatus = "Local — PC";
        }
        else if (IsUsbMode)
        {
            _router.SwitchToNet(TransportMode.Usb);
        }
        else if (IsRp2040Mode)
        {
            // Faz 30: son kullanılan mod RP2040 ise cihazı arka planda (USB) bağla.
            _router.SwitchToRp2040();
            ConnectStatus = "RP2040 aranıyor…";
            ConnectRp2040Async();
        }
    }

    // Faz 30: RP2040'a async bağlan (ctor fire-and-forget — async void: exception UI'da yutulur).
    private async void ConnectRp2040Async()
    {
        try
        {
            var ok = await _transport.ConnectAsync("", 0);
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                IsConnected   = ok;
                ConnectStatus = ok ? "RP2040 — bağlı" : "RP2040 aranıyor…";
                if (ok) { _pingEma = 0; PingMs = 0; PushAdaptiveSettings(); }
                else    { ScheduleRp2040Reconnect(); }   // cihaz takılı değilse arka planda ara
            });
        }
        catch { Application.Current.Dispatcher.BeginInvoke(() => ScheduleRp2040Reconnect()); }
    }

    private static readonly Key[] FBarKeys =
        [Key.F1, Key.F2, Key.F3, Key.F4, Key.F5, Key.F6, Key.F7, Key.F8];

    private void OnHookKeyDown(object? sender, HookKeyEventArgs e)
    {
        var idx = Array.IndexOf(FBarKeys, e.Key);
        if (idx < 0) return;
        _skillResolver.ActiveBarIndex = idx;
        // Hook thread'inde çalışır → senkron Invoke hook'u bloke ederdi; BeginInvoke kullan.
        Application.Current.Dispatcher.BeginInvoke(() => ActiveBarIndex = idx);
    }

    private void RefreshCalibrationStatus()
    {
        var map = _state.SkillBar;
        if (map.CalibratedAt is null)
        {
            CalibrationStatus = "⚠ Kalibre edilmedi";
            IsCalibrated      = false;
        }
        else
        {
            CalibrationStatus = $"✓ {map.FilledSlotCount}/{map.TotalSlotCount} slot tanımlı";
            IsCalibrated      = true;
        }
    }

    [RelayCommand]
    private void ResetCalibration()
    {
        var result = MessageBox.Show(
            "Kalibrasyon verisi silinecek. Tüm slot-skill eşleştirmeleri kaybolacak. Emin misiniz?",
            "Kalibrasyonu Sıfırla",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        _state.SkillBar = new Models.SkillSlotMap();
        SaveState();
        RefreshCalibrationStatus();
    }

    [RelayCommand]
    private void OpenCalibration()
    {
        var vm     = new CalibrationViewModel(_skillLibrary, new SkillRecognizer(_skillLibrary), _state);
        var wizard = new Views.CalibrationWizardWindow(vm);
        wizard.Owner = Application.Current.MainWindow;
        wizard.ShowDialog();
        SaveState();
        RefreshCalibrationStatus();
    }

    private Views.TestRunnerWindow? _testWindow;

    [RelayCommand]
    private void OpenTestRunner()
    {
        if (_testWindow is { IsLoaded: true })
        {
            _testWindow.Activate();
            return;
        }
        var vm = App.Services.GetRequiredService<TestRunnerViewModel>();
        _testWindow = new Views.TestRunnerWindow { DataContext = vm, Owner = Application.Current.MainWindow };
        _testWindow.Closed += (_, _) => _testWindow = null;
        _testWindow.Show();
    }

    private Views.SettingsWindow?  _settingsWindow;
    private Views.HelpWindow?      _helpWindow;

    [RelayCommand]
    private void OpenSettings()
    {
        if (_settingsWindow is { IsLoaded: true }) { _settingsWindow.Activate(); return; }
        _settingsWindow = new Views.SettingsWindow { DataContext = this, Owner = Application.Current.MainWindow };
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    [RelayCommand]
    private void OpenHelp()
    {
        if (_helpWindow is { IsLoaded: true }) { _helpWindow.Activate(); return; }
        _helpWindow = new Views.HelpWindow { Owner = Application.Current.MainWindow };
        _helpWindow.Closed += (_, _) => _helpWindow = null;
        _helpWindow.Show();
    }

    [RelayCommand]
    private void ToggleLanguage() => Language = Language == "tr" ? "en" : "tr";

    [RelayCommand]
    private void ToggleActive() => ToggleSelectedMode();

    public void SetCapturing(bool capturing)
    {
        _dispatcher.IsCapturingBinding = capturing;
    }

    [RelayCommand]
    private void SelectPage(string page)
    {
        // Mod çalışırken sayfa/mod değiştirmeyi engelle (önce Durdur)
        if (Active)
        {
            LogMessage = "Mod çalışıyor — önce Durdur (F12), sonra modu değiştir.";
            return;
        }
        CurrentPage = page;
    }

    partial void OnCurrentPageChanged(string value)
    {
        _state.CurrentPage = value;
        _store.Save(_state);
        // FujiMacro: sayfa seçimi = çalıştırılacak mod. "settings" bir MOD DEĞİL —
        // Ayarlar'a geçmek F12'nin başlatacağı modu değiştirmesin (son mod korunur).
        if (value != "settings")
            SyncSelectedModeFromPage();
    }

    private void UpdateStats(string comboId, double elapsedMs)
    {
        if (!_state.Stats.TryGetValue(comboId, out var stats))
            stats = new ComboStats();
        stats.TotalCasts++;
        stats.AvgSpeedMs = stats.TotalCasts == 1
            ? elapsedMs
            : (stats.AvgSpeedMs * (stats.TotalCasts - 1) + elapsedMs) / stats.TotalCasts;
        _state.Stats[comboId] = stats;

        var vm = Combos.FirstOrDefault(c => c.Id == comboId);
        vm?.ApplyStats(stats);
    }

    private async void PulseIsFiring(ComboViewModel vm)
    {
        vm.IsFiring = true;
        await Task.Delay(500);
        vm.IsFiring = false;
    }

    // ─────────────────────────────────────────────────────────────────────────

    private void SaveState()
    {
        if (IsLocalMode)
        {
            _state.ConnectionMode = "local";
            _state.SerialHost     = _savedWifiHost;
        }
        else if (IsUsbMode)
        {
            _state.UsbHost        = PhoneHost;
            _state.SerialHost     = _savedWifiHost;
            _state.ConnectionMode = "usb";
        }
        else if (IsRp2040Mode)
        {
            _state.ConnectionMode = "rp2040";
            _state.SerialHost     = _savedWifiHost;
        }
        else
        {
            _state.SerialHost     = PhoneHost;
            _state.ConnectionMode = "wifi";
        }
        _state.SerialPort = PhonePort;
        _store.Save(_state);
    }
}
