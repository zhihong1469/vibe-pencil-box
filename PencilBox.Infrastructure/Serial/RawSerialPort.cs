// <copyright file="RawSerialPort.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PencilBox.Core.Communication;

namespace PencilBox.Infrastructure.Serial;

/// <summary>
/// **裸句柄串口**：直接用 `CreateFile("\\.\COMx")` + `GetCommState/SetCommState/SetCommTimeouts` 打开，
/// 给 `System.IO.Ports.SerialPort` **拒开的虚拟串口**兜底。
///
/// 为什么必须有它（2026-09-17 实测，非推断）：
/// - 我们 Dongle 的日志口 **COM6** 是 `ROOT\PORTS\0000` + `服务=WUDFRd`（**UMDF 软件虚拟串口**）；
/// - `SerialPort.GetPortNames()` 列得出它、注册表 `HARDWARE\DEVICEMAP\SERIALCOMM` 有它、
///   OS 层 `CreateFileW("\\.\COM6")` **能开**；
/// - 但 `new SerialPort("COM6").Open()`（连显式 `"\\.\COM6"` 形式）**恒抛**
///   `ArgumentException: ... does not resolve to a valid serial port`；
/// - 同机真实 USB 转串口（COM7 / COM23 = CH340）一切正常 ⇒ **不是波特率/DTR 配置问题，也不是设备故障，
///   而是 .NET `SerialPort` 对 UMDF / root 枚举虚拟串口的兼容性限制**（PuTTY / TeraTerm / pyserial 都能开）。
///
/// 与正式路径（<see cref="SerialTransport"/>）保持同两条纪律：
/// 1. **不拉 DTR/RTS**（R1 板 `DTR→RESET` 低有效，误拉会复位笔端）——显式清掉 DCB 的 DTR/RTS 控制位；
/// 2. **读超时有界**（夹在 [50,500]ms）——取消能在半秒内生效，静默口也不空转。
/// </summary>
internal sealed class RawSerialPort : IDisposable
{
    // ---- Win32 ----

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;

    /// <summary>DCB 标志位（winbase.h）：DTR 控制位（bit4-5）</summary>
    private const uint DcbDtrControlMask = 0x30;

    /// <summary>DCB 标志位：RTS 控制位（bit12-13）</summary>
    private const uint DcbRtsControlMask = 0x3000;

    /// <summary>DCB 标志位：fOutxCtsFlow（bit2）</summary>
    private const uint DcbOutxCtsFlow = 0x04;

    /// <summary>DCB 标志位：fOutxDsrFlow（bit3）</summary>
    private const uint DcbOutxDsrFlow = 0x08;

    /// <summary>DCB 标志位：fParity（bit1）</summary>
    private const uint DcbParity = 0x02;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetCommState(SafeFileHandle hFile, ref Dcb lpDcb);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetCommState(SafeFileHandle hFile, ref Dcb lpDcb);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetCommTimeouts(SafeFileHandle hFile, ref CommTimeouts lpCommTimeouts);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(SafeFileHandle hFile, IntPtr lpOverlapped);

    /// <summary>DCB（28B，字段序必须与 winbase.h 一致）</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Dcb
    {
        public uint DCBlength;
        public uint BaudRate;
        public uint Flags;
        public ushort wReserved;
        public ushort XonLim;
        public ushort XoffLim;
        public byte ByteSize;
        public byte Parity;
        public byte StopBits;
        public byte XonChar;
        public byte XoffChar;
        public byte ErrorChar;
        public byte EofChar;
        public byte EvtChar;
        public ushort wReserved1;
    }

    /// <summary>COMMTIMEOUTS（20B）</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct CommTimeouts
    {
        public uint ReadIntervalTimeout;
        public uint ReadTotalTimeoutMultiplier;
        public uint ReadTotalTimeoutConstant;
        public uint WriteTotalTimeoutMultiplier;
        public uint WriteTotalTimeoutConstant;
    }

    // ---- 实例 ----

    private readonly SafeFileHandle _handle;
    private readonly FileStream _stream;
    private bool _disposed;

    /// <summary>读写流（`FileStream` 直通 `ReadFile`/`WriteFile`；字符设备故不可 seek）</summary>
    public Stream Stream => _stream;

    public bool IsOpen => !_disposed && !_handle.IsInvalid && !_handle.IsClosed;

    /// <summary>实际生效的读超时（毫秒）</summary>
    public int ReadTimeoutMs { get; }

    /// <summary>
    /// 踹醒**挂起的同步 Read**（收尾路径专用）：同步句柄的在途 `ReadFile` 无法靠取消令牌打断，
    /// 驱动若不完成它，连 `CloseHandle` 都会一直等在途 IO —— 先 `CancelIoEx` 再交后台 Dispose，
    /// 调用方线程绝不阻塞（06 M1 验收"双口停止卡死"的裸句柄路径兜底）。
    /// 无在途读时本调用无害（`ERROR_NOT_FOUND`）。
    /// </summary>
    public void CancelPendingRead()
    {
        if (IsOpen)
            CancelIoEx(_handle, IntPtr.Zero);
    }

    private RawSerialPort(SafeFileHandle handle, Stream stream, int readTimeoutMs)
    {
        _handle = handle;
        _stream = (FileStream)stream;
        ReadTimeoutMs = readTimeoutMs;
    }

    /// <summary>
    /// 判断 `SerialPort.Open()` 的失败是否属于"这个（虚拟）口名 .NET 不认" —— **只有这一种情况才该走裸句柄兜底**；
    /// 占用/权限/线缆等真错误必须原样暴露，不能被兜底吞掉。
    /// 判据用 `ParamName`（而非本地化后的消息文本）。
    /// </summary>
    public static bool IsUnsupportedPortName(Exception ex)
        => ex is ArgumentException { ParamName: "portName" };

    /// <summary>打开 `COMx`（内部走 `\\.\COMx`）</summary>
    /// <exception cref="Win32Exception">打开失败（被占用 / 权限 / 口不存在等），带系统错误码</exception>
    public static RawSerialPort Open(string portName, SerialPortSettings settings)
    {
        var handle = CreateFileW(
            @"\\.\" + portName,
            GenericRead | GenericWrite,
            dwShareMode: 0,                      // 独占：与串口语义一致，也守住"COM 口独占"纪律
            IntPtr.Zero,
            OpenExisting,
            dwFlagsAndAttributes: 0,             // 同步句柄（配合 SetCommTimeouts 做有界阻塞读）
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, $"裸句柄打开 {portName} 失败：{new Win32Exception(error).Message}");
        }

        try
        {
            ApplyState(handle, settings, out int readTimeoutMs);
            var stream = new FileStream(handle, FileAccess.ReadWrite, bufferSize: 1, isAsync: false);
            return new RawSerialPort(handle, stream, readTimeoutMs);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>按 <see cref="SerialPortSettings"/> 配 DCB 与超时（**DTR/RTS 一律禁用**）</summary>
    private static void ApplyState(SafeFileHandle handle, SerialPortSettings settings, out int readTimeoutMs)
    {
        var dcb = default(Dcb);
        dcb.DCBlength = (uint)Marshal.SizeOf<Dcb>();

        if (!GetCommState(handle, ref dcb))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetCommState 失败");

        dcb.BaudRate = (uint)settings.BaudRate;
        dcb.ByteSize = (byte)settings.DataBits;
        dcb.Parity = (byte)settings.Parity;      // Win32 与本端枚举同序（None..Space = 0..4）
        dcb.StopBits = ToWin32StopBits(settings.StopBits);

        // 不拉 DTR/RTS + 关掉硬件流控（笔端 DTR→RESET 低有效，误拉会复位笔端）
        dcb.Flags &= ~DcbDtrControlMask;
        dcb.Flags &= ~DcbRtsControlMask;
        dcb.Flags &= ~DcbOutxCtsFlow;
        dcb.Flags &= ~DcbOutxDsrFlow;

        if (settings.Parity == SerialParity.None)
            dcb.Flags &= ~DcbParity;
        else
            dcb.Flags |= DcbParity;

        if (!SetCommState(handle, ref dcb))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetCommState 失败");

        // 有界阻塞读：等到首个字节即返回；一直没有则 `constant` 毫秒后返回 0 字节（不抛异常、不空转）
        readTimeoutMs = Math.Clamp(settings.ReadTimeout, 50, 500);
        var timeouts = new CommTimeouts
        {
            ReadIntervalTimeout = 0xFFFFFFFF,
            ReadTotalTimeoutMultiplier = 0,
            ReadTotalTimeoutConstant = (uint)readTimeoutMs,
            WriteTotalTimeoutMultiplier = 0,
            WriteTotalTimeoutConstant = (uint)Math.Clamp(settings.WriteTimeout, 50, 500)
        };

        if (!SetCommTimeouts(handle, ref timeouts))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetCommTimeouts 失败");
    }

    /// <summary>停止位映射：本端 `None/One/OnePointFive/Two` → Win32 `ONESTOPBIT/ONE5STOPBITS/TWOSTOPBITS`（**序不同，不能强转**）</summary>
    private static byte ToWin32StopBits(SerialStopBits stopBits) => stopBits switch
    {
        SerialStopBits.OnePointFive => 1,   // ONE5STOPBITS
        SerialStopBits.Two => 2,            // TWOSTOPBITS
        _ => 0                              // ONESTOPBITS（含 None：按 1 位处理）
    };

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        try
        {
            _stream.Dispose();   // 一并释放 _handle
        }
        catch (Exception)
        {
            // 收尾期的释放失败不影响调用方
        }
    }
}
