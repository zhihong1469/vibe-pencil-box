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
