using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace RoundedTB
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            WPFUI.Theme.Watcher.Start();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // Backstop for every exit route. The title bar's close button calls
            // Application.Shutdown() itself, which means MainWindow.OnClosing runs with
            // shouldReallyDieNoReally still false and skips its cleanup - leaving the
            // taskbar clipped after the process is gone. RestoreTaskbars is idempotent,
            // so the tray menu's "Close RoundedTB" path is unaffected.
            // Qualified: inside App, the bare name "MainWindow" binds to the inherited
            // Application.MainWindow property rather than to the RoundedTB.MainWindow type.
            RoundedTB.MainWindow.Instance?.RestoreTaskbars();

            base.OnExit(e);
        }
    }
}
