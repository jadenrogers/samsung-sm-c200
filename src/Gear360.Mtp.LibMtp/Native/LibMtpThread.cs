using System.Collections.Concurrent;

namespace Gear360.Mtp.LibMtp.Native;

/// <summary>
/// The one thread that talks to libmtp. libmtp (and the libusb state under it) is not thread-safe,
/// so every native call in this backend is queued here and run in order.
/// </summary>
internal sealed class LibMtpThread
{
    private static readonly Lazy<LibMtpThread> s_instance = new(() => new LibMtpThread(), LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly BlockingCollection<Action> _queue = [];
    private readonly Thread _thread;
    private bool _initialized;

    private LibMtpThread()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "libmtp",
        };
        _thread.Start();
    }

    /// <summary>The shared libmtp thread, started on first use.</summary>
    public static LibMtpThread Instance => s_instance.Value;

    /// <summary>True when the caller is already running on the libmtp thread.</summary>
    public bool IsCurrentThread => Environment.CurrentManagedThreadId == _thread.ManagedThreadId;

    /// <summary>
    /// Runs <paramref name="work"/> on the libmtp thread after making sure libmtp is loaded and
    /// <c>LIBMTP_Init</c> has run. A token cancelled before the work starts cancels the task; once
    /// started, the work itself decides how to honour the token.
    /// </summary>
    /// <exception cref="LibMtpNotFoundException">libmtp could not be loaded.</exception>
    public Task<T> InvokeAsync<T>(Func<T> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        cancellationToken.ThrowIfCancellationRequested();
        LibMtpLibrary.EnsureLoaded();

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Execute()
        {
            if (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                EnsureInitialized();
                completion.TrySetResult(work());
            }
            catch (OperationCanceledException ex)
            {
                completion.TrySetCanceled(ex.CancellationToken);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        }

        if (IsCurrentThread)
        {
            Execute();
        }
        else
        {
            _queue.Add(Execute, CancellationToken.None);
        }

        return completion.Task;
    }

    /// <summary>Runs <paramref name="work"/> on the libmtp thread; see <see cref="InvokeAsync{T}(Func{T}, CancellationToken)"/>.</summary>
    public Task InvokeAsync(Action work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        return InvokeAsync(
            () =>
            {
                work();
                return true;
            },
            cancellationToken);
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
        {
            LibMtpNative.Init();
            _initialized = true;
        }
    }

    private void Run()
    {
        foreach (var item in _queue.GetConsumingEnumerable())
        {
            item();
        }
    }
}
