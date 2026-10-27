using System.Security.Cryptography;
using System.Text;
using System.Windows;
using SneakyFit.Infrastructure;

namespace SneakyFit.Desktop;

internal sealed class AppInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activation;
    private RegisteredWaitHandle? _listener;
    private bool _owned;
    private bool _disposed;

    public AppInstance()
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(PortablePaths.RootDirectory.ToUpperInvariant())))[..24];
        _mutex = new Mutex(false, @"Local\SneakyFit.v1." + key);
        _activation = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SneakyFit.Activate.v1." + key);
    }

    public bool Acquire()
    {
        try { _owned = _mutex.WaitOne(0); }
        catch (AbandonedMutexException) { _owned = true; }
        if (!_owned) _activation.Set();
        return _owned;
    }

    public void Listen(Window window)
    {
        var closed = false;
        window.Closed += (_, _) => closed = true;
        _listener = ThreadPool.RegisterWaitForSingleObject(_activation, (_, _) =>
        {
            if (window.Dispatcher.HasShutdownStarted) return;
            window.Dispatcher.BeginInvoke(() =>
            {
                if (_disposed || closed || window.Dispatcher.HasShutdownStarted) return;
                if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                window.Show(); window.Activate();
            });
        }, null, Timeout.Infinite, false);
    }

    public void Dispose()
    {
        _disposed = true;
        _listener?.Unregister(null);
        if (_owned) { _mutex.ReleaseMutex(); _owned = false; }
        _activation.Dispose(); _mutex.Dispose();
    }
}
