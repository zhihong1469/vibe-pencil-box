// <copyright file="MainThreadHelper.cs" company="PencilBox Contributors">
//   Copyright (c) 2026 PencilBox Contributors. All rights reserved.
//   Licensed under the Polyform Noncommercial License 1.0.0.
//   See LICENSE file in the project root for full license information.
// </copyright>
using System.Windows.Threading;

namespace PencilBox.Desktop.Helpers;

/// <summary>
/// UI 线程辅助类
/// </summary>
public static class MainThreadHelper
{
    private static Dispatcher? _dispatcher;

    public static void Initialize(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public static void EnsureOnMainThread(Action action)
    {
        if (_dispatcher == null)
        {
            action();
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.Invoke(action);
        }
    }

    public static async Task EnsureOnMainThreadAsync(Action action)
    {
        if (_dispatcher == null)
        {
            action();
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            await _dispatcher.InvokeAsync(action);
        }
    }
}
