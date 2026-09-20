using System.Configuration;

namespace RemoteControl.Viewer.Configuration;

/// <summary>
/// Настройки консоли из App.config (позже заменить на UI + хранение через DPAPI).
/// </summary>
public static class AppSettings
{
    private static string Get(string key, string fallback = "")
    {
        try
        {
            return ConfigurationManager.AppSettings[key] ?? fallback;
        }
        catch (ConfigurationErrorsException)
        {
            return fallback;
        }
    }

    public static string InternalRootCaThumbprint => Get("InternalRootCaThumbprint");

    public static int AgentPort
    {
        get
        {
            string value = Get("AgentPort", "5900");
            return int.TryParse(value, out int port) ? port : 5900;
        }
    }

    public static string AgentExePath => Get("AgentExePath", "RemoteControlAgent.exe");

    public static bool PushAgentByDefault =>
        Get("PushAgentByDefault", "true").Equals("true", StringComparison.OrdinalIgnoreCase);
}
