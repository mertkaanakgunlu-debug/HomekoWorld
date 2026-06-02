using System.ComponentModel;
using System.Windows;
using HomekoWorld.ViewModels;

namespace HomekoWorld.Views;

public partial class TestRunnerWindow : Window
{
    public TestRunnerWindow()
    {
        InitializeComponent();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (DataContext is TestRunnerViewModel vm && vm.IsRunning)
        {
            vm.StopCommand.Execute(null);
            // Small pause to let the loop's finally block fire ResumeAfterTest
            System.Threading.Thread.Sleep(150);
        }
    }
}
