// <copyright file="MainWindow.xaml.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using PencilBox.Desktop.ViewModels;

namespace PencilBox.Desktop;

/// <summary>
/// 主窗口：尺寸/位置/状态记忆（弹性窗口配套——用户按自己屏幕调整后下次按原样打开）。
/// 记忆文件放 %AppData%\PencilBox\window.json，损坏/缺失一律回退 XAML 默认值，绝不因此起不来。
/// </summary>
public partial class MainWindow : Window
{
    private static string StorePath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PencilBox");
            return Path.Combine(dir, "window.json");
        }
    }

    private sealed record WindowBounds(int Width, int Height, int Left, int Top, bool Maximized);

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

#if !DEBUG_TOOLS
        // 成品版（Release/外发，未定义 DEBUG_TOOLS）：隐藏调试工具面板，只留产品主页给普通用户
        DebugToolsExpander.Visibility = Visibility.Collapsed;
#endif

        RestoreBoundsIfAny();
    }

    private void RestoreBoundsIfAny()
    {
        try
        {
            if (!File.Exists(StorePath))
                return;

            var b = JsonSerializer.Deserialize<WindowBounds>(File.ReadAllText(StorePath));
            if (b == null || b.Width < (int)MinWidth || b.Height < (int)MinHeight)
                return;

            Width = b.Width;
            Height = b.Height;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = b.Left;
            Top = b.Top;
            if (b.Maximized)
                WindowState = WindowState.Maximized;
        }
        catch
        {
            // 记忆文件损坏等：静默回退 XAML 默认值
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        try
        {
            var b = WindowState == WindowState.Maximized
                ? new WindowBounds((int)RestoreBounds.Width, (int)RestoreBounds.Height,
                    (int)RestoreBounds.Left, (int)RestoreBounds.Top, true)
                : new WindowBounds((int)ActualWidth, (int)ActualHeight,
                    (int)Left, (int)Top, false);

            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(b));
        }
        catch
        {
            // 写不进去（无权限等）：不影响退出
        }

        base.OnClosing(e);
    }
}
