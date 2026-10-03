using System.Collections.Concurrent;

namespace Gear360.Mtp.Windows;

/// <summary>
/// A task scheduler that runs every task, one at a time, on a single dedicated STA thread.
/// All Windows Portable Devices calls go through it, so callers on any thread (including the
/// thread pool) never touch the COM objects directly and device operations never overlap.
/// </summary>
internal sealed class StaTaskScheduler : TaskScheduler, IDisposable
{
    private static readonly Lazy<StaTaskScheduler> SharedInstance = new(() => new StaTaskScheduler("Gear360 WPD worker"));

    private readonly BlockingCollection<Task> _queue = new();
    private readonly Thread _thread;

    /// <summary>Starts the worker thread.</summary>
    /// <param name="threadName">Name shown for the thread in debuggers.</param>
    public StaTaskScheduler(string threadName)
    {
        _thread = new Thread(RunLoop)
        {
            IsBackground = true,
            Name = threadName,
        };
        if (OperatingSystem.IsWindows())
        {
            _thread.SetApartmentState(ApartmentState.STA);
        }

        _thread.Start();
    }

    /// <summary>The process-wide worker used for all WPD access. It lives until the process exits.</summary>
    public static StaTaskScheduler Shared => SharedInstance.Value;

    /// <summary>The managed thread id of the worker thread.</summary>
    public int ThreadId => _thread.ManagedThreadId;

    /// <inheritdoc />
    public override int MaximumConcurrencyLevel => 1;

    /// <summary>Runs <paramref name="function"/> on the worker thread.</summary>
    /// <exception cref="ObjectDisposedException">The scheduler has been disposed.</exception>
    public Task<T> RunAsync<T>(Func<T> function, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(function);
        ObjectDisposedException.ThrowIf(_queue.IsAddingCompleted, this);
        try
        {
            return Task.Factory.StartNew(function, cancellationToken, TaskCreationOptions.DenyChildAttach, this);
        }
        catch (TaskSchedulerException ex) when (_queue.IsAddingCompleted)
        {
            throw new ObjectDisposedException(nameof(StaTaskScheduler), ex);
        }
    }

    /// <summary>Runs <paramref name="action"/> on the worker thread.</summary>
    /// <exception cref="ObjectDisposedException">The scheduler has been disposed.</exception>
    public Task RunAsync(Action action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return RunAsync(
            () =>
            {
                action();
                return true;
            },
            cancellationToken);
    }

    /// <summary>Stops accepting work, lets queued tasks finish and waits for the thread to exit.</summary>
    public void Dispose()
    {
        _queue.CompleteAdding();
        if (Environment.CurrentManagedThreadId != _thread.ManagedThreadId)
        {
            _thread.Join();
        }
    }

    /// <inheritdoc />
    protected override void QueueTask(Task task) => _queue.Add(task);

    /// <inheritdoc />
    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) =>
        Environment.CurrentManagedThreadId == _thread.ManagedThreadId && TryExecuteTask(task);

    /// <inheritdoc />
    protected override IEnumerable<Task> GetScheduledTasks() => _queue.ToArray();

    private void RunLoop()
    {
        foreach (var task in _queue.GetConsumingEnumerable())
        {
            TryExecuteTask(task);
        }
    }
}
