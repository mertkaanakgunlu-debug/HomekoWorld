using System.Collections.ObjectModel;
using System.Drawing;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HomekoWorld.Models;
using HomekoWorld.Services;
using HomekoWorld.Services.Capture;
using HomekoWorld.Services.Skills;

namespace HomekoWorld.ViewModels;

// One slot entry for the review step (step 3)
public partial class SlotReviewItem : ObservableObject
{
    public string        SlotKey      { get; init; } = ""; // e.g. "F3.7"
    public BitmapSource? Thumbnail    { get; init; }
    public int           BestDistance { get; init; } = 64; // pHash Hamming distance of pre-selected guess
    [ObservableProperty] private SkillInfo? _selectedSkill;
    [ObservableProperty] private bool       _isEmpty;
}

public partial class CalibrationViewModel : ObservableObject
{
    private readonly SkillLibrary    _library;
    private readonly SkillRecognizer _recognizer;
    private readonly AppState        _state;

    // ── Step 1 ────────────────────────────────────────────────────────────────
    [ObservableProperty] private int _barCount  = 8;
    [ObservableProperty] private int _slotCount = 10;

    // ── Step 2 — area selection ───────────────────────────────────────────────
    // Populated by CalibrationOverlayWindow after user draws rect
    public System.Drawing.Rectangle SelectedRegion { get; set; }
    [ObservableProperty] private bool _regionSelected;
    private Bitmap? _preCapture; // captured before wizard re-appears

    // ── Step 3 — review ───────────────────────────────────────────────────────
    public ObservableCollection<SlotReviewItem> UnrecognizedSlots { get; } = [];
    public IReadOnlyList<SkillInfo>             AllSkills         => _library.All;
    public SkillLibrary                         Library           => _library;
    public SkillSlotMap                         SlotMap           => _state.SkillBar;

    // ── Navigation ────────────────────────────────────────────────────────────
    [ObservableProperty] private int    _currentStep   = 1; // 1, 2, or 3
    [ObservableProperty] private string _statusMessage = "Skill bar konfigürasyonuna hoş geldiniz.";

    public event EventHandler? Completed;
    public event EventHandler? Cancelled;
    public event EventHandler? RequestAreaSelection; // tells the view to open the overlay

    public CalibrationViewModel(SkillLibrary library, SkillRecognizer recognizer, AppState state)
    {
        _library    = library;
        _recognizer = recognizer;
        _state      = state;
    }

    [RelayCommand]
    private void GoToAreaSelection()
    {
        if (BarCount < 1 || BarCount > 8 || SlotCount < 1 || SlotCount > 10)
        {
            StatusMessage = "Lütfen geçerli bar (1-8) ve slot (1-10) sayıları girin.";
            return;
        }
        CurrentStep = 2;
        StatusMessage = "Oyun ekranında skill barlarının tümünü kapsayan alanı seçin.";
        RequestAreaSelection?.Invoke(this, EventArgs.Empty);
    }

    // Called by the overlay window after user draws the rectangle.
    // preCapture: screen grabbed right after overlay closed (windows still hidden) — prevents
    // capturing wizard chrome instead of the game.
    public void SetSelectedRegion(System.Drawing.Rectangle rect, Bitmap? preCapture = null)
    {
        _preCapture?.Dispose();
        _preCapture = preCapture;
        SelectedRegion = rect;
        RegionSelected = rect.Width > 10 && rect.Height > 10;
        if (RegionSelected)
        {
            StatusMessage = $"Seçili alan: {rect.Width}×{rect.Height} px. İşlem başlatılıyor…";
            RunRecognition();
        }
    }

    private void RunRecognition()
    {
        UnrecognizedSlots.Clear();

        Bitmap    screenShot;
        Bitmap[,] cells;
        try
        {
            // Use pre-captured bitmap when available (captured while all windows were hidden)
            screenShot  = _preCapture ?? ScreenCapture.CaptureScreen();
            _preCapture = null; // ownership transferred; don't dispose separately
            cells       = ScreenCapture.SplitGrid(screenShot, SelectedRegion, BarCount, SlotCount);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Ekran yakalanamadı: {ex.Message}";
            return;
        }

        var slotKeys = BuildSlotKeys();
        int autoCount   = 0;
        int reviewCount = 0;

        for (int bar = 0; bar < BarCount; bar++)
        for (int slot = 0; slot < SlotCount; slot++)
        {
            var cell    = cells[bar, slot];
            var (match, dist) = _recognizer.Recognize(cell);
            var slotKey = slotKeys[bar, slot];

            if (match is not null && dist <= _recognizer.AutoThreshold)
            {
                // Confident match — auto-assign
                _state.SkillBar.Slots[slotKey] = match.Id;
                autoCount++;
            }
            else
            {
                // Below confidence — send to review with best guess pre-selected
                // so user can confirm or change instead of searching 239 skills from scratch
                reviewCount++;
                UnrecognizedSlots.Add(new SlotReviewItem
                {
                    SlotKey      = slotKey,
                    Thumbnail    = ScreenCapture.ToBitmapSource(cell),
                    BestDistance = dist,
                    SelectedSkill = match, // pre-select closest match (may be null if library empty)
                });
            }
        }

        _state.SkillBar.BarCount  = BarCount;
        _state.SkillBar.SlotCount = SlotCount;

        if (UnrecognizedSlots.Count > 0)
        {
            CurrentStep   = 3;
            var autoMsg   = autoCount > 0 ? $"{autoCount} slot otomatik atandı. " : "";
            StatusMessage = $"{autoMsg}{reviewCount} slot tanınamadı — lütfen kontrol edin (öneri seçili).";
        }
        else
        {
            Finish();
        }
    }

    [RelayCommand]
    private void Finish()
    {
        // Apply manual assignments from review step
        foreach (var item in UnrecognizedSlots)
        {
            if (item.IsEmpty)
                _state.SkillBar.Slots.Remove(item.SlotKey);
            else if (item.SelectedSkill is not null)
                _state.SkillBar.Slots[item.SlotKey] = item.SelectedSkill.Id;
        }

        _state.SkillBar.CalibratedAt = DateTime.UtcNow;
        StatusMessage = "Konfigürasyon tamamlandı!";
        Completed?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel() => Cancelled?.Invoke(this, EventArgs.Empty);

    // Builds "F{bar+1}.{slotKey}" for each cell — slot keys: 1-9 then 0
    private string[,] BuildSlotKeys()
    {
        var keys      = new string[BarCount, SlotCount];
        var slotChars = new string[] { "1","2","3","4","5","6","7","8","9","0" };
        for (int bar = 0; bar < BarCount; bar++)
        for (int slot = 0; slot < SlotCount; slot++)
        {
            var slotChar = slot < slotChars.Length ? slotChars[slot] : (slot + 1).ToString();
            keys[bar, slot] = $"F{bar + 1}.{slotChar}";
        }
        return keys;
    }
}
