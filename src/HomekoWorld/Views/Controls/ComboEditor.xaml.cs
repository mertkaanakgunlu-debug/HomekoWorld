using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HomekoWorld.Core;
using HomekoWorld.Models;
using HomekoWorld.Services;
using HomekoWorld.Services.Skills;
using HomekoWorld.ViewModels;

namespace HomekoWorld.Views.Controls;

public partial class ComboEditor : UserControl
{
    public ComboEditor() => InitializeComponent();

    private MainViewModel?        MainVm => DataContext as MainViewModel;
    private ComboEditorViewModel? Vm     => MainVm?.Editor;

    private ComboEditorViewModel? _subscribedVm;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        SubscribeToVm();
    }

    private void SubscribeToVm()
    {
        if (_subscribedVm is not null)
            _subscribedVm.RequestSkillPick -= OnRequestSkillPick;

        _subscribedVm = Vm;

        if (_subscribedVm is not null)
            _subscribedVm.RequestSkillPick += OnRequestSkillPick;
    }

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        DataContextChanged += (_, _) => SubscribeToVm();
    }

    private string? OnRequestSkillPick(string? classId)
    {
        var appState = (App.Services.GetService(typeof(AppState)) as AppState)
                       ?? new AppState();
        var library  = (App.Services.GetService(typeof(SkillLibrary)) as SkillLibrary)
                       ?? new SkillLibrary();

        var dialog = new SkillPickerDialog(appState.SkillBar, library, classId)
        {
            Owner = Window.GetWindow(this),
        };
        return dialog.ShowDialog() == true ? dialog.SelectedSkillId : null;
    }

    private void BindingCapture_Click(object sender, MouseButtonEventArgs e)
    {
        if (Vm is null) return;

        MainVm!.SetCapturing(true);
        Vm.IsCapturing = true;

        var win = Window.GetWindow(this);
        if (win is not null)
            win.PreviewKeyDown += OnWindowPreviewKeyDown;
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is Window win)
            win.PreviewKeyDown -= OnWindowPreviewKeyDown;

        if (Vm is null || !Vm.IsCapturing) return;
        e.Handled = true;

        MainVm?.SetCapturing(false);

        if (e.Key == Key.Escape)
        {
            Vm.SetBinding(null);
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var name = KeyCode.ToName(key);
        if (name is null) return;

        var mods = KeyCode.IsModifier(key)
            ? Array.Empty<string>()
            : KeyCode.GetModifiers(GetCurrentModifiers());

        Vm.SetBinding(new Models.KeyBinding { Modifiers = mods, Code = name });
    }

    private void StepsScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var sv = (ScrollViewer)sender;
        bool atTop    = sv.VerticalOffset == 0;
        bool atBottom = sv.VerticalOffset >= sv.ScrollableHeight;
        if (sv.ScrollableHeight == 0 || (e.Delta > 0 && atTop) || (e.Delta < 0 && atBottom))
        {
            e.Handled = true;
            (sv.Parent as UIElement)?.RaiseEvent(
                new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
                    { RoutedEvent = UIElement.MouseWheelEvent });
        }
    }

    private static KeyboardModifiers GetCurrentModifiers()
    {
        var mods = KeyboardModifiers.None;
        if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
            mods |= KeyboardModifiers.Shift;
        if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl))
            mods |= KeyboardModifiers.Ctrl;
        if (Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt))
            mods |= KeyboardModifiers.Alt;
        return mods;
    }
}
