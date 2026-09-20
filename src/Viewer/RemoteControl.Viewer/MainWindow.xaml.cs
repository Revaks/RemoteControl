using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RemoteControl.Core.Ad;
using RemoteControl.Core.Push;
using RemoteControl.Core.Rfb;
using RemoteControl.Core.Security;
using RemoteControl.Viewer.Configuration;

namespace RemoteControl.Viewer;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<AdComputer> _computers = new();

    private RfbClient? _client;
    private PushSession? _push;
    private WriteableBitmap? _bitmap;
    private byte _buttonMask;
    private bool _mouseCaptured;
    private int _port;
    private string _connectedHost = string.Empty;

    // MouseMove приходит десятками событий в секунду, а запись в поток
    // сериализуется (SslStream не терпит параллельных Write). Семафор гасит
    // лишние кадры, чтобы не копить очередь и не перегружать канал.
    private readonly SemaphoreSlim _pointerLock = new(1, 1);

    // Клавиатура: низкоуровневый хук + очередь, чтобы порядок нажатий не терялся
    // и колбэк хука не тормозил (иначе Windows снимет хук по таймауту).
    private const uint SasKeysym = 0xFFFFFF00; // Ctrl+Alt+Del на удалённой машине
    private KeyboardHook? _keyboardHook;
    private Channel<(uint Keysym, bool Down)>? _keyChannel;
    private Task? _keyPump;

    // HWND окна консоли: по нему проверяем, что хук должен перехватывать ввод
    // (GetForegroundWindow() == наше окно), иначе клавиши уйдут в чужое приложение.
    private IntPtr _hwnd;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    // Диагностика клавиатуры (App.config: KeyboardTrace=true) — пишем в
    // keyboard-trace.log рядом с exe, чтобы разбирать «клавиша не дошла».
    private static readonly bool KeyTrace = AppSettings.KeyboardTrace;
    private static readonly object TraceLock = new();

    private static void TraceKey(string message)
    {
        if (!KeyTrace)
            return;

        try
        {
            lock (TraceLock)
                File.AppendAllText(
                    Path.Combine(AppContext.BaseDirectory, "keyboard-trace.log"),
                    $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // трассировка не должна мешать работе
        }
    }

    public MainWindow()
    {
        InitializeComponent();

        _port = AppSettings.AgentPort;
        PushCheck.IsChecked = AppSettings.PushAgentByDefault;
        ComputerList.ItemsSource = _computers;

        Loaded += async (_, _) => await RefreshComputersAsync();
        Closing += async (_, _) => await DisconnectAsync();
    }

    // ---------- AD-список ----------

    private async Task RefreshComputersAsync()
    {
        try
        {
            StatusText.Text = "Загрузка списка компьютеров из AD...";
            IReadOnlyList<AdComputer> list = await Task.Run(
                () => AdBrowser.FindWindowsComputers(maxResults: 2000));

            _computers.Clear();
            foreach (AdComputer computer in list)
                _computers.Add(computer);

            StatusText.Text = $"Компьютеров в домене: {_computers.Count}";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Ошибка загрузки списка из AD";
            MessageBox.Show(this, ex.Message, "Ошибка AD", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        _ = RefreshComputersAsync();

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ICollectionView view = CollectionViewSource.GetDefaultView(ComputerList.ItemsSource);
        view.Filter = o =>
            string.IsNullOrWhiteSpace(SearchBox.Text) ||
            ((AdComputer)o).Name.Contains(SearchBox.Text, StringComparison.OrdinalIgnoreCase);
    }

    private void ComputerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ComputerList.SelectedItem is AdComputer computer &&
            string.IsNullOrWhiteSpace(HostBox.Text))
        {
            HostBox.Text = computer.DnsHostName;
        }
    }

    private void ComputerList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ComputerList.SelectedItem is AdComputer computer)
        {
            HostBox.Text = computer.DnsHostName;
            _ = ConnectAsync(computer.DnsHostName);
        }
    }

    private void HostBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            _ = ConnectAsync(HostBox.Text.Trim());
    }

    // ---------- Подключение ----------

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        string host = HostBox.Text.Trim();
        if (host.Length == 0 && ComputerList.SelectedItem is AdComputer computer)
            host = computer.DnsHostName;

        if (host.Length == 0)
        {
            MessageBox.Show(this, "Укажите имя компьютера.", "Подключение",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await ConnectAsync(host);
    }

    private async Task ConnectAsync(string host)
    {
        try
        {
            SetBusy(true);
            StatusText.Text = $"Подключение к {host}...";
            _connectedHost = host;

            // Режим DameWare: скопировать агент через ADMIN$ и запустить службу.
            if (PushCheck.IsChecked == true)
            {
                _push = new PushSession(host, _port);
                await _push.DeployAsync(AppSettings.AgentExePath);
                StatusText.Text = "Агент развёрнут, устанавливаю TLS...";
            }

            // Клиентский сертификат оператора (выпущен AD CS, EKU Client Authentication).
            X509Certificate2 clientCert = FindClientCertificate()
                ?? throw new InvalidOperationException(
                    "Не найден клиентский сертификат в личном хранилище (CurrentUser\\My). " +
                    "Настройте авторегистрацию сертификатов через AD CS (шаблон Client Authentication).");

            SslStream ssl = await TlsTransport.ConnectAsync(host, _port, clientCert, CancellationToken.None);

            var client = new RfbClient(ssl);
            client.FrameUpdated += OnFrameUpdated;
            client.Disconnected += OnDisconnected;
            await client.ConnectAsync(CancellationToken.None);
            await client.RequestUpdateAsync(incremental: false, CancellationToken.None);
            _client = client;

            _bitmap = new WriteableBitmap(
                client.Framebuffer.Width, client.Framebuffer.Height, 96, 96,
                PixelFormats.Pbgra32, null);
            ScreenImage.Source = _bitmap;
            ResolutionText.Text = $"{client.Framebuffer.Width}x{client.Framebuffer.Height}";
            StatusText.Text = $"Подключено: {client.DesktopName} (TLS: {ssl.NegotiatedCipherSuite}, {ssl.SslProtocol})";

            ConnectButton.IsEnabled = false;
            DisconnectButton.IsEnabled = true;
            ScreenHost.Focus();

            // Перехватываем клавиатуру хуком: иначе Win/Alt+Tab срабатывают
            // и локально, и на удалённой машине.
            StartInputCapture(client);
            StatusText.Text += " | клавиатура захвачена (Ctrl+Alt+Shift — локально, Ctrl+Alt+End — Ctrl+Alt+Del)";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Ошибка подключения";
            MessageBox.Show(this, ex.ToString(), "Не удалось подключиться",
                MessageBoxButton.OK, MessageBoxImage.Error);
            await DisconnectAsync();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void DisconnectButton_Click(object sender, RoutedEventArgs e) =>
        await DisconnectAsync();

    private async Task DisconnectAsync()
    {
        // Сначала снимаем хук и досылаем оставшиеся клавиши, пока канал ещё жив:
        // иначе после Dispose клиента нажатия уходят в никуда.
        await StopInputCaptureAsync();

        if (_client is not null)
        {
            _client.FrameUpdated -= OnFrameUpdated;
            _client.Disconnected -= OnDisconnected;
            await _client.DisposeAsync();
            _client = null;
        }

        if (_push is not null)
        {
            try { await _push.DisposeAsync(); }
            catch { /* удаление агента не должно мешать */ }
            _push = null;
        }

        _bitmap = null;
        ScreenImage.Source = null;
        ResolutionText.Text = string.Empty;
        ConnectButton.IsEnabled = true;
        DisconnectButton.IsEnabled = false;

        if (!StatusText.Text.StartsWith("Ошибка", StringComparison.OrdinalIgnoreCase))
            StatusText.Text = "Отключено";
    }

    private void OnDisconnected(object? sender, EventArgs e)
    {
        Dispatcher.InvokeAsync(async () =>
        {
            // Соединение разорвано — снимаем хук, чтобы клавиши больше не
            // перехватывались (иначе они «исчезали» бы, не доходя до удалённого стола).
            await StopInputCaptureAsync();

            StatusText.Text = $"Соединение с {_connectedHost} разорвано.";
            ConnectButton.IsEnabled = true;
            DisconnectButton.IsEnabled = false;
            if (_push is not null)
            {
                await _push.DisposeAsync();
                _push = null;
            }
        });
    }

    private void OnFrameUpdated(object? sender, FrameUpdateEventArgs e)
    {
        RfbClient? client = _client;
        if (client is null)
            return;

        Dispatcher.InvokeAsync(() =>
        {
            if (_client != client)
                return;

            Framebuffer fb = client.Framebuffer;

            if (_bitmap is null || _bitmap.PixelWidth != fb.Width || _bitmap.PixelHeight != fb.Height)
            {
                _bitmap = new WriteableBitmap(fb.Width, fb.Height, 96, 96, PixelFormats.Pbgra32, null);
                ScreenImage.Source = _bitmap;
                ResolutionText.Text = $"{fb.Width}x{fb.Height}";
            }

            foreach ((int x, int y, int w, int h) in e.Rectangles)
            {
                if (x < 0 || y < 0 || w <= 0 || h <= 0 || x + w > fb.Width || y + h > fb.Height)
                    continue;

                // Один вызов WritePixels на прямоугольник (вместо построчных вызовов):
                // источник — наш фреймбуфер с тем же stride, offset указывает на
                // первый пиксель прямоугольника. Это на порядки быстрее на больших
                // обновлениях (800 вызовов -> 1 на кадр).
                _bitmap.WritePixels(
                    new Int32Rect(x, y, w, h),
                    fb.Data,
                    fb.Stride,
                    y * fb.Stride + x * 4);
            }
        }, DispatcherPriority.Render);
    }

    // ---------- Ввод (мышь/клавиатура) ----------

    private (int X, int Y) GetFrameBufferPoint(Point position)
    {
        if (_bitmap is null)
            return (-1, -1);

        double bmpW = _bitmap.PixelWidth;
        double bmpH = _bitmap.PixelHeight;
        double elementW = ScreenImage.ActualWidth;
        double elementH = ScreenImage.ActualHeight;
        if (elementW <= 0 || elementH <= 0)
            return (-1, -1);

        // Stretch=Uniform: учитываем letterbox-поля.
        double scale = Math.Min(elementW / bmpW, elementH / bmpH);
        double offsetX = (elementW - bmpW * scale) / 2.0;
        double offsetY = (elementH - bmpH * scale) / 2.0;

        int x = (int)((position.X - offsetX) / scale);
        int y = (int)((position.Y - offsetY) / scale);

        if (x < 0 || y < 0 || x >= bmpW || y >= bmpH)
            return (-1, -1);
        return (x, y);
    }

    // ---------- Отправка ввода (устойчиво к разрыву соединения) ----------

    /// <summary>Отправляет событие мыши, дожидаясь завершения предыдущей отправки.</summary>
    private async Task SendPointerAsync(byte mask, int x, int y)
    {
        RfbClient? client = _client;
        if (client is null)
            return;

        await _pointerLock.WaitAsync().ConfigureAwait(true);
        try
        {
            await client.SendPointerEventAsync(mask, (ushort)x, (ushort)y, CancellationToken.None);
        }
        catch (Exception)
        {
            // Соединение разорвано — консоль не должна падать из-за этого.
        }
        finally
        {
            _pointerLock.Release();
        }
    }

    /// <summary>Перемещение мыши без ожидания: если предыдущий кадр ещё в полёте — пропускаем.</summary>
    private async Task SendPointerCoalescedAsync(byte mask, int x, int y)
    {
        RfbClient? client = _client;
        if (client is null || !_pointerLock.Wait(0))
            return;

        try
        {
            await client.SendPointerEventAsync(mask, (ushort)x, (ushort)y, CancellationToken.None);
        }
        catch (Exception)
        {
        }
        finally
        {
            _pointerLock.Release();
        }
    }

    private async Task SendKeyAsync(uint keysym, bool down)
    {
        RfbClient? client = _client;
        if (client is null)
            return;

        try
        {
            await client.SendKeyEventAsync(keysym, down, CancellationToken.None);
        }
        catch (Exception)
        {
        }
    }

    // ---------- Захват клавиатуры хуком ----------

    /// <summary>Ставит низкоуровневый хук и насос, который шлёт клавиши в RFB.</summary>
    private void StartInputCapture(RfbClient client)
    {
        if (_keyboardHook is not null)
            return;

        _hwnd = new WindowInteropHelper(this).Handle;

        // Канал развязывает колбэк хука и запись в SslStream: колбэк обязан
        // вернуться за миллисекунды, иначе Windows молча снимает хук.
        _keyChannel = Channel.CreateUnbounded<(uint Keysym, bool Down)>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        var hook = new KeyboardHook { CaptureAllowed = HookCaptureAllowed };
        hook.KeyEvent += (keysym, down) => _keyChannel?.Writer.TryWrite((keysym, down));
        hook.SecureAttention += () =>
        {
            _keyChannel?.Writer.TryWrite((SasKeysym, true));
            _keyChannel?.Writer.TryWrite((SasKeysym, false));
        };

        if (!hook.Start())
        {
            hook.Dispose();
            _keyChannel.Writer.TryComplete();
            _keyChannel = null;
            StatusText.Text += " | хук недоступен, клавиатура — через события окна";
            TraceKey("hook FAILED to install");
            return;
        }

        _keyPump = Task.Run(() => PumpKeysAsync(_keyChannel.Reader));
        _keyboardHook = hook;
        TraceKey($"hook installed, hwnd=0x{_hwnd.ToInt64():X}, trace={(KeyTrace ? "on" : "off")}");
    }

    /// <summary>Снимает хук и досылает оставшиеся в очереди клавиши.</summary>
    private async Task StopInputCaptureAsync()
    {
        KeyboardHook? hook = _keyboardHook;
        _keyboardHook = null;
        hook?.Dispose();

        Channel<(uint Keysym, bool Down)>? channel = _keyChannel;
        _keyChannel = null;
        channel?.Writer.TryComplete();

        Task? pump = _keyPump;
        _keyPump = null;
        if (pump is not null)
        {
            // Если запись залипла, не подвешиваем отключение — просто бросаем насос.
            try { await Task.WhenAny(pump, Task.Delay(1000)); }
            catch (Exception) { }
        }
    }

    private async Task PumpKeysAsync(ChannelReader<(uint Keysym, bool Down)> reader)
    {
        await foreach ((uint keysym, bool down) in reader.ReadAllAsync().ConfigureAwait(false))
        {
            RfbClient? client = _client;
            if (client is null)
                continue;

            try
            {
                TraceKey($"send sym=0x{keysym:X4} down={down}");
                await client.SendKeyEventAsync(keysym, down, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Разрыв соединения обработает OnDisconnected.
            }
        }
    }

    /// <summary>Хук глобальный: перехватываем только когда активно окно консоли
    /// и фокус не в локальном поле ввода/списке.</summary>
    private bool HookCaptureAllowed()
    {
        if (_hwnd == IntPtr.Zero)
            return false;

        // Модальные диалоги (MessageBox) забирают фокус — им тоже отдаём клавиши,
        // иначе Enter/Escape в диалоге не сработают.
        return GetForegroundWindow() == _hwnd && !IsLocalInputTarget();
    }

    private async void ScreenHost_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        (int x, int y) = GetFrameBufferPoint(e.GetPosition(ScreenImage));
        if (x < 0)
            return;

        _buttonMask = e.ChangedButton switch
        {
            MouseButton.Left => (byte)(_buttonMask | 0x01),
            MouseButton.Middle => (byte)(_buttonMask | 0x02),
            MouseButton.Right => (byte)(_buttonMask | 0x04),
            _ => _buttonMask,
        };

        await SendPointerAsync(_buttonMask, x, y);

        _mouseCaptured = ScreenHost.CaptureMouse();
        ScreenHost.Focus();
        e.Handled = true;
    }

    private async void ScreenHost_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_client is null)
            return;

        (int x, int y) = GetFrameBufferPoint(e.GetPosition(ScreenImage));
        if (x < 0)
            return;

        await SendPointerCoalescedAsync(_buttonMask, x, y);
        e.Handled = true;
    }

    private async void ScreenHost_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_client is null)
            return;

        _buttonMask = e.ChangedButton switch
        {
            MouseButton.Left => (byte)(_buttonMask & ~0x01),
            MouseButton.Middle => (byte)(_buttonMask & ~0x02),
            MouseButton.Right => (byte)(_buttonMask & ~0x04),
            _ => _buttonMask,
        };

        (int x, int y) = GetFrameBufferPoint(e.GetPosition(ScreenImage));
        if (x >= 0)
            await SendPointerAsync(_buttonMask, x, y);

        if (_mouseCaptured)
        {
            ScreenHost.ReleaseMouseCapture();
            _mouseCaptured = false;
        }
        e.Handled = true;
    }

    private async void ScreenHost_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_client is null)
            return;

        (int x, int y) = GetFrameBufferPoint(e.GetPosition(ScreenImage));
        if (x < 0)
            return;

        // Биты 3 и 4 маски RFB — колесо вверх/вниз.
        byte wheelMask = e.Delta > 0 ? (byte)0x08 : (byte)0x10;
        await SendPointerAsync((byte)(_buttonMask | wheelMask), x, y);
        await SendPointerAsync(_buttonMask, x, y);
        e.Handled = true;
    }

    private bool IsLocalInputTarget()
    {
        // Пока пользователь печатает в локальных полях или ходит по списку —
        // не пересылаем клавиши в удалённую машину.
        return Keyboard.FocusedElement is TextBox ||
               Keyboard.FocusedElement is ListView ||
               Keyboard.FocusedElement is ListViewItem;
    }

    private async void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Когда хук установлен, клавиатурой владеет он: WPF-события остаются
        // только фолбэком на случай, если SetWindowsHookEx не сработал
        // (иначе клавиши уходили бы удалённому столу дважды).
        if (_keyboardHook is not null || _client is null || IsLocalInputTarget())
            return;

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (KeysymMapper.TryGetKeysym(key, Keyboard.Modifiers, out uint keysym))
        {
            await SendKeyAsync(keysym, down: true);
            e.Handled = true;
        }
    }

    private async void MainWindow_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (_keyboardHook is not null || _client is null || IsLocalInputTarget())
            return;

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (KeysymMapper.TryGetKeysym(key, Keyboard.Modifiers, out uint keysym))
        {
            await SendKeyAsync(keysym, down: false);
            e.Handled = true;
        }
    }

    // ---------- Служебное ----------

    private void SetBusy(bool busy)
    {
        ConnectButton.IsEnabled = !busy;
        RefreshButton.IsEnabled = !busy;
        ComputerList.IsEnabled = !busy;
    }

    private static X509Certificate2? FindClientCertificate()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);

        // EKU Client Authentication = 1.3.6.1.5.5.7.3.2
        foreach (X509Certificate2 cert in store.Certificates)
        {
            if (!cert.HasPrivateKey)
                continue;

            bool hasClientAuth = cert.Extensions
                .OfType<X509EnhancedKeyUsageExtension>()
                .SelectMany(eku => eku.EnhancedKeyUsages.Cast<Oid>())
                .Any(oid => oid.Value == "1.3.6.1.5.5.7.3.2");

            if (hasClientAuth)
                return new X509Certificate2(cert); // копия с закрытым ключом
        }

        return null;
    }
}
