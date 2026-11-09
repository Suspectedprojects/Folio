using System.Windows;
using System.Windows.Threading;

namespace Folio;

public partial class App : Application
{
    public static ThumbnailLoader Loader { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Loader = new ThumbnailLoader(Dispatcher, Math.Clamp(Environment.ProcessorCount / 2, 2, 6));
        new MainWindow(e.Args.FirstOrDefault()).Show();
    }

    void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "Folio", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
