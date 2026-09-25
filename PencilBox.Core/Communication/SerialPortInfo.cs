// <copyright file="SerialPortInfo.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
namespace PencilBox.Core.Communication;

/// <summary>
/// 串口信息
/// </summary>
public class SerialPortInfo
{
    /// <summary>
    /// 串口名称（如 COM3）
    /// </summary>
    public string PortName { get; set; } = string.Empty;

    /// <summary>
    /// 串口描述
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// 设备实例路径
    /// </summary>
    public string? DeviceId { get; set; }

    /// <summary>
    /// 用于 ComboBox 显示的字符串
    /// </summary>
    public string Display => string.IsNullOrEmpty(Description)
        ? PortName
        : $"{PortName} - {Description}";

    public override string ToString() => Display;
}
