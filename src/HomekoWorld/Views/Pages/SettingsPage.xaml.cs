using System.Windows.Controls;
using System.Windows.Input;

namespace HomekoWorld.Views.Pages;

public partial class SettingsPage : UserControl
{
    public SettingsPage()
    {
        InitializeComponent();
    }

    // Tek-satır TextBox'lar (iç ScrollViewer'ları yüzünden) wheel'i yutup paneli kaydırmayı
    // engelliyordu → preview aşamasında yakala, içerik panelini daima kaydır.
    private void AdvScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var sv = (ScrollViewer)sender;
        sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta);
        e.Handled = true;
    }
}
