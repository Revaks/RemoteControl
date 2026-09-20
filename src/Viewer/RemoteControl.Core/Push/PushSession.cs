using System.Net.Sockets;
using RemoteControl.Core.Push;

namespace RemoteControl.Core.Push;

/// <summary>
/// Push-развёртывание агента по образцу DameWare:
/// копирование через ADMIN$ → создание службы → запуск → ожидание порта → (после сеанса) удаление.
/// </summary>
public sealed class PushSession : IAsyncDisposable
{
    public const string ServiceName = "RemoteControlAgent";
    public const string ServiceDisplayName = "Remote Control Agent";
    public const string RemoteDirectory = "Windows\\Temp";
    public const string RemoteExeName = "RemoteControlAgent.exe";

    private readonly string _host;
    private readonly int _port;
    private readonly RemoteServiceManager _scm;
    private bool _cleanupDone;

    public PushSession(string host, int port = 5900)
    {
        _host = host;
        _port = port;
        _scm = new RemoteServiceManager(host);
    }

    public string RemoteBinaryPath => $@"C:\{RemoteDirectory}\{RemoteExeName}";

    // admin$ указывает на C:\Windows, поэтому через UNC подкаталог — только Temp.
    public string UncAdminSharePath => $@"\\{_host}\admin$\Temp\{RemoteExeName}";

    /// <summary>Копирует агент и запускает службу. agentExePath — локальный путь к собранному агенту.</summary>
    public async Task DeployAsync(string agentExePath, TimeSpan? startTimeout = null, CancellationToken ct = default)
    {
        // Если агент уже установлен (например, единым MSI), его служба уже есть: не подменяем
        // файл и не создаём свою — иначе CleanupAsync удалит установленную службу и оставит
        // машину без агента. Просто убеждаемся, что служба запущена и порт открыт.
        if (_scm.ServiceExists(ServiceName))
        {
            _scm.StartExisting(ServiceName);
            await WaitForPortAsync(_port, startTimeout ?? TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
            return;
        }

        if (!File.Exists(agentExePath))
            throw new FileNotFoundException("Не найден бинарник агента.", agentExePath);

        // 1. Копирование через ADMIN$ (права локального администратора, SMB 445).
        string unc = UncAdminSharePath;
        await Task.Run(() => File.Copy(agentExePath, unc, overwrite: true), ct).ConfigureAwait(false);

        // 2. Создание и запуск службы.
        _scm.CreateAndStart(ServiceName, ServiceDisplayName, RemoteBinaryPath);

        // 3. Ждём открытия порта агента.
        await WaitForPortAsync(_port, startTimeout ?? TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Останавливает и удаляет службу с файлами агента — но только если их поставили МЫ.
    /// Установленный агент (MSI) при завершении сеанса не трогаем.
    /// </summary>
    public async Task CleanupAsync()
    {
        if (_cleanupDone)
            return;

        await Task.Run(() =>
        {
            try
            {
                if (!_scm.CreatedByUs)
                    return;

                _scm.StopAndDelete();
                RemoteServiceManager.DeleteRemoteFiles(_host, $@"Temp\{RemoteExeName}");
            }
            finally
            {
                _cleanupDone = true;
            }
        }).ConfigureAwait(false);
    }

    private async Task WaitForPortAsync(int port, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            using var tcp = new TcpClient();
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromMilliseconds(500));
                await tcp.ConnectAsync(_host, port, cts.Token).ConfigureAwait(false);
                return; // порт открыт — агент слушает
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // таймаут попытки — пробуем ещё
            }
            catch (SocketException)
            {
                // порт ещё закрыт — пробуем ещё
            }
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
        throw new TimeoutException($"Агент не открыл порт {port} за {timeout}.");
    }

    public async ValueTask DisposeAsync()
    {
        try { await CleanupAsync().ConfigureAwait(false); }
        finally { _scm.Dispose(); }
    }
}
