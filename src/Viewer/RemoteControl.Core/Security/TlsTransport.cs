using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RemoteControl.Core.Security;

/// <summary>
/// Установка защищённого соединения с агентом: TLS 1.3 (fallback 1.2) с взаимной
/// аутентификацией по сертификатам, выпущенным внутренним центром сертификации (AD CS).
/// </summary>
public static class TlsTransport
{
    /// <summary>
    /// Разрешённые наборы шифров: только AEAD (современные). На Windows наборы TLS 1.3
    /// управляются Schannel автоматически, CipherSuitesPolicy ограничивает TLS 1.2.
    /// </summary>
    public static readonly TlsCipherSuite[] AllowedCipherSuites =
    {
        TlsCipherSuite.TLS_AES_256_GCM_SHA384,
        TlsCipherSuite.TLS_AES_128_GCM_SHA256,
        TlsCipherSuite.TLS_CHACHA20_POLY1305_SHA256,
        TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384,
        TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384,
        TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256,
        TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256,
    };

    /// <summary>Отпечаток (SHA-256) корневого сертификата внутреннего ЦС; null — принимать любой корень из доверенных.</summary>
    public static string? InternalRootCaThumbprint { get; set; }

    /// <summary>Имя хоста, которое разрешено в SAN сертификата агента (null — не проверять).</summary>
    public static Func<string, bool>? ServerNameValidator { get; set; }

    /// <summary>Создаёт SslStream с mTLS и возвращает его поверх TCP-соединения.</summary>
    public static async Task<SslStream> ConnectAsync(
        string host, int port, X509Certificate2 clientCertificate, CancellationToken ct)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port, ct).ConfigureAwait(false);

        var ssl = new SslStream(
            tcp.GetStream(),
            leaveInnerStreamOpen: false,
            ValidateServerCertificate,
            userCertificateSelectionCallback: null,
            EncryptionPolicy.RequireEncryption);

        var options = new SslClientAuthenticationOptions
        {
            TargetHost = host,
            ClientCertificates = new X509CertificateCollection { clientCertificate },
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.Online,
        };

        // CipherSuitesPolicy поддерживается только на Linux/macOS; на Windows
        // наборами TLS 1.2 управляет Schannel (см. комментарий выше).
        if (!OperatingSystem.IsWindows())
        {
            options.CipherSuitesPolicy = new CipherSuitesPolicy(AllowedCipherSuites);
        }

        try
        {
            await ssl.AuthenticateAsClientAsync(options, ct).ConfigureAwait(false);
        }
        catch
        {
            ssl.Dispose();
            tcp.Dispose();
            throw;
        }

        // Контролируем, что согласован действительно современный набор.
        if (ssl.NegotiatedCipherSuite != TlsCipherSuite.TLS_AES_256_GCM_SHA384 &&
            ssl.NegotiatedCipherSuite != TlsCipherSuite.TLS_AES_128_GCM_SHA256 &&
            ssl.NegotiatedCipherSuite != TlsCipherSuite.TLS_CHACHA20_POLY1305_SHA256 &&
            ssl.NegotiatedCipherSuite != TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384 &&
            ssl.NegotiatedCipherSuite != TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384 &&
            ssl.NegotiatedCipherSuite != TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256 &&
            ssl.NegotiatedCipherSuite != TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256)
        {
            throw new AuthenticationException($"Согласован недопустимый набор шифров: {ssl.NegotiatedCipherSuite}");
        }

        return ssl;
    }

    /// <summary>
    /// Проверка сертификата агента: цепочка до внутреннего корневого ЦС, EKU Server Authentication
    /// и (опционально) имя хоста через ServerNameValidator.
    /// </summary>
    private static bool ValidateServerCertificate(
        object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (certificate is not X509Certificate2 cert2)
            return false;

        // Самоподписанный сертификат или неизвестный ЦС — не принимаем.
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors))
            return false;

        if (!string.IsNullOrEmpty(InternalRootCaThumbprint))
        {
            bool rootOk = chain?.ChainElements
                .Cast<X509ChainElement>()
                .Any(e => e.Certificate.Thumbprint.Equals(InternalRootCaThumbprint, StringComparison.OrdinalIgnoreCase))
                ?? false;
            if (!rootOk)
                return false;
        }

        // EKU: Server Authentication (1.3.6.1.5.5.7.3.1)
        foreach (var eku in cert2.Extensions.OfType<X509EnhancedKeyUsageExtension>())
        {
            bool hasServerAuth = eku.EnhancedKeyUsages
                .Cast<Oid>()
                .Any(o => o.Value == "1.3.6.1.5.5.7.3.1");
            if (!hasServerAuth)
                return false;
        }

        // Имя: стандартная проверка SslStream уже выполнена (errors не содержит RemoteCertificateNameMismatch,
        // значит имя совпало). ServerNameValidator — дополнительное ограничение (например, только *.corp.local).
        if (ServerNameValidator is not null)
        {
            string host = ((SslStream)sender).TargetHostName
                          ?? cert2.GetNameInfo(X509NameType.DnsName, false);
            if (!ServerNameValidator(host))
                return false;
        }

        return true;
    }
}
