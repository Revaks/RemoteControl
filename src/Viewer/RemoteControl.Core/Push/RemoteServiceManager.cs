using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RemoteControl.Core.Push;

/// <summary>
/// Управление службами на удалённой машине через Service Control Manager (P/Invoke).
/// Использует текущий сеанс оператора (Kerberos/NTLM), как DameWare.
/// </summary>
public sealed class RemoteServiceManager : IDisposable
{
    private const uint SC_MANAGER_CONNECT        = 0x0001;
    private const uint SC_MANAGER_CREATE_SERVICE = 0x0002;
    private const uint SERVICE_QUERY_STATUS      = 0x0004;
    private const uint SERVICE_START             = 0x0010;
    private const uint SERVICE_STOP              = 0x0020;
    private const uint DELETE                     = 0x00010000;

    private const uint SERVICE_WIN32_OWN_PROCESS  = 0x00000010;
    private const uint SERVICE_DEMAND_START       = 0x00000003;
    private const uint SERVICE_ERROR_NORMAL       = 0x00000001;

    private const uint SERVICE_CONTROL_STOP       = 0x00000001;
    private const uint SERVICE_STOPPED            = 0x00000001;
    private const uint SERVICE_START_PENDING      = 0x00000002;
    private const uint SERVICE_STOP_PENDING       = 0x00000003;
    private const uint SERVICE_RUNNING            = 0x00000004;

    private readonly string _host;
    private readonly IntPtr _scm;
    private IntPtr _service;
    private bool _disposed;

    public RemoteServiceManager(string host)
    {
        _host = host;
        _scm = OpenSCManagerW(host, null, SC_MANAGER_CONNECT | SC_MANAGER_CREATE_SERVICE);
        if (_scm == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Не удалось открыть SCM на '{host}'.");
    }

    public bool ServiceExists(string serviceName)
    {
        IntPtr svc = OpenServiceW(_scm, serviceName, SERVICE_QUERY_STATUS);
        if (svc == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            if (err == 1060 /* ERROR_SERVICE_DOES_NOT_EXIST */)
                return false;
            throw new Win32Exception(err, $"Не удалось проверить службу '{serviceName}'.");
        }
        CloseServiceHandle(svc);
        return true;
    }

    /// <summary>Создаёт и запускает службу агента (от LocalSystem, запуск по требованию).</summary>
    public void CreateAndStart(string serviceName, string displayName, string remoteBinaryPath)
    {
        if (_service != IntPtr.Zero)
            throw new InvalidOperationException("Служба уже создана.");

        _service = CreateServiceW(
            _scm,
            serviceName,
            displayName,
            SERVICE_QUERY_STATUS | SERVICE_START | SERVICE_STOP | DELETE,
            SERVICE_WIN32_OWN_PROCESS,
            SERVICE_DEMAND_START,
            SERVICE_ERROR_NORMAL,
            remoteBinaryPath,
            null, null, null, null, null);

        if (_service == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            if (err == 1073 /* ERROR_SERVICE_EXISTS */)
            {
                _service = OpenServiceW(_scm, serviceName, SERVICE_QUERY_STATUS | SERVICE_START | SERVICE_STOP | DELETE);
                if (_service == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Служба существует, но открыть её не удалось.");
            }
            else
            {
                throw new Win32Exception(err, $"Не удалось создать службу '{serviceName}'.");
            }
        }

        if (!StartServiceW(_service, 0, null))
        {
            int err = Marshal.GetLastWin32Error();
            if (err != 1056 /* ERROR_SERVICE_ALREADY_RUNNING */)
                throw new Win32Exception(err, $"Не удалось запустить службу '{serviceName}'.");
        }
    }

    /// <summary>Ожидает перехода службы в состояние <paramref name="targetState"/>.</summary>
    public void WaitForState(uint targetState, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var status = Query();
            if (status.dwCurrentState == targetState)
                return;
            if (targetState == SERVICE_RUNNING && status.dwCurrentState != SERVICE_START_PENDING)
                throw new Win32Exception((int)status.dwWin32ExitCode, "Служба не перешла в состояние Running.");
            if (targetState == SERVICE_STOPPED && status.dwCurrentState != SERVICE_STOP_PENDING)
                throw new Win32Exception((int)status.dwWin32ExitCode, "Служба не перешла в состояние Stopped.");

            Thread.Sleep(200);
        }
        throw new TimeoutException($"Служба не сменила состояние за {timeout}.");
    }

    public SERVICE_STATUS Query()
    {
        var status = new SERVICE_STATUS();
        if (!QueryServiceStatus(_service, ref status))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось опросить состояние службы.");
        return status;
    }

    /// <summary>Останавливает и удаляет службу.</summary>
    public void StopAndDelete()
    {
        if (_service == IntPtr.Zero)
            return;

        var status = Query();
        if (status.dwCurrentState == SERVICE_RUNNING || status.dwCurrentState == SERVICE_START_PENDING)
        {
            var st = new SERVICE_STATUS();
            if (!ControlService(_service, SERVICE_CONTROL_STOP, ref st))
            {
                int err = Marshal.GetLastWin32Error();
                if (err != 1062 /* ERROR_SERVICE_NOT_ACTIVE */)
                    throw new Win32Exception(err, "Не удалось остановить службу.");
            }
            WaitForState(SERVICE_STOPPED, TimeSpan.FromSeconds(10));
        }

        if (!DeleteService(_service))
        {
            int err = Marshal.GetLastWin32Error();
            if (err != 1072 /* ERROR_SERVICE_MARKED_FOR_DELETE */)
                throw new Win32Exception(err, "Не удалось удалить службу.");
        }

        CloseServiceHandle(_service);
        _service = IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_service != IntPtr.Zero)
        {
            CloseServiceHandle(_service);
            _service = IntPtr.Zero;
        }
        if (_scm != IntPtr.Zero)
            CloseServiceHandle(_scm);
    }

    public static void DeleteRemoteFiles(string host, params string[] remotePaths)
    {
        foreach (string remote in remotePaths)
        {
            string unc = $@"\\{host}\admin$\{remote.TrimStart('\\')}";
            try
            {
                if (File.Exists(unc))
                    File.Delete(unc);
            }
            catch (IOException)
            {
                // файл может быть заблокирован службой; удалим при следующем подключении
            }
            catch (UnauthorizedAccessException)
            {
                // нет прав — не критично
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SERVICE_STATUS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManagerW(string? lpMachineName, string? lpDatabaseName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateServiceW(
        IntPtr hSCManager, string lpServiceName, string lpDisplayName, uint dwDesiredAccess,
        uint dwServiceType, uint dwStartType, uint dwErrorControl, string lpBinaryPathName,
        string? lpLoadOrderGroup, string? lpdwTagId, string? lpDependencies,
        string? lpServiceStartName, string? lpPassword);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenServiceW(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceW(IntPtr hService, uint dwNumServiceArgs, string[]? lpServiceArgVectors);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(IntPtr hService, uint dwControl, ref SERVICE_STATUS lpServiceStatus);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatus(IntPtr hService, ref SERVICE_STATUS lpServiceStatus);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteService(IntPtr hService);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr hSCObject);
}
