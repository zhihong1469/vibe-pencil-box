// <copyright file="KeymapCardViewModel.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Collections.ObjectModel;
using PencilBox.Core.Models;

namespace PencilBox.Desktop.ViewModels;

/// <summary>
/// 一键一卡的 Keymap 卡片（卡=物理键；行=该键的事件映射）。
/// 行实例与保存路径共享（ProductHomeViewModel.KeymapRows），卡片仅重组不复制。
/// </summary>
public class KeymapCardViewModel
{
    public KeymapCardViewModel(byte keyId, bool isEditable, IEnumerable<KeymapRowViewModel> rows)
    {
        KeyId = keyId;
        KeyName = KeymapFormatter.GetKeyName(keyId);
        IsEditable = isEditable;
        Rows = new ObservableCollection<KeymapRowViewModel>(rows);
    }

    public byte KeyId { get; }

    /// <summary>物理键产品名（K2 语音 / K3 前翻…，KeymapFormatter 单点）</summary>
    public string KeyName { get; }

    /// <summary>本卡是否可编辑（出厂默认配置整体只读）</summary>
    public bool IsEditable { get; }

    public ObservableCollection<KeymapRowViewModel> Rows { get; }
}
