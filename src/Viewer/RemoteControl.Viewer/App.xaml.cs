using System.Windows;
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
    }
}
