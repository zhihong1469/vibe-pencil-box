using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Management;
using System.Text;
using Microsoft.Extensions.Logging;
using PencilBox.Core.Communication;

namespace PencilBox.Infrastructure.Serial;

/// <summary>
/// 标准串口助手实现
/// </summary>
public class SerialTransport : ISerialTransport
{
    private readonly ILogger<SerialTransport> _logger;
    private SerialPort? _serialPort;

    /// <summary>裸句柄路径：**仅当 `SerialPort` 拒开该口名时**启用（见 <see cref="RawSerialPort"/>）</summary>
    private RawSerialPort? _rawPort;
    private CancellationTokenSource? _readCts;
    private Task? _readTask;
    private bool _isConnected;
    private bool _readErrorReported;

    public bool IsConnected => _isConnected;
    public string? CurrentPortName { get; private set; }
    public SerialPortSettings? CurrentSettings { get; private set; }

    /// <summary>打开的物理端口是否仍可用（正式路径与裸句柄兜底路径二选一）</summary>
    private bool IsPortOpen => _serialPort is { IsOpen: true } || _rawPort is { IsOpen: true };

    /// <summary>
    /// 停止位映射：本端枚举（`None/One/OnePointFive/Two` = 0/1/2/3）与
    /// `System.IO.Ports.StopBits`（`None/One/Two/OnePointFive` = 0/1/2/3）**序不同，不能强转**。
    /// </summary>
    private static StopBits ToWin32LikeStopBits(SerialStopBits stopBits) => stopBits switch
    {
        SerialStopBits.OnePointFive => StopBits.OnePointFive,
        SerialStopBits.Two => StopBits.Two,
        _ => StopBits.One
    };

    public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;
    public event EventHandler<Core.Communication.SerialDataReceivedEventArgs>? DataReceived;

    public SerialTransport(ILogger<SerialTransport> logger)
    {
        _logger = logger;
    }

    public Task<IReadOnlyList<SerialPortInfo>> EnumeratePortsAsync(
        CancellationToken cancellationToken = default)
    {
        var ports = new List<SerialPortInfo>();

        try
        {
            var portNames = SerialPort.GetPortNames().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _logger.LogInformation("SerialPort.GetPortNames() returned {Count} ports", portNames.Count);

            // 通过 WMI 获取更友好的描述（可选，失败不阻塞）
            var descriptions = GetPortDescriptions(portNames);

            foreach (var name in portNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                descriptions.TryGetValue(name, out var description);

                ports.Add(new SerialPortInfo
                {
                    PortName = name,
                    Description = description ?? string.Empty
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enumerate serial ports");
        }

        return Task.FromResult<IReadOnlyList<SerialPortInfo>>(ports);
    }

    public async Task<bool> ConnectAsync(string portName, SerialPortSettings settings,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Opening serial port {Port} @ {Baud}bps", portName, settings.BaudRate);

        try
        {
            await DisconnectAsync(cancellationToken);

            try
            {
                _serialPort = new SerialPort(portName, settings.BaudRate)
                {
                    DataBits = settings.DataBits,
                    StopBits = ToWin32LikeStopBits(settings.StopBits),
                    Parity = (Parity)settings.Parity,
                    Handshake = (Handshake)settings.Handshake,
                    ReadTimeout = settings.ReadTimeout,
                    WriteTimeout = settings.WriteTimeout,
                    Encoding = Encoding.UTF8,
                    DtrEnable = settings.DtrEnable,
                    RtsEnable = settings.RtsEnable
                };

                _serialPort.Open();
            }
            catch (Exception ex) when (RawSerialPort.IsUnsupportedPortName(ex))
            {
                // .NET 不认这类口名（UMDF/root 枚举的**虚拟串口**，如 Dongle 日志口 COM6）⇒ 改裸句柄。
                // 只兜这一种；占用/权限等真错误照旧原样抛。
                _serialPort?.Dispose();
                _serialPort = null;
                _rawPort = RawSerialPort.Open(portName, settings);
                _logger.LogInformation(
                    "SerialPort 不认口名 {Port}（虚拟串口），已改用裸句柄打开（DTR/RTS 仍不施加）", portName);
            }

            _isConnected = true;
            CurrentPortName = portName;
            CurrentSettings = settings;

            StartReading();

            ConnectionStateChanged?.Invoke(this,
                new ConnectionStateChangedEventArgs(true, new HidDeviceInfo
                {
                    DevicePath = portName,
                    ProductName = $"Serial {portName}"
                }));

            _logger.LogInformation("Serial port {Port} opened", portName);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open serial port {Port}", portName);
            ConnectionStateChanged?.Invoke(this,
                new ConnectionStateChangedEventArgs(false, null, ex));
            return false;
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (!_isConnected && _serialPort == null && _rawPort == null)
            return;

        _logger.LogInformation("Closing serial port...");

        // 先落"已断开"，再停读线程：否则读循环被取消时 `_isConnected` 仍为 true
        // ⇒ 把**正常收尾**误报成"串口读取异常"警告（每次停止都刷一条，纯噪音）
        _isConnected = false;
        var readTask = _readTask;   // StopReading 会清字段；先捕获供下方判断
        StopReading();

        var serial = _serialPort;
        _serialPort = null;
        var raw = _rawPort;
        _rawPort = null;

        // 读线程是否仍未收尾（正常收尾 500ms 内结束；卡住 = 驱动忽略读超时/不响应取消）
        var readStillPending = readTask is { IsCompleted: false };

        if (serial != null)
        {
            if (readStillPending)
            {
                // ⚠️ 读线程还卡在同步 Read 里时，**绝不能在本线程 Close/Dispose**：
                // `SerialPort.Close` 会同步等在途读收尾，驱动不配合就永久挂死——
                // 且本方法跑在 SerialPortHub 闸门内，一挂就把**所有**口的关闭全部堵死
                // （06 M1 验收"双口采集正常停止卡死"的根因）。交给后台收尾，立即返回。
                _ = Task.Run(() =>
                {
                    try
                    {
                        if (serial.IsOpen)
                            serial.Close();
                        serial.Dispose();
                        _logger.LogDebug("Serial port closed (background: read was still pending)");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Background serial close failed");
                    }
                });
            }
            else
            {
                try
                {
                    if (serial.IsOpen)
                        serial.Close();
                    serial.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error closing serial port");
                }
            }
        }

        if (raw != null)
        {
            if (readStillPending)
            {
                raw.CancelPendingRead();                 // 踹醒挂起的同步读（CancelIoEx）
                _ = Task.Run(() => raw.Dispose());       // 关句柄要等在途 IO 收尾 → 后台做，不阻塞闸门
            }
            else
            {
                raw.Dispose();
            }
        }

        _isConnected = false;   // 与开头那次重复，只是为"早退路径也不残留 true"兜底
        var portName = CurrentPortName;
        CurrentPortName = null;
        CurrentSettings = null;

        ConnectionStateChanged?.Invoke(this,
            new ConnectionStateChangedEventArgs(false, new HidDeviceInfo
            {
                DevicePath = portName ?? string.Empty,
                ProductName = $"Serial {portName}"
            }));

        _logger.LogInformation("Serial port {Port} closed", portName);
        await Task.CompletedTask;
    }

    public async Task SendAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        if (!_isConnected)
            throw new InvalidOperationException("Serial port not connected");

        cancellationToken.ThrowIfCancellationRequested();

        await Task.Run(() =>
        {
            if (_rawPort is { IsOpen: true } raw)
                raw.Stream.Write(data, 0, data.Length);
            else if (_serialPort is { IsOpen: true })
                _serialPort.Write(data, 0, data.Length);
            else
                throw new InvalidOperationException("Serial port not connected");
        }, cancellationToken);

        _logger.LogDebug("Sent {Bytes} bytes to {Port}", data.Length, CurrentPortName);
    }

    public async Task SendTextAsync(string text, CancellationToken cancellationToken = default)
    {
        if (text == null)
            return;

        var bytes = Encoding.UTF8.GetBytes(text);
        await SendAsync(bytes, cancellationToken);
    }

    public async Task SendHexAsync(string hexString, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(hexString))
            return;

        var cleaned = System.Text.RegularExpressions.Regex.Replace(hexString, @"[^0-9A-Fa-f]", "");
        if (cleaned.Length % 2 != 0)
            throw new ArgumentException("Hex string length must be even");

        var bytes = new byte[cleaned.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(cleaned.Substring(i * 2, 2), 16);
        }

        await SendAsync(bytes, cancellationToken);
    }

    private void StartReading()
    {
        _readCts = new CancellationTokenSource();
        _readTask = Task.Run(() => ReadLoop(_readCts.Token));
        _logger.LogDebug("Serial read loop started");
    }

    private void StopReading()
    {
        var cts = _readCts;
        var task = _readTask;
        _readCts = null;
        _readTask = null;

        cts?.Cancel();

        if (task != null)
        {
            try
            {
                task.Wait(TimeSpan.FromSeconds(2));
                cts?.Dispose();   // 读线程已收尾才释放（它可能还要访问 token 的 WaitHandle）
            }
            catch (AggregateException)
            {
                cts?.Dispose();   // 任务已终态（faulted/canceled），token 无人再用
            }
            // Wait 超时 ⇒ 读线程还活着：cts 交给 GC（此刻释放它，读线程异常路径访问
            // WaitHandle 会 ODE）；DisconnectAsync 会按"读未收尾"走后台关句柄路径
        }
        else
        {
            cts?.Dispose();
        }

        _logger.LogDebug("Serial read loop stopped");
    }

    /// <summary>
    /// 串口读循环（独立线程；`StopReading` 靠取消令牌收它）。
    ///
    /// ⚠️ 这里**刻意用同步读 `BaseStream.Read` 而不是 `ReadAsync`**：
    /// `ReadAsync` 在 `ReadTimeout` 到点时会以 `ERROR_OPERATION_ABORTED`（`IOException`）收尾，
    /// 于是"**暂时没数据**"这种最常见的情况会被当成读取异常 —— 每个静默串口都刷告警、并在
    /// 50ms 重试节奏上空转。同步读把这种情况表现为 `TimeoutException`（= 本轮没读到），
    /// 与**真正的掉线/句柄失效**区分得开（后者才是要告警的）。
    ///
    /// 超时值夹在 [50, 500]ms：既保证取消能在半秒内生效，又不空转。
    /// </summary>
    private void ReadLoop(CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];

        // 两条打开路径共用本循环：正式（SerialPort）/ 兜底（裸句柄，见 RawSerialPort）
        var stream = _serialPort?.BaseStream ?? _rawPort?.Stream;
        if (stream == null)
            return;

        // 夹住读超时：既保证取消能在半秒内生效，又不至于把超时设成 0（=永不超时，取消就收不住）
        // （裸句柄路径的超时已在 RawSerialPort.ApplyState 里按同一区间设好）
        if (_serialPort != null)
            _serialPort.ReadTimeout = Math.Clamp(_serialPort.ReadTimeout, 50, 500);

        while (!cancellationToken.IsCancellationRequested && IsPortOpen)
        {
            try
            {
                int bytesRead = stream.Read(buffer, 0, buffer.Length);
                if (bytesRead == 0)
                    continue;   // 本轮没读到（裸句柄：超时返回 0），继续下一轮

                var data = new byte[bytesRead];
                Buffer.BlockCopy(buffer, 0, data, 0, bytesRead);

                _readErrorReported = false;   // 恢复正常读取，允许下次错误突发重新提示
                _logger.LogDebug("Received {Bytes} bytes from {Port}", bytesRead, CurrentPortName);
                DataReceived?.Invoke(this, new Core.Communication.SerialDataReceivedEventArgs(data));
            }
            catch (TimeoutException)
            {
                // 本轮没读到数据：正常情况，继续下一轮
            }
            catch (Exception ex)
            {
                // 关闭/拔线期间的 IO 中止属正常收尾，降噪为 Debug；其余按"每次错误突发只提示一次"处理
                if (!_isConnected || _readCts?.IsCancellationRequested == true)
                {
                    _logger.LogDebug(ex, "Serial read loop ended during shutdown");
                    break;
                }

                if (!_readErrorReported)
                {
                    _readErrorReported = true;
                    _logger.LogWarning("串口读取异常（后续同类错误降为 Debug）：{Message}", ex.Message);
                }
                else
                {
                    _logger.LogDebug(ex, "Error reading from serial port");
                }

                // 出错后小歇（可被取消唤醒），避免句柄已坏时热转
                if (cancellationToken.WaitHandle.WaitOne(50))
                    break;
            }
        }
    }

    private static Dictionary<string, string> GetPortDescriptions(IReadOnlyList<string> portNames)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // ① 常规：Win32_SerialPort 有 Caption（CH340 / CP210x / 蓝牙等 USB 转串口都在这里）
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, Caption FROM Win32_SerialPort");

            foreach (ManagementObject obj in searcher.Get())
            {
                string? deviceId = obj["DeviceID"]?.ToString();
                string? caption = obj["Caption"]?.ToString();

                if (!string.IsNullOrWhiteSpace(deviceId) && !string.IsNullOrWhiteSpace(caption))
                    result[deviceId] = caption;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WMI serial port query failed: {ex.Message}");
        }

        // ② 补漏：**虚拟调试串口**（如 `JLVirtualJtagSerial Device (COM6)`）不在 Win32_SerialPort 里，
        //    只在 Win32_PnPEntity 有名字。少了这一步，我们自己的 Dongle 日志口会被标成"未知"类型
        //    —— 那"避开调试器虚拟口"的类型标注就成了空话（05 §3.2 / 坑点速查 §七）。
        var missing = portNames.Where(n => !result.ContainsKey(n)).ToList();
        if (missing.Count == 0)
            return result;

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");

            foreach (ManagementObject obj in searcher.Get())
            {
                var name = obj["Name"]?.ToString();
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var match = System.Text.RegularExpressions.Regex.Match(name, @"\((COM\d+)\)");
                if (!match.Success)
                    continue;

                var portName = match.Groups[1].Value;
                if (missing.Contains(portName, StringComparer.OrdinalIgnoreCase) && !result.ContainsKey(portName))
                    result[portName] = name;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WMI PnP query failed: {ex.Message}");
        }

        return result;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }
}
