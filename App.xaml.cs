using System.Windows;

namespace vibranceGUI2;

public partial class App : Application
{
    private void Application_Exit(object sender, ExitEventArgs e)
    {
        if (MainWindow is MainWindow mw)
            mw.Cleanup();
    }
}
