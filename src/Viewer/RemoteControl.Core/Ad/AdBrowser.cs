using System.DirectoryServices;

namespace RemoteControl.Core.Ad;

/// <summary>Компьютер домена, найденный через LDAP.</summary>
public sealed record AdComputer(
    string Name,
    string DnsHostName,
    string OperatingSystem,
    DateTime? LastLogon);

/// <summary>
/// Поиск Windows-компьютеров в Active Directory. Использует текущий Kerberos-тикет
/// пользователя — без ввода пароля (SSO).
/// </summary>
public static class AdBrowser
{
    /// <summary>Возвращает включённые Windows-компьютеры домена.</summary>
    public static IReadOnlyList<AdComputer> FindWindowsComputers(
        string? searchBase = null, int maxResults = 1000, CancellationToken ct = default)
    {
        using var root = new DirectoryEntry(EnsureLdapPath(searchBase ?? DefaultNamingContext()));
        using var searcher = new DirectorySearcher(root)
        {
            // objectCategory=computer, исключаем отключённые (userAccountControl & 2)
            Filter = "(&(objectCategory=computer)(!(userAccountControl:1.2.840.113556.1.4.803:=2)))",
            Sort = new SortOption("name", SortDirection.Ascending),
            SizeLimit = maxResults,
            PageSize = 500,
            CacheResults = false,
        };

        searcher.PropertiesToLoad.AddRange(new[]
        {
            "name", "dnsHostName", "operatingSystem", "lastLogonTimestamp",
        });

        var result = new List<AdComputer>();
        using var results = searcher.FindAll();
        foreach (SearchResult entry in results)
        {
            ct.ThrowIfCancellationRequested();
            result.Add(new AdComputer(
                Name: GetString(entry, "name"),
                DnsHostName: GetString(entry, "dnsHostName"),
                OperatingSystem: GetString(entry, "operatingSystem"),
                LastLogon: GetFileTime(entry, "lastLogonTimestamp")));
        }

        return result;
    }

    /// <summary>Возвращает FQDN машины по её NetBIOS/короткому имени через DNS-имя в AD.</summary>
    public static string? ResolveDnsHostName(string computerName, CancellationToken ct = default)
    {
        using var root = new DirectoryEntry(DefaultNamingContext());
        using var searcher = new DirectorySearcher(root)
        {
            Filter = $"(&(objectCategory=computer)(|(name={EscapeLdapFilter(computerName)})(dnsHostName={EscapeLdapFilter(computerName)})))",
            SizeLimit = 1,
            CacheResults = false,
        };
        searcher.PropertiesToLoad.Add("dnsHostName");

        using var results = searcher.FindAll();
        foreach (SearchResult entry in results)
        {
            ct.ThrowIfCancellationRequested();
            return GetString(entry, "dnsHostName");
        }
        return null;
    }

    private static string DefaultNamingContext()
    {
        using var rootDse = new DirectoryEntry("LDAP://RootDSE");
        // DirectoryEntry в .NET (Core) требует префикс "LDAP://" — без него Bind падает
        // с COMException 0x80004005 на serverless-пути "DC=...".
        return "LDAP://" + (string)rootDse.Properties["defaultNamingContext"].Value!;
    }

    private static string EnsureLdapPath(string path) =>
        path.Contains("://", StringComparison.Ordinal) ? path : "LDAP://" + path;

    private static string GetString(SearchResult entry, string property) =>
        entry.Properties[property].Count > 0
            ? entry.Properties[property][0]?.ToString() ?? string.Empty
            : string.Empty;

    private static DateTime? GetFileTime(SearchResult entry, string property)
    {
        if (entry.Properties[property].Count == 0)
            return null;

        object value = entry.Properties[property][0]!;
        if (value is long fileTime && fileTime > 0)
            return DateTime.FromFileTimeUtc(fileTime).ToLocalTime();
        return null;
    }

    /// <summary>Экранирование спецсимволов для LDAP-фильтра (RFC 4515).</summary>
    private static string EscapeLdapFilter(string value) =>
        value.Replace("\\", "\\5c")
             .Replace("*", "\\2a")
             .Replace("(", "\\28")
             .Replace(")", "\\29")
             .Replace("\0", "\\00");
}
