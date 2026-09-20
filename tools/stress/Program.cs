using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RemoteControl.Core.Rfb;
using RemoteControl.Core.Security;

static X509Certificate2? FindClientCertificate()
{
    using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
    store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
    foreach (var cert in store.Certificates)
    {
        if (!cert.HasPrivateKey) continue;
        bool hasClientAuth = cert.Extensions
            .OfType<X509EnhancedKeyUsageExtension>()
            .SelectMany(eku => eku.EnhancedKeyUsages.Cast<Oid>())
            .Any(oid => oid.Value == "1.3.6.1.5.5.7.3.2");
        if (hasClientAuth) return new X509Certificate2(cert);
    }
    return null;
}

try
{
    var cert = FindClientCertificate();
    if (cert is null) { Console.WriteLine("FAIL: no client cert"); return 1; }

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    var ssl = await TlsTransport.ConnectAsync("192.168.122.10", 5900, cert, cts.Token);
    var client = new RfbClient(ssl);
    await client.ConnectAsync(cts.Token);
    Console.WriteLine($"connected: {client.Framebuffer.Width}x{client.Framebuffer.Height}");

    int ok = 0, failed = 0;
    var errors = new System.Collections.Concurrent.ConcurrentBag<string>();

    // Стресс: параллельные pointer/key события — раньше это валило SslStream
    // («This method may not be called when another write operation is pending»).
    var tasks = new List<Task>();
    for (int i = 0; i < 300; i++)
    {
        int x = 100 + (i % 500);
        int y = 100 + (i % 400);
        tasks.Add(Task.Run(async () =>
        {
            try
            {
                if (i % 3 == 0)
                    await client.SendKeyEventAsync(0x0061, down: i % 2 == 0, CancellationToken.None);
                else
                    await client.SendPointerEventAsync((byte)(i % 8), (ushort)x, (ushort)y, CancellationToken.None);
                Interlocked.Increment(ref ok);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failed);
                errors.Add($"{ex.GetType().Name}: {ex.Message}");
            }
        }));
    }

    await Task.WhenAll(tasks);
    Console.WriteLine($"ok={ok} failed={failed}");
    foreach (var e in errors.Distinct().Take(5))
        Console.WriteLine($"  ERR {e}");

    await client.RequestUpdateAsync(incremental: false, CancellationToken.None);
    await Task.Delay(2000);
    Console.WriteLine($"still connected: {client.IsConnected}");
    await client.DisposeAsync();
    return failed == 0 ? 0 : 2;
}
catch (Exception ex)
{
    Console.WriteLine($"FAIL: {ex.GetType().Name}: {ex.Message}");
    return 3;
}
