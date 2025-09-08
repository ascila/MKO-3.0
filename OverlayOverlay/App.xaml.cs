using System.Windows;
using System;

namespace OverlayOverlay;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Mostrar cualquier excepción no controlada durante el arranque
        this.DispatcherUnhandledException += (s, ex) =>
        {
            try { MessageBox.Show(ex.Exception.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error); }
            catch { }
        };
        AppDomain.CurrentDomain.UnhandledException += (s, ex2) =>
        {
            try { MessageBox.Show(ex2.ExceptionObject.ToString(), "Error", MessageBoxButton.OK, MessageBoxImage.Error); }
            catch { }
        };
        base.OnStartup(e);
    }
}
