using System.IO;
using System.Windows;
using System.Windows.Threading;
using RemoteControl.Core.Security;
using RemoteControl.Viewer.Configuration;

[assembly: ThemeInfo(
    ResourceDictionaryLocation.None,
    ResourceDictionaryLocation.SourceAssembly)]

namespace RemoteControl.Viewer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        string thumbprint = AppSettings.InternalRootCaThumbprint;
        if (!string.IsNullOrWhiteSpace(thumbprint))
            TlsTransport.InternalRootCaThumbprint = thumbprint;

        // Ошибка в обработчике события (ввод, кадр) не должна ронять консоль:
        // пишем в лог рядом с exe и продолжаем работу.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogError("AppDomain", args.ExceptionObject as Exception);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogError("Dispatcher", e.Exception);
        e.Handled = true;
    }

    private static void LogError(string source, Exception? ex)
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "viewer-errors.log");
            File.AppendAllText(path,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{source}] {ex}{Environment.NewLine}");
        }
        catch
        {
            // логирование не должно мешать работе
        }
    }
}
