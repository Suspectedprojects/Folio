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
        // "Folio.exe --join a.jpg b.jpg ..." opens the Join images window on its own.
        if (e.Args.Length > 2 && e.Args[0].Equals("--join", StringComparison.OrdinalIgnoreCase))
        {
            new JoinWindow(e.Args.Skip(1)) { WindowStartupLocation = WindowStartupLocation.CenterScreen }.Show();
            return;
        }
        new MainWindow(e.Args.FirstOrDefault()).Show();
    }

    void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "Folio", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
