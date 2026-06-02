using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace HomekoWorld.Views.Controls;

public partial class TitleBar : UserControl
{
    public TitleBar()
    {
        InitializeComponent();
    }

    private Window? OwnerWindow => Window.GetWindow(this);

    private void DragArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }
        OwnerWindow?.DragMove();
    }

    private void BtnMin_Click(object sender, RoutedEventArgs e)
        => OwnerWindow!.WindowState = WindowState.Minimized;

    private void BtnMax_Click(object sender, RoutedEventArgs e)
        => ToggleMaximize();

    private void BtnClose_Click(object sender, RoutedEventArgs e)
        => OwnerWindow?.Close();

    private void ToggleMaximize()
    {
        if (OwnerWindow is null) return;
        OwnerWindow.WindowState = OwnerWindow.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }
}
