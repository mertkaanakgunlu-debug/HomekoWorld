using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using HomekoWorld.Services;
using HomekoWorld.Services.Capture;
using HomekoWorld.ViewModels;
using HomekoWorld.Views.Controls;

namespace HomekoWorld.Views;

public partial class CalibrationWizardWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    public CalibrationWizardWindow(CalibrationViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;

        vm.RequestAreaSelection += OnRequestAreaSelection;
        vm.Completed            += (_, _) => Close();
        vm.Cancelled            += (_, _) => Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int dark = 1;
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
    }

    private void OnRequestAreaSelection(object? sender, EventArgs e)
    {
        var mainWin = Application.Current.MainWindow;
        Hide();
        if (mainWin != null && mainWin != this) mainWin.Hide();

        Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => { }));
        System.Threading.Thread.Sleep(150);

        var overlay = new CalibrationOverlayWindow();
        overlay.ShowDialog();

        Bitmap? captured = overlay.Confirmed ? ScreenCapture.CaptureScreen() : null;

        if (mainWin != null && mainWin != this) mainWin.Show();
        Show();
        Activate();

        if (overlay.Confirmed && DataContext is CalibrationViewModel vm)
            vm.SetSelectedRegion(overlay.SelectedRect, captured);
        else
            (DataContext as CalibrationViewModel)?.CancelCommand.Execute(null);
    }

    private void PickSkillButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not SlotReviewItem item) return;
        if (DataContext is not CalibrationViewModel vm) return;

        var picker = new SkillPickerDialog(vm.SlotMap, vm.Library) { Owner = this };
        if (picker.ShowDialog() == true && picker.SelectedSkillId is not null)
        {
            item.SelectedSkill = vm.AllSkills.FirstOrDefault(s => s.Id == picker.SelectedSkillId);
            item.IsEmpty = false;
        }
    }
}
