// <copyright file="SerialScopeView.xaml.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using PencilBox.Desktop.ViewModels;

namespace PencilBox.Desktop.Views;

/// <summary>
/// 串口页（View）。
///
/// 代码里只做一件 XAML 做不了的事：**自动落底**（打印区跟着最新行滚）。
/// 挂的是集合的 <see cref="INotifyCollectionChanged"/>（而不是每行的属性变更），
/// 批量刷屏时一次 Reset 只滚一次。
/// </summary>
public partial class SerialScopeView : UserControl
{
    private INotifyCollectionChanged? _observed;

    public SerialScopeView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_observed != null)
            _observed.CollectionChanged -= OnLinesChanged;

        _observed = (e.NewValue as SerialScopeViewModel)?.Lines;

        if (_observed != null)
            _observed.CollectionChanged += OnLinesChanged;
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (DataContext is not SerialScopeViewModel viewModel || !viewModel.AutoScroll)
            return;

        int count = LogList.Items.Count;
        if (count == 0)
            return;

        LogList.ScrollIntoView(LogList.Items[count - 1]);
    }
}
