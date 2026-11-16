using System.Threading.Channels;

namespace FileFerry.Data.Sqlite;

/// <summary>
/// SQLite's single writer via <c>System.Threading.Channels</c>: every change
/// lines up in one queue and executes sequentially — no baseline/journal reordering.
/// A <see cref="RunAsync"/> call returns only after the write actually happened
/// (durability "before return").
/// </summary>
public sealed class WriteQueue : IAsyncDisposable
{
    private readonly Channel<WorkItem> _queue = Channel.CreateUnbounded<WorkItem>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _pump;

    /// <summary>
    /// Create the queue and start its reading executor.
    /// </summary>
    public WriteQueue() => _pump = Task.Run(PumpAsync);

    /// <summary>
    /// Enqueue one write unit and await its execution (or that write's failure).
    /// </summary>
    public Task RunAsync(Func<Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return _queue.Writer.TryWrite(new WorkItem(work, completion))
            ? completion.Task
            : Task.FromException(new ObjectDisposedException(nameof(WriteQueue)));
    }

    /// <summary>
    /// Complete the queue and await the drained tail.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        return new ValueTask(_pump);
    }

    /// <summary>
    /// Sequential executor: one write failure does not kill the queue — it reaches only
    /// its own caller.
    /// </summary>
    private async Task PumpAsync()
    {
        await foreach (var item in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await item.Work().ConfigureAwait(false);
                item.Result.SetResult();
            }
            catch (Exception exception)
            {
                item.Result.SetException(exception);
            }
        }
    }

    /// <summary>
    /// Unit: the work plus its own completion.
    /// </summary>
    private readonly record struct WorkItem(Func<Task> Work, TaskCompletionSource Result);
}
