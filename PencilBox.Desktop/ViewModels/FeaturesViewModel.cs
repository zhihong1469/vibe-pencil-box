// <copyright file="FeaturesViewModel.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using PencilBox.Core.Features;
using PencilBox.Core.Models;
using PencilBox.Core.Services;
using PencilBox.Desktop.Helpers;

namespace PencilBox.Desktop.ViewModels;

/// <summary>
/// 功能控制台（14 号架构提案，2026-10-19）：可选功能模块总开关。
/// CLI `feature` 域与 GUI 共用同一份 configs/features.json——任意一边切换，另一边经 Changed 事件跟上。
/// 语义：关 = CLI 管辖域拒绝 + GUI 入口（页签）隐藏 + 运行中引擎立即停。
/// 核心域（连接/绑定/笔端状态/OTA/Keymap）不可关，不进本表（设备强相关，与连接同生共死）。
/// </summary>
public partial class FeaturesViewModel : ObservableObject
{
    private readonly IFeatureToggleService _toggles;
    private readonly IAudioPassthrough _passthrough;

    public ObservableCollection<FeatureItemViewModel> Items { get; }

    public FeaturesViewModel(IFeatureToggleService toggles, IAudioPassthrough passthrough)
    {
        _toggles = toggles;
        _passthrough = passthrough;

        Items = new ObservableCollection<FeatureItemViewModel>(
            _toggles.Describe().Select(d => new FeatureItemViewModel(_toggles, d, OnToggleRequested)));

        // CLI 侧 feature on/off 也走这里刷新（开关是同一份持久化状态）
        _toggles.Changed += (_, _) => MainThreadHelper.EnsureOnMainThread(Refresh);
        Refresh();
    }

    /// <summary>开关请求（复选框两路绑定进入）。</summary>
    private void OnToggleRequested(FeatureDescriptor descriptor, bool enabled)
    {
        _toggles.SetEnabled(descriptor.Id, enabled);

        // 停用副作用：运行中引擎立即停（扩音=直通回路截断；扩音页 UI 订阅 StateChanged 自愈归 Idle）
        if (!enabled && descriptor.Id == "audio" && _passthrough.State == AudioPassthroughState.Running)
            _ = _passthrough.StopAsync();
    }

    private void Refresh()
    {
        foreach (var item in Items)
            item.Refresh();

        OnPropertyChanged(nameof(AudioTabVisibility));
        OnPropertyChanged(nameof(AirMouseTabVisibility));
        OnPropertyChanged(nameof(SerialScopeTabVisibility));
        OnPropertyChanged(nameof(SoftTriggerTabVisibility));
    }

    // 页签显隐直出 Visibility（坑点速查 §三：转换器返回类型≠目标属性会绑定静默失败——不设中间转换器）
    public Visibility AudioTabVisibility => ToVis("audio");
    public Visibility AirMouseTabVisibility => ToVis("airmouse");
    public Visibility SerialScopeTabVisibility => ToVis("seriallog");
    public Visibility SoftTriggerTabVisibility => ToVis("softtrigger");

    private Visibility ToVis(string featureId) =>
        _toggles.IsEnabled(featureId) ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>功能控制台一行：名称 + 开关 + 说明 + 管辖 CLI 域。</summary>
public partial class FeatureItemViewModel : ObservableObject
{
    private readonly IFeatureToggleService _toggles;
    private readonly Action<FeatureDescriptor, bool> _onToggleRequested;

    public FeatureDescriptor Descriptor { get; }

    public string Id { get; }
    public string Name { get; }
    public string Description { get; }
    public string CliDomainsText { get; }

    public FeatureItemViewModel(
        IFeatureToggleService toggles,
        FeatureDescriptor descriptor,
        Action<FeatureDescriptor, bool> onToggleRequested)
    {
        _toggles = toggles;
        Descriptor = descriptor;
        _onToggleRequested = onToggleRequested;

        Id = descriptor.Id;
        Name = descriptor.Name;
        Description = descriptor.Description;
        CliDomainsText = string.Join(" / ", descriptor.CliDomains);
    }

    /// <summary>当前开关（复选框两路绑定；与 CLI 同一份持久化状态）。</summary>
    public bool IsEnabled
    {
        get => _toggles.IsEnabled(Id);
        set
        {
            if (value != _toggles.IsEnabled(Id))
                _onToggleRequested(Descriptor, value);
        }
    }

    /// <summary>开关状态可能被外部（CLI/他处）改变：由 FeaturesViewModel 统一推刷新。</summary>
    public void Refresh() => OnPropertyChanged(nameof(IsEnabled));
}
