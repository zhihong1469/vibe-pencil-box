// <copyright file="SignalIndicatorControl.xaml.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PencilBox.Desktop.Controls;

/// <summary>
/// 蓝牙信号强度指示器控件（5 格信号条）
/// 独立可替换的 UI 边界。RSSI 映射为信号格数。
/// </summary>
public partial class SignalIndicatorControl : UserControl
{
    public static readonly DependencyProperty IsConnectedProperty =
        DependencyProperty.Register(nameof(IsConnected), typeof(bool), typeof(SignalIndicatorControl),
            new PropertyMetadata(false, OnVisualPropertyChanged));

    public static readonly DependencyProperty RssiProperty =
        DependencyProperty.Register(nameof(Rssi), typeof(int), typeof(SignalIndicatorControl),
            new PropertyMetadata(0, OnVisualPropertyChanged));

    public bool IsConnected
    {
        get => (bool)GetValue(IsConnectedProperty);
        set => SetValue(IsConnectedProperty, value);
    }

    public int Rssi
    {
        get => (int)GetValue(RssiProperty);
        set => SetValue(RssiProperty, value);
    }

    public SignalIndicatorControl()
    {
        InitializeComponent();
    }

    private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SignalIndicatorControl control)
            control.UpdateSignal();
    }

    private void UpdateSignal()
    {
        if (Bar1 == null)
            return;

        int bars = IsConnected ? RssiToBars(Rssi) : 0;
        var activeBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0xCC, 0x71));
        var inactiveBrush = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0));

        Bar1.Fill = bars >= 1 ? activeBrush : inactiveBrush;
        Bar2.Fill = bars >= 2 ? activeBrush : inactiveBrush;
        Bar3.Fill = bars >= 3 ? activeBrush : inactiveBrush;
        Bar4.Fill = bars >= 4 ? activeBrush : inactiveBrush;
        Bar5.Fill = bars >= 5 ? activeBrush : inactiveBrush;

        StateText.Text = IsConnected
            ? $"RSSI {Rssi} dBm"
            : "未连接";
    }

    /// <summary>RSSI(dBm) → 信号格数（1~5）</summary>
    private static int RssiToBars(int rssi)
    {
        return rssi switch
        {
            >= -60 => 5,
            >= -68 => 4,
            >= -76 => 3,
            >= -85 => 2,
            _ => 1
        };
    }
}
