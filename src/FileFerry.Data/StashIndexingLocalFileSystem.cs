using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;
using FileFerry.Core.Ports;

namespace FileFerry.Data;

/// <summary>
/// <see cref="ILocalFileSystem"/> decorator producing the stash-buffer index
/// (<see cref="IStashRepository"/>, invariant): a successful copy inside the
/// stash root ⇒ <see cref="IStashRepository.AddAsync"/> in the same journal step as the
/// physical copy; a copy removal (TTL-purge) ⇒ unindexing. Zero policy: the root and the
/// clock are parameters, and there is no decision here about "what" to copy — that is the
/// planner's job.
/// </summary>
public sealed class StashIndexingLocalFileSystem : ILocalFileSystem
{
    private readonly ILocalFileSystem _inner;
    private readonly IStashRepository _index;
    private readonly Guid _pairId;
    private readonly RelativePath _stashRoot;
    private readonly IClock _clock;

    /// <summary>
    /// Wrap the pair's filesystem side with indexing of its stash copies.
    /// </summary>
    /// <param name="inner">The real filesystem (SAF or plain).</param>
    /// <param name="index">Stash index the copies are registered in (same journal step as the delete).</param>
    /// <param name="pairId">The owning pair — the decorator is per pair, like the run scope.</param>
    /// <param name="stashRoot">Stash root (from <c>IStashPolicy.StashRoot</c>).</param>
    /// <param name="clock">Indexing clock (the same Composition Root clock).</param>
    public StashIndexingLocalFileSystem(
        ILocalFileSystem inner,
        IStashRepository index,
        Guid pairId,
        RelativePath stashRoot,
        IClock clock)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _stashRoot = stashRoot;
        _pairId = pairId;
        _clock = clock;
    }

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<FileSnapshot>, SyncError>> SnapshotAsync(CancellationToken ct) =>
        _inner.SnapshotAsync(ct);

    /// <inheritdoc />
    public Task<Result<Stream, SyncError>> ReadAsync(RelativePath path, CancellationToken ct) =>
        _inner.ReadAsync(path, ct);

    /// <inheritdoc />
    public Task<Result<FileSnapshot, SyncError>> WriteAsync(RelativePath path, Stream content, CancellationToken ct) =>
        IndexOnWriteAsync(path, content, ct);

    /// <inheritdoc />
    public Task<Result<Ok, SyncError>> CreateFolderAsync(RelativePath path, CancellationToken ct) =>
        _inner.CreateFolderAsync(path, ct);

    /// <inheritdoc />
    public Task<Result<Ok, SyncError>> CopyAsync(RelativePath from, RelativePath to, CancellationToken ct) =>
        IndexOnCopyAsync(from, to, ct);

    /// <inheritdoc />
    public Task<Result<Ok, SyncError>> MoveAsync(RelativePath from, RelativePath to, CancellationToken ct) =>
        _inner.MoveAsync(from, to, ct);

    /// <inheritdoc />
    public Task<Result<Ok, SyncError>> DeleteAsync(RelativePath path, CancellationToken ct) =>
        UnindexOnDeleteAsync(path, ct);

    /// <summary>
    /// A write into the stash root: the planner captures a "locally vanished" file
    /// the server via <c>DownloadToLocal → stash path</c>, i.e. a write rather than a
    /// copy — so index that too, otherwise the copy and the live file would diverge
    /// (invisible to the trash-buffer screen, purge and restore). The origin is recovered from the
    /// policy's layout: <c>root/&lt;stamp&gt;/&lt;origin&gt;</c>.
    /// </summary>
    private async Task<Result<FileSnapshot, SyncError>> IndexOnWriteAsync(
        RelativePath path, Stream content, CancellationToken ct)
    {
        var written = await _inner.WriteAsync(path, content, ct).ConfigureAwait(false);
        if (written.IsFailure || OriginFromStashLayout(path) is not { } origin)
        {
            return written;
        }

        try
        {
            await _index.AddAsync(new StashEntry(_pairId, path, origin, _clock.UtcNow), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result<FileSnapshot, SyncError>.Failure(
                SyncError.Of(ErrorKind.Unknown, $"The stash-copy index for '{path}' was not written.", ex));
        }

        return written;
    }

    /// <summary>
    /// Origin from the stash layout: strip the root and the stamp directory;
    /// anything else yields <see langword="null"/>.
    /// </summary>
    private RelativePath? OriginFromStashLayout(RelativePath path)
    {
        if (!IsStashPath(path) || path.Depth <= _stashRoot.Depth + 1)
        {
            return null; // not stash, or no origin segment (the root itself / stamp level)
        }

        var segments = path.Segments;
        return RelativePath.Parse(string.Join("/", segments.Skip(_stashRoot.Segments.Count + 1)));
    }

    /// <summary>
    /// A copy inside the stash root: after physical success comes the index; an index
    /// failure fails the step (the executor retries idempotently).
    /// </summary>
    private async Task<Result<Ok, SyncError>> IndexOnCopyAsync(RelativePath from, RelativePath to, CancellationToken ct)
    {
        var copied = await _inner.CopyAsync(from, to, ct).ConfigureAwait(false);
        if (copied.IsFailure || !IsStashPath(to))
        {
            return copied;
        }

        try
        {
            await _index.AddAsync(new StashEntry(_pairId, to, from, _clock.UtcNow), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result<Ok, SyncError>.Failure(
                SyncError.Of(ErrorKind.Unknown, $"The stash-copy index for '{to}' was not written.", ex));
        }

        return copied;
    }

    /// <summary>
    /// A copy removal (purge/restore): after physical success comes unindexing.
    /// </summary>
    private async Task<Result<Ok, SyncError>> UnindexOnDeleteAsync(RelativePath path, CancellationToken ct)
    {
        var deleted = await _inner.DeleteAsync(path, ct).ConfigureAwait(false);
        if (deleted.IsFailure || !IsStashPath(path))
        {
            return deleted;
        }

        try
        {
            await _index.RemoveAsync(_pairId, path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result<Ok, SyncError>.Failure(
                SyncError.Of(ErrorKind.Unknown, $"The stash-copy index for '{path}' was not removed.", ex));
        }

        return deleted;
    }

    /// <summary>
    /// Whether the path lies inside the pair's stash buffer (the root itself included).
    /// </summary>
    private bool IsStashPath(RelativePath path) =>
        path.EqualsIgnoreCase(_stashRoot) || path.StartsWith(_stashRoot);
}
