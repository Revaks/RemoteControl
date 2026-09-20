// Мини-CA для лаборатории RemoteControl (без роли AD CS).
//
// Что делает:
//   * при первом запуске создаёт самоподписанный корневой CA (LocalMachine\My + LocalMachine\Root);
//   * поднимает HTTP-эндпоинт (по умолчанию http://+:80/):
//       GET  /ca.cer  — корневой сертификат (DER)
//       GET  /ca.crl  — CRL (DER), пересобирается на каждый запрос
//       POST /enroll  — подпись PKCS#10 (JSON: kind, csr, names[] / upn)
//   * /enroll требует аутентификацию Windows (Negotiate/NTLM) и проверяет, что запрошенное
//     имя принадлежит вызывающему: для kind=machine это учётная запись компьютера (…$),
//     для kind=user — что UPN совпадает с вызывающим пользователем.
//
// Запуск (SYSTEM, задача планировщика при старте): labca.exe
// Параметры окружения: LABCA_PREFIX (по умолчанию http://+:80/),
//                      LABCA_PUBLIC_URL (по умолчанию http://dc1.corp.local/),
//                      LABCA_SUBJECT, LABCA_LOG.

using System.Diagnostics;
using System.Net;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace RemoteControl.LabCa;

internal static class Program
{
    // Анонимный слушатель: корневой сертификат и CRL (CRL обязан быть доступен без
    // аутентификации — иначе проверка отзыва в Schannel/.NET не сработает).
    private static readonly string Prefix =
        Environment.GetEnvironmentVariable("LABCA_PREFIX") ?? "http://+:80/";

    // Слушатель для /enroll: требует аутентификацию Windows (Negotiate/NTLM).
    private static readonly string EnrollPrefix =
        Environment.GetEnvironmentVariable("LABCA_ENROLL_PREFIX") ?? "http://+:8555/";

    // Публичный URL ЦС. По умолчанию — сам сервер (автономный режим для MSI):
    // в этот адрес попадают CDP/CRL и оттуда клиенты забирают корень.
    private static readonly string PublicUrl = ResolvePublicUrl();

    private static string ResolvePublicUrl()
    {
        string? fromEnv = Environment.GetEnvironmentVariable("LABCA_PUBLIC_URL");
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv.TrimEnd('/') + "/";
        try { return $"http://{System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName()).HostName}/"; }
        catch { return $"http://{Environment.MachineName}/"; }
    }

    private static readonly string CrlUrl = PublicUrl + "ca.crl";
    private static readonly string CaSubject =
        Environment.GetEnvironmentVariable("LABCA_SUBJECT") ?? "CN=RemoteControl Lab CA";
    private static readonly string LogPath =
        Environment.GetEnvironmentVariable("LABCA_LOG") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                     "RemoteControl", "labca.log");

    private static void Log(string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}";
        Console.WriteLine(line);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, line + Environment.NewLine);
        }
        catch { /* журнал не критичен */ }
    }

    private static int Main()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log("FATAL: " + e.ExceptionObject);

        X509Certificate2 caCert = EnsureCaCertificate(out RSA caKey);
        Log($"CA: {caCert.Subject} thumbprint={caCert.Thumbprint}");
        Log($"CRL URL: {CrlUrl}");
        Log($"Корень/CRL: {Prefix}");
        Log($"Enroll:    {EnrollPrefix}");

        HttpListener anon = StartListener(Prefix, AuthenticationSchemes.Anonymous, "anon");
        HttpListener auth = StartListener(EnrollPrefix, AuthenticationSchemes.Negotiate, "enroll");

        var enrollThread = new Thread(() => Serve(auth, ctx => HandleEnroll(ctx, caCert, caKey), "enroll"))
        {
            IsBackground = true,
        };
        enrollThread.Start();

        // Главный поток обслуживает анонимный слушатель (корень/CRL).
        Serve(anon, ctx => HandleAnonymous(ctx, caCert, caKey), "anon");
        return 0;
    }

    private static HttpListener StartListener(string prefix, AuthenticationSchemes schemes, string name)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.AuthenticationSchemes = schemes;
        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            Log($"{name}: HttpListener.Start({prefix}): {ex.Message} — добавляю urlacl и пробую снова");
            RunNetsh($"http add urlacl url={prefix} user=SYSTEM");
            listener.Start();
        }
        return listener;
    }

    private static void Serve(HttpListener listener, Action<HttpListenerContext> handler, string name)
    {
        while (true)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = listener.GetContext();
            }
            catch (Exception ex)
            {
                Log($"{name}: GetContext: {ex.Message}");
                continue;
            }

            try
            {
                handler(ctx);
            }
            catch (Exception ex)
            {
                Log($"{name}: ошибка обработки запроса: " + ex);
                TryFail(ctx, 500, ex.Message);
            }
            finally
            {
                try { ctx.Response.Close(); } catch { }
            }
        }
    }

    // ---------- маршрутизация ----------

    // Анонимный слушатель: только корневой сертификат, CRL и health.
    private static void HandleAnonymous(HttpListenerContext ctx, X509Certificate2 caCert, RSA caKey)
    {
        string path = (ctx.Request.Url?.AbsolutePath ?? "/").TrimEnd('/').ToLowerInvariant();
        string method = ctx.Request.HttpMethod;

        if (method == "GET" && (path == "/ca.cer" || path == "/ca.crt"))
        {
            byte[] der = caCert.Export(X509ContentType.Cert);
            ctx.Response.ContentType = "application/x-x509-ca-cert";
            ctx.Response.OutputStream.Write(der);
            return;
        }

        if (method == "GET" && path == "/ca.crl")
        {
            byte[] crl = BuildCrl(caCert, caKey);
            ctx.Response.ContentType = "application/pkix-crl";
            ctx.Response.OutputStream.Write(crl);
            return;
        }

        if (method == "GET" && path == "/health")
        {
            WriteText(ctx, "ok");
            return;
        }

        TryFail(ctx, 404, "not found");
    }

    private sealed record EnrollRequest(string? kind, string? csr, string[]? names, string? upn);
    private sealed record EnrollResponse(string? cert, string? error);

    private static void HandleEnroll(HttpListenerContext ctx, X509Certificate2 caCert, RSA caKey)
    {
        string path = (ctx.Request.Url?.AbsolutePath ?? "/").TrimEnd('/').ToLowerInvariant();
        if (ctx.Request.HttpMethod != "POST" || path != "/enroll")
        {
            TryFail(ctx, 404, "not found");
            return;
        }

        WindowsIdentity? identity = ctx.User?.Identity as WindowsIdentity;
        if (identity is null || !identity.IsAuthenticated)
        {
            Log("enroll: отказано — нет аутентификации");
            ctx.Response.AddHeader("WWW-Authenticate", "Negotiate");
            ctx.Response.AddHeader("WWW-Authenticate", "NTLM");
            TryFail(ctx, 401, "Windows authentication required");
            return;
        }

        string caller = identity.Name; // DOMAIN\account
        string account = caller.Contains('\\') ? caller[(caller.IndexOf('\\') + 1)..] : caller;

        // Локальный SYSTEM определяем по SID S-1-5-18: имя локализовано
        // (например, NT AUTHORITY\СИСТЕМА), поэтому строка "SYSTEM" не подходит.
        bool isLocalSystem = identity.User?.Value == "S-1-5-18";

        string body;
        using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
            body = reader.ReadToEnd();

        EnrollRequest? req;
        try
        {
            req = JsonSerializer.Deserialize<EnrollRequest>(body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            TryFail(ctx, 400, "bad json: " + ex.Message);
            return;
        }

        if (req?.csr is null || req.kind is null)
        {
            TryFail(ctx, 400, "kind and csr are required");
            return;
        }

        byte[] csrDer;
        try { csrDer = Convert.FromBase64String(req.csr); }
        catch { TryFail(ctx, 400, "csr must be base64 DER"); return; }

        try
        {
            X509Certificate2 signed = req.kind switch
            {
                "machine" => SignMachine(csrDer, req.names, caller, account, isLocalSystem, caCert, caKey),
                "user" => SignUser(csrDer, req.upn, account, caCert, caKey),
                _ => throw new InvalidOperationException("kind must be 'machine' or 'user'"),
            };

            Log($"enroll: {req.kind} от {caller} -> {signed.Subject} [{signed.Thumbprint}]");
            ctx.Response.ContentType = "application/json";
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(new EnrollResponse(
                Convert.ToBase64String(signed.Export(X509ContentType.Cert)), null));
            ctx.Response.OutputStream.Write(json);
        }
        catch (InvalidOperationException ex)
        {
            Log($"enroll: отказано {caller}: {ex.Message}");
            TryFail(ctx, 403, ex.Message);
        }
        catch (CryptographicException ex)
        {
            Log($"enroll: некорректный CSR от {caller}: {ex.Message}");
            TryFail(ctx, 400, "invalid CSR: " + ex.Message);
        }
    }

    // ---------- подпись ----------

    private static X509Certificate2 SignMachine(byte[] csrDer, string[]? names, string caller, string account,
        bool isLocalSystem, X509Certificate2 caCert, RSA caKey)
    {
        if (names is null || names.Length == 0 || string.IsNullOrWhiteSpace(names[0]))
            throw new InvalidOperationException("names is required for machine");

        // Запрос от локального SYSTEM (например, enrollment на самом ЦС) приходит как
        // NT AUTHORITY\СИСТЕМА (имя локализовано), а не как учётная запись компьютера.
        // Тогда имя должно совпадать с именем машины, где работает ЦС.
        string machineShort;
        if (isLocalSystem)
            machineShort = Environment.MachineName;
        else if (account.EndsWith('$'))
            machineShort = account.TrimEnd('$');
        else
            throw new InvalidOperationException("запрос машинного сертификата должен идти от учётной записи компьютера или локального SYSTEM");

        string primaryShort = names[0].Split('.')[0];
        if (!primaryShort.Equals(machineShort, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"имя '{names[0]}' не принадлежит вызывающему компьютеру {machineShort}");

        var san = new SubjectAlternativeNameBuilder();
        foreach (string n in names)
        {
            if (IPAddress.TryParse(n, out IPAddress? ip)) san.AddIpAddress(ip);
            else san.AddDnsName(n);
        }

        return CreateCertificate(csrDer, "CN=" + EscapeCn(names[0]), san,
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            "1.3.6.1.5.5.7.3.1" /* Server Auth */, caCert, caKey);
    }

    private static X509Certificate2 SignUser(byte[] csrDer, string? upn, string account,
        X509Certificate2 caCert, RSA caKey)
    {
        if (string.IsNullOrWhiteSpace(upn) || !upn.Contains('@'))
            throw new InvalidOperationException("upn is required for user");

        string accountShort = account.TrimEnd('$');
        string upnShort = upn.Split('@')[0];
        if (!upnShort.Equals(accountShort, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"UPN '{upn}' не совпадает с вызывающим пользователем {accountShort}");

        var san = new SubjectAlternativeNameBuilder();
        san.AddUserPrincipalName(upn);

        return CreateCertificate(csrDer, "CN=" + EscapeCn(upnShort), san,
            X509KeyUsageFlags.DigitalSignature,
            "1.3.6.1.5.5.7.3.2" /* Client Auth */, caCert, caKey);
    }

    private static X509Certificate2 CreateCertificate(byte[] csrDer, string subject,
        SubjectAlternativeNameBuilder san, X509KeyUsageFlags keyUsage, string ekuOid,
        X509Certificate2 caCert, RSA caKey)
    {
        CertificateRequest loaded = CertificateRequest.LoadSigningRequest(csrDer, HashAlgorithmName.SHA256);

        var req = new CertificateRequest(new X500DistinguishedName(subject), loaded.PublicKey, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(keyUsage, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid(ekuOid) }, false));
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(BuildCdp(CrlUrl));
        req.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(caCert, true, false));

        byte[] serial = RandomNumberGenerator.GetBytes(16);
        return req.Create(caCert.SubjectName,
            X509SignatureGenerator.CreateForRSA(caKey, RSASignaturePadding.Pkcs1),
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddYears(2),
            serial);
    }

    // ---------- корневой CA и CRL ----------

    private static X509Certificate2 EnsureCaCertificate(out RSA caKey)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);

        foreach (X509Certificate2 c in store.Certificates)
        {
            if (c.Subject == CaSubject && c.HasPrivateKey)
            {
                caKey = c.GetRSAPrivateKey()
                    ?? throw new InvalidOperationException("у CA-сертификата нет RSA-ключа");
                Log("Использую существующий CA-сертификат");
                return new X509Certificate2(c.Export(X509ContentType.Cert));
            }
        }

        Log("Создаю корневой CA (RSA-4096, 10 лет)");
        using RSA rsa = RSA.Create(4096);
        var req = new CertificateRequest(CaSubject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        req.CertificateExtensions.Add(BuildCdp(CrlUrl));

        using X509Certificate2 self = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(10));

        // CreateSelfSigned уже возвращает сертификат С ключом (CopyWithPrivateKey не нужен).
        // Переимпорт через PFX с MachineKeySet|PersistKeySet — чтобы ключ пережил перезапуск.
        const string pfxPassword = "labca-persist";
        byte[] pfx = self.Export(X509ContentType.Pfx, pfxPassword);
        var persisted = new X509Certificate2(pfx, pfxPassword,
            X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
        if (OperatingSystem.IsWindows())
            persisted.FriendlyName = "RemoteControl Lab CA";
        store.Add(persisted);
        GrantSystemKeyAccess(persisted);

        using (var rootStore = new X509Store(StoreName.Root, StoreLocation.LocalMachine))
        {
            rootStore.Open(OpenFlags.ReadWrite);
            rootStore.Add(new X509Certificate2(self.Export(X509ContentType.Cert)));
            rootStore.Close();
        }

        caKey = persisted.GetRSAPrivateKey()
            ?? throw new InvalidOperationException("не удалось получить ключ созданного CA");
        return new X509Certificate2(self.Export(X509ContentType.Cert));
    }

    private static byte[] BuildCrl(X509Certificate2 caCert, RSA caKey)
    {
        var builder = new CertificateRevocationListBuilder();
        X509AuthorityKeyIdentifierExtension aki =
            X509AuthorityKeyIdentifierExtension.CreateFromCertificate(caCert, true, false);
        return builder.Build(
            caCert.SubjectName,
            X509SignatureGenerator.CreateForRSA(caKey, RSASignaturePadding.Pkcs1),
            BigInteger.One,
            DateTimeOffset.UtcNow.AddDays(7),
            HashAlgorithmName.SHA256,
            aki,
            DateTimeOffset.UtcNow.AddMinutes(-5));
    }

    // ---------- вспомогательное ----------

    private static byte[] DerLen(int len)
    {
        if (len < 0x80) return new[] { (byte)len };
        if (len <= 0xFF) return new byte[] { 0x81, (byte)len };
        return new byte[] { 0x82, (byte)(len >> 8), (byte)len };
    }

    private static byte[] DerTlv(byte tag, byte[] content)
    {
        byte[] len = DerLen(content.Length);
        var buf = new byte[1 + len.Length + content.Length];
        buf[0] = tag;
        Buffer.BlockCopy(len, 0, buf, 1, len.Length);
        Buffer.BlockCopy(content, 0, buf, 1 + len.Length, content.Length);
        return buf;
    }

    private static byte[] DerSeq(params byte[][] items)
    {
        int total = items.Sum(x => x.Length);
        var content = new byte[total];
        int o = 0;
        foreach (byte[] item in items) { Buffer.BlockCopy(item, 0, content, o, item.Length); o += item.Length; }
        return DerTlv(0x30, content);
    }

    // CDP: DistributionPoint ::= SEQUENCE { distributionPoint [0] { fullName [0] { [6] url } } }
    private static X509Extension BuildCdp(string url)
    {
        byte[] uri = DerTlv(0x86, Encoding.ASCII.GetBytes(url));
        return new X509Extension("2.5.29.31",
            DerSeq(DerSeq(DerTlv(0xA0, DerTlv(0xA0, uri)))), false);
    }

    private static string EscapeCn(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char ch in name)
        {
            if (ch is ',' or '+' or '"' or '\\' or '<' or '>' or ';' or '=') sb.Append('\\');
            sb.Append(ch);
        }
        return sb.ToString();
    }

    private static void GrantSystemKeyAccess(X509Certificate2 cert)
    {
        try
        {
            if (cert.GetRSAPrivateKey() is RSACng cng && !string.IsNullOrEmpty(cng.Key.UniqueName))
            {
                string keyFile = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "Microsoft", "Crypto", "Keys", cng.Key.UniqueName);
                if (File.Exists(keyFile)) RunNetsh($"\"{keyFile}\" /grant *S-1-5-18:(F)", "icacls.exe");
            }
        }
        catch (Exception ex)
        {
            Log("GrantSystemKeyAccess: " + ex.Message);
        }
    }

    private static void RunNetsh(string args, string exe = "netsh.exe")
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            p?.WaitForExit();
        }
        catch (Exception ex)
        {
            Log($"{exe} {args}: {ex.Message}");
        }
    }

    private static void WriteText(HttpListenerContext ctx, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        ctx.Response.ContentType = "text/plain; charset=utf-8";
        ctx.Response.OutputStream.Write(bytes);
    }

    private static void TryFail(HttpListenerContext ctx, int status, string message)
    {
        try
        {
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(new EnrollResponse(null, message));
            ctx.Response.OutputStream.Write(json);
        }
        catch { }
    }
}
