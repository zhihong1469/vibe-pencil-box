// <copyright file="AirMouseTelemetryView.xaml.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Windows.Controls;

namespace PencilBox.Desktop.Views;

/// <summary>
/// `0x62` 遥测流与录波页（空鼠工作台新增页；对齐卷 06 §3）——逻辑全在
/// <see cref="ViewModels.TelemetryViewModel"/>。
/// </summary>
public partial class AirMouseTelemetryView : UserControl
{
    public AirMouseTelemetryView()
    {
        InitializeComponent();
    }
}
