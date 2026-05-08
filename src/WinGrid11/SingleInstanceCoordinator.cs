using System.IO;
using System.IO.Pipes;

namespace WinGrid11;

/// <summary>
/// Enforces "only one WinGrid11 process at a time" via a named mutex,
/// and provides a tiny named-pipe channel so a second launch can
/// surface the running instance's settings window instead of silently
/// exiting.
///
/// Why mutex + pipe rather than just mutex:
///  * A user double-clicking the shortcut while WinGrid11 is already
///    running deserves *some* feedback. Silently exiting feels broken.
///    The pipe nudges the existing instance to pop its settings window.
///  * The pipe is best-effort. Cross-integrity (running normal-user
///    second instance while admin first instance holds the mutex)
///    nudges may be denied by the OS - that's fine, we still exit.
///
/// Elevation handoff:
///  * On "Restart as administrator" the parent calls ReleaseForHandoff
///    before launching the elevated child. The child acquires the
///    mutex on startup as soon as the parent's runas Process.Start
///    returns. If the user declines UAC, the parent calls
///    ReacquireAfterFailedHandoff to keep enforcing single-instance.
/// </summary>
internal sealed class SingleInstanceCoordinator : IDisposable
{
    // Local namespace (no Global\ prefix). Per-session lock matches our
    // per-user app model: two RDP sessions of the same user can each run
    // their own WinGrid11.
    private const string MutexName = "WinGrid11.SingleInstance.v1";
    private const string PipeName = "WinGrid11.Activate.v1";
    private const string ActivateCommand = "show-settings";

    private Mutex? _mutex;
    private CancellationTokenSource? _serverCts;
    private bool _acquired;

    /// <summary>
    /// Raised on a worker thread when another process pings the
    /// activation pipe. Subscribers must marshal to UI thread themselves.
    /// </summary>
    public event Action? ActivationRequested;

    /// <summary>
    /// Try to become the single instance. Returns false if another
    /// process already holds the lock.
    /// </summary>
    public bool TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: false, MutexName);
        try
        {
            // Short wait covers benign races: e.g. previous instance
            // still tearing down its tray icon when this one starts.
            _acquired = _mutex.WaitOne(TimeSpan.FromSeconds(2));
        }
        catch (AbandonedMutexException)
        {
            // Previous owner died without releasing. The OS gives us
            // the mutex; ignore the warning, we hold it now.
            _acquired = true;
        }
        return _acquired;
    }

    public void StartServer()
    {
        if (!_acquired) return;
        _serverCts = new CancellationTokenSource();
        _ = Task.Run(() => ServerLoopAsync(_serverCts.Token));
    }

    private async Task ServerLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(token).ConfigureAwait(false);

                using var reader = new StreamReader(server);
                var line = await reader.ReadLineAsync(token).ConfigureAwait(false);
                if (line == ActivateCommand)
                {
                    ActivationRequested?.Invoke();
                }
            }
            catch (OperationCanceledException) { return; }
            catch
            {
                // Malformed clients or transient pipe errors shouldn't
                // kill the server; loop and accept the next connection.
            }
        }
    }

    /// <summary>
    /// Best-effort signal to a running instance to pop its settings
    /// window. Called by the second instance just before it exits.
    /// </summary>
    public static void NudgeExistingInstance()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            pipe.Connect(1000);
            using var writer = new StreamWriter(pipe) { AutoFlush = true };
            writer.WriteLine(ActivateCommand);
        }
        catch
        {
            // Pipe gone, denied across integrity levels, server busy.
            // Existing instance is still running; the user can find it
            // in the system tray.
        }
    }

    /// <summary>
    /// Release the mutex so a planned successor (the elevated child of
    /// RestartElevated) can acquire it cleanly without waiting on the
    /// parent's full shutdown. Returns true if a handoff was performed.
    /// </summary>
    public bool ReleaseForHandoff()
    {
        if (_mutex is null || !_acquired) return false;
        try { _mutex.ReleaseMutex(); _acquired = false; return true; }
        catch { return false; }
    }

    /// <summary>
    /// Pair with <see cref="ReleaseForHandoff"/> when the handoff is
    /// abandoned (e.g. user declined the UAC prompt). Re-grabs the
    /// mutex without waiting; if some other process snuck in, gives up.
    /// </summary>
    public void ReacquireAfterFailedHandoff()
    {
        if (_mutex is null || _acquired) return;
        try { _acquired = _mutex.WaitOne(0); } catch { }
    }

    public void Dispose()
    {
        _serverCts?.Cancel();
        if (_mutex is not null)
        {
            try { if (_acquired) _mutex.ReleaseMutex(); } catch { }
            _mutex.Dispose();
            _mutex = null;
        }
    }
}
