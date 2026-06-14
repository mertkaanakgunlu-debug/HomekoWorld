using CommunityToolkit.Mvvm.ComponentModel;

namespace HomekoWorld.ViewModels;

/// <summary>
/// Faz 40 — tamir slot seçici hücresi (giyili ekipman 3×5 ızgarasının bir slotu).
/// <see cref="Order"/> 1-tabanlı seçim sırası (0 = seçili değil); UI'da seçili hücre
/// yeşil highlight + sıra numarasıyla gösterilir.
/// </summary>
public partial class RepairSlotCell : ObservableObject
{
    /// <summary>Slot indeksi (satır*3 + sütun, 0-14).</summary>
    public int Index { get; }

    [ObservableProperty] private bool _selected;
    [ObservableProperty] private int  _order;

    public RepairSlotCell(int index) => Index = index;
}
