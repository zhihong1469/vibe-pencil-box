// <copyright file="GlobalKeyboardHook.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Runtime.InteropServices;
using PencilBox.Core.Services;

namespace PencilBox.Infrastructure.Platform;

/// <summary>
/// Win32 低级键盘钩子（WH_KEYBOARD_LL）实现：专用线程装钩 + GetMessage 泵；
/// **永不吞键**（恒 CallNextHookEx），down 边沿去重（长按重复不重发）。
/// 钩子线程存活期间回调委托必须保活（_proc 字段持有）。
/// </summary>
public sealed class GlobalKeyboardHook : IGlobalKeyboardHook, IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int WmQuit = 0x0012;

    public event Action<int>? KeyDown;

    private readonly object _gate = new();
    private Thread? _thread;
    private int _threadId;
    private IntPtr _hook;
    private LowLevelHookProc? _proc;               // 钩子回调委托必须保活，防 GC 后钩子失效
    private readonly HashSet<int> _downKeys = new();   // 仅钩子线程访问

    public void Start()
    {
        lock (_gate)
        {
            if (_thread != null)
                return;
            _thread = new Thread(PumpLoop)
            {
                Name = "GlobalKeyboardHook",
                IsBackground = true
            };
            _thread.Start();
        }
    }

    public void Stop()
    {
        Thread? thread;
        lock (_gate)
        {
            thread = _thread;
            _thread = null;
        }
        if (thread == null)
            return;
        PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        thread.Join(2000);
    }

    private void PumpLoop()
    {
        _threadId = GetCurrentThreadId();
        _proc = HookProc;
        _hook = SetWindowsHookEx(WhKeyboardLl, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
            return;   // 安装失败：静默退线程（监听腿缺失，状态卡停留"待事件"，不影响其余功能）

        // GetMessage 返回 0（WM_QUIT）或 -1（错误）时退出循环
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
        }

        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _downKeys.Clear();
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var msg = wParam.ToInt32();
            var kbd = Marshal.PtrToStructure<KbdDllHookStruct>(lParam);
            var vk = (int)kbd.VkCode;
            switch (msg)
            {
                case WmKeyDown or WmSysKeyDown when _downKeys.Add(vk):
                    KeyDown?.Invoke(vk);
                    break;
                case WmKeyUp or WmSysKeyUp:
                    _downKeys.Remove(vk);
                    break;
            }
        }
        // 永不吞键：观察者语义，外部热键软件不受影响
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdDllHookStruct
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    private delegate IntPtr LowLevelHookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelHookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out NativeMessage lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(int threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern int GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public NativePoint Point;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    public void Dispose() => Stop();
}
