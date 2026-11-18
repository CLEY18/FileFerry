using System.IO;
using Android.Content;
using Android.Provider;
using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;
using FileFerry.Core.Ports;

namespace FileFerry.App;

/// <summary>
/// SAF implementation of <see cref="ILocalFileSystem"/> over the persistable tree URI 
/// <c>AndroidSafFolderPicker</c>. Enumeration uses <c>BuildChildDocumentsUri</c>: one
/// ContentResolver query per directory (batch), no N+1 per file. Writes go through a tmp document
/// + rename (the most atomicity SAF offers: there is no true rename-over-whole — the window
/// between delete-old and rename-new is closed by the planner's stash side).
/// Zero policy: no filters, no stash logic — that is the engine's job.
/// </summary>
public sealed class SafLocalFileSystem : ILocalFileSystem
{
    /// <summary>
    /// MIME type of a directory in SAF. The canonical value
    /// <c>DocumentsContract.Document.MIME_TYPE_DIR</c> is "vnd.android.document/directory"; given
    /// the wrong "application/vnd.…" the provider silently downgrades it to a zero-byte file
    /// (Android 16: the stash folder was born as a file, SnapshotAsync saw directories as files).
    /// </summary>
    private const string DirMimeType = "vnd.android.document/directory";

    private readonly ContentResolver _resolver;
    private readonly Android.Net.Uri _treeUri;
    private readonly string _authority;
    private readonly string _rootDocId;

    /// <summary>
    /// Creates the file system over a persistable tree URI (read|write grant issued by the picker).
    /// </summary>
    /// <param name="treeUri">Tree URI of the pair root (<c>LocalTreeKey</c>).</param>
    public SafLocalFileSystem(ContentResolver resolver, Android.Net.Uri treeUri)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _treeUri = treeUri ?? throw new ArgumentNullException(nameof(treeUri));
        _authority = treeUri.Authority ?? throw new ArgumentException("Tree-URI without authority.", nameof(treeUri));
        _rootDocId = DocumentsContract.GetTreeDocumentId(treeUri)?.ToString()
                     ?? throw new ArgumentException("Tree-URI without document-id.", nameof(treeUri));
    }

    /// <summary>
    /// Shape of one child from a single batch query: id, name, type, size, time.
    /// </summary>
    /// <param name="Name">DisplayName — the canonical relative-path segment here.</param>
    /// <param name="IsDirectory">Directory, decided by MIME.</param>
    /// <param name="Size">File size (0 for directories or unknown).</param>
    /// <param name="LastModifiedMs">Last modification, ms since epoch (0 — provider did not report it).</param>
    private sealed record Node(string DocId, string Name, bool IsDirectory, long Size, long LastModifiedMs);

    /// <inheritdoc cref="ILocalFileSystem.SnapshotAsync" />
    public Task<Result<IReadOnlyList<FileSnapshot>, SyncError>> SnapshotAsync(CancellationToken ct) =>
        GuardAsync<IReadOnlyList<FileSnapshot>>(_rootDocId, () =>
            Task.Run<Result<IReadOnlyList<FileSnapshot>, SyncError>>(() =>
            {
                var snapshots = new List<FileSnapshot>();
                var frontier = new Stack<(string DocId, RelativePath Prefix)>();
                frontier.Push((_rootDocId, RelativePath.Empty));
                while (frontier.Count > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    var (docId, prefix) = frontier.Pop();
                    foreach (var node in QueryChildren(docId, ct))
                    {
                        var relative = prefix.Combine(node.Name);
                        snapshots.Add(node.IsDirectory
                            ? new FileSnapshot(relative, EntryKind.Directory, 0)
                            : new FileSnapshot(
                                relative,
                                EntryKind.File,
                                node.Size,
                                node.LastModifiedMs > 0
                                    ? DateTimeOffset.FromUnixTimeMilliseconds(node.LastModifiedMs).ToUniversalTime()
                                    : null));
                        if (node.IsDirectory)
                        {
                            frontier.Push((node.DocId, relative));
                        }
                    }
                }

                snapshots.Sort(static (a, b) => a.Path.CompareTo(b.Path));
                return Result<IReadOnlyList<FileSnapshot>, SyncError>.Success(snapshots);
            }, ct),
            ct);

    /// <inheritdoc cref="ILocalFileSystem.ReadAsync" />
    public Task<Result<Stream, SyncError>> ReadAsync(RelativePath path, CancellationToken ct) =>
        GuardAsync(path, () => Task.Run<Result<Stream, SyncError>>(() =>
        {
            var node = Resolve(path, ct) ?? throw NotFound(path);
            if (node.IsDirectory)
            {
                throw DirExpected(path);
            }

            var stream = _resolver.OpenInputStream(DocUri(node.DocId))
                         ?? throw NotFound(path); // the provider may have removed the entry between queries — a race also yields Not Found
            return Result<Stream, SyncError>.Success(stream);
        }, ct), ct);

    /// <inheritdoc cref="ILocalFileSystem.WriteAsync" />
    /// <remarks>
    /// A tmp document in the same directory + <c>renameDocument</c> to the canonical name — the most
    /// atomic thing SAF allows. Replacing an existing file: delete+rename with a micro-window
    /// (data protection — stash before delete at the planner level, not here).
    /// </remarks>
    public Task<Result<FileSnapshot, SyncError>> WriteAsync(RelativePath path, Stream content, CancellationToken ct) =>
        GuardAsync(path, () => Task.Run<Result<FileSnapshot, SyncError>>(() =>
        {
            var existing = Resolve(path, ct);
            if (existing is { IsDirectory: true })
            {
                throw Fail(ErrorKind.Conflict, $"'{path}' is a directory, not a file.");
            }

            var parentUri = DocUri(EnsureDirectory(path.Parent, ct));
            var tmpUri = DocumentsContract.CreateDocument(_resolver, parentUri, "application/octet-stream", $".tmp-{Guid.NewGuid():N}")
                         ?? throw Fail(ErrorKind.Unknown, $"Provider refused to create the temporary file for '{path}'.");
            long written;
            try
            {
                using var output = _resolver.OpenOutputStream(tmpUri, "w")
                                   ?? throw Fail(ErrorKind.Unknown, $"Provider did not open a write stream for '{path}'.");
                written = Pump(content, output, ct);
            }
            catch (Exception)
            {
                TryDeleteDoc(tmpUri); // leave no litter; the original failure propagates upward
                throw;
            }

            if (existing is not null)
            {
                DocumentsContract.DeleteDocument(_resolver, DocUri(existing.DocId));
            }

            var renamed = DocumentsContract.RenameDocument(_resolver, tmpUri, path.Name)
                          ?? throw Fail(ErrorKind.Conflict, $"Provider rejected the temporary-file rename to '{path}'.");
            NotifyMedia(GetDocId(renamed));
            // The evidence must agree with the scan — take the provider's mtime, not UtcNow
            // (otherwise the baseline records a time SnapshotAsync can never show → eternal churn).
            var final = Resolve(path, ct);
            var modifiedAt = final is { LastModifiedMs: > 0 }
                ? DateTimeOffset.FromUnixTimeMilliseconds(final.LastModifiedMs).ToUniversalTime()
                : DateTimeOffset.UtcNow; // the provider stays silent about mtime — fall back to the clock
            return Result<FileSnapshot, SyncError>.Success(
                new FileSnapshot(path, EntryKind.File, written, modifiedAt));
        }, ct), ct);

    /// <inheritdoc cref="ILocalFileSystem.CreateFolderAsync" />
    public Task<Result<Ok, SyncError>> CreateFolderAsync(RelativePath path, CancellationToken ct) =>
        GuardAsync(path, () => Task.Run<Result<Ok, SyncError>>(() =>
        {
            EnsureDirectory(path, ct);
            return Result<Ok, SyncError>.Success();
        }, ct), ct);

    /// <inheritdoc cref="ILocalFileSystem.CopyAsync" />
    /// <remarks>
    /// SAF has no copy primitive — the source stream is pumped into a newly created target document
    /// (the source stays untouched — a stash requirement). The target is overwritten like Plain.
    /// </remarks>
    public Task<Result<FileSnapshot, SyncError>> CopyAsync(RelativePath from, RelativePath to, CancellationToken ct) =>
        GuardAsync(to, () => Task.Run<Result<FileSnapshot, SyncError>>(() =>
        {
            var source = Resolve(from, ct) ?? throw NotFound(from);
            if (source.IsDirectory)
            {
                throw Fail(ErrorKind.Conflict, $"Source '{from}' is a directory; only files are copied.");
            }

            var target = Resolve(to, ct);
            if (target is { IsDirectory: true })
            {
                throw Fail(ErrorKind.Conflict, $"Target '{to}' is a directory.");
            }

            var parentUri = DocUri(EnsureDirectory(to.Parent, ct));
            if (target is not null)
            {
                DocumentsContract.DeleteDocument(_resolver, DocUri(target.DocId)); // Plain parity: FileMode.Create — the target is overwritten
            }

            var created = DocumentsContract.CreateDocument(_resolver, parentUri, "application/octet-stream", to.Name)
                          ?? throw Fail(ErrorKind.Unknown, $"Provider refused to create '{to}'.");
            long written;
            try
            {
                using var input = _resolver.OpenInputStream(DocUri(source.DocId))
                                  ?? throw NotFound(from);
                using var output = _resolver.OpenOutputStream(created, "w")
                                   ?? throw Fail(ErrorKind.Unknown, $"Provider did not open a write stream for '{to}'.");
                written = Pump(input, output, ct);
            }
            catch (Exception)
            {
                TryDeleteDoc(created);
                throw;
            }

            NotifyMedia(GetDocId(created));
            // evidence as the provider sees the fresh copy (same rule as WriteAsync: the baseline
            // must record a time the scan can actually show, or every next run sees a phantom change)
            var fresh = Resolve(to, ct);
            var modifiedAt = fresh is { LastModifiedMs: > 0 }
                ? DateTimeOffset.FromUnixTimeMilliseconds(fresh.LastModifiedMs).ToUniversalTime()
                : DateTimeOffset.UtcNow;
            return Result<FileSnapshot, SyncError>.Success(
                new FileSnapshot(to, EntryKind.File, written, modifiedAt));
        }, ct), ct);

    /// <inheritdoc cref="ILocalFileSystem.MoveAsync" />
    /// <remarks>
    /// Reference identity is preserved: <c>moveDocument</c> (other directory) +
    /// <c>renameDocument</c> (name) — no copy+delete. An occupied target is Conflict, like Plain.
    /// </remarks>
    public Task<Result<Ok, SyncError>> MoveAsync(RelativePath from, RelativePath to, CancellationToken ct) =>
        GuardAsync(to, () => Task.Run<Result<Ok, SyncError>>(() =>
        {
            var source = Resolve(from, ct) ?? throw NotFound(from);
            if (Resolve(to, ct) is not null)
            {
                throw Fail(ErrorKind.Conflict, $"Target '{to}' already exists.");
            }

            var parentDoc = EnsureDirectory(to.Parent, ct);
            var sourceParent = from.Parent.IsEmpty ? _rootDocId : Resolve(from.Parent, ct)?.DocId ?? _rootDocId;
            var doc = DocUri(source.DocId);
            if (!string.Equals(sourceParent, parentDoc, StringComparison.Ordinal))
            {
                // moveDocument also takes the source parent (API 24): reference identity is kept
                doc = DocumentsContract.MoveDocument(_resolver, doc, DocUri(sourceParent), DocUri(parentDoc))
                      ?? throw Fail(ErrorKind.Conflict, $"Provider rejected moving '{from}' to '{to}'.");
            }

            if (!string.Equals(source.Name, to.Name, StringComparison.Ordinal))
            {
                doc = DocumentsContract.RenameDocument(_resolver, doc, to.Name)
                      ?? throw Fail(ErrorKind.Conflict, $"Provider rejected renaming '{from}' to '{to}'.");
            }

            NotifyMedia(GetDocId(doc));
            return Result<Ok, SyncError>.Success();
        }, ct), ct);

    /// <inheritdoc cref="ILocalFileSystem.DeleteAsync" />
    public Task<Result<Ok, SyncError>> DeleteAsync(RelativePath path, CancellationToken ct) =>
        GuardAsync(path, () => Task.Run<Result<Ok, SyncError>>(() =>
        {
            if (path.IsEmpty)
            {
                throw Fail(ErrorKind.Conflict, "The pair root is never deleted.");
            }

            var node = Resolve(path, ct) ?? throw NotFound(path);
            if (!DocumentsContract.DeleteDocument(_resolver, DocUri(node.DocId)))
            {
                throw Fail(ErrorKind.Conflict, $"Provider rejected deleting '{path}'.");
            }

            NotifyMedia(node.DocId); // the gallery must learn about the disappearance too (path, if it is displayed)
            return Result<Ok, SyncError>.Success();
        }, ct), ct);

    /// <summary>
    /// Provider columns for one batch directory query: id, name, type, size, last modified.
    /// </summary>
    private static readonly string[] Projection =
    [
        DocumentsContract.Document.ColumnDocumentId,
        DocumentsContract.Document.ColumnDisplayName,
        DocumentsContract.Document.ColumnMimeType,
        DocumentsContract.Document.ColumnSize,
        DocumentsContract.Document.ColumnLastModified,
    ];

    /// <summary>
    /// Children of a directory from a single batch ChildDocuments URI query (cursor closed early).
    /// </summary>
    private List<Node> QueryChildren(string parentDocId, CancellationToken ct)
    {
        var nodes = new List<Node>();
        // Only the tree variant: the raw BuildChildDocumentsUri is available to the provider's owner
        // and throws SecurityException for a persistable tree grant.
        var childUri = DocumentsContract.BuildChildDocumentsUriUsingTree(_treeUri, parentDocId)
                       ?? throw Fail(ErrorKind.Unknown, "Provider did not build a ChildDocuments URI.");
        var cursor = _resolver.Query(childUri, Projection, null, null, null);
        if (cursor is null)
        {
            return nodes; // the provider did not answer — empty directory (a misbehaving provider throws instead — the guard maps it)
        }

        try
        {
            while (cursor.MoveToNext())
            {
                ct.ThrowIfCancellationRequested();
                nodes.Add(new Node(
                    cursor.GetString(0) ?? string.Empty,
                    cursor.GetString(1) ?? string.Empty,
                    string.Equals(cursor.GetString(2), DirMimeType, StringComparison.Ordinal),
                    cursor.IsNull(3) ? 0 : cursor.GetLong(3),
                    cursor.IsNull(4) ? 0 : cursor.GetLong(4)));
            }
        }
        finally
        {
            cursor.Close();
            (cursor as IDisposable)?.Dispose();
        }

        return nodes;
    }

    /// <summary>
    /// Node by relative path; <see langword="null"/> — the entry is absent. Descends segment
    /// segment (one batch query per directory).
    /// </summary>
    private Node? Resolve(RelativePath path, CancellationToken ct)
    {
        if (path.IsEmpty)
        {
            return new Node(_rootDocId, string.Empty, true, 0, 0);
        }

        var currentDocId = _rootDocId;
        Node? found = null;
        foreach (var segment in path.Segments)
        {
            found = FindChild(currentDocId, segment, ct);
            if (found is null)
            {
                return null;
            }

            currentDocId = found.DocId;
        }

        return found;
    }

    /// <summary>
    /// Child with an exact (Ordinal) name; SAF DisplayName is the canonical path segment.
    /// </summary>
    private Node? FindChild(string parentDocId, string name, CancellationToken ct)
    {
        foreach (var node in QueryChildren(parentDocId, ct))
        {
            if (string.Equals(node.Name, name, StringComparison.Ordinal))
            {
                return node;
            }
        }

        return null;
    }

    /// <summary>
    /// Guarantees the directory (creates missing segments); idempotent. A file on the path is
    /// Conflict, like Plain.
    /// </summary>
    private string EnsureDirectory(RelativePath directory, CancellationToken ct)
    {
        var current = _rootDocId;
        foreach (var segment in directory.Segments)
        {
            var found = FindChild(current, segment, ct);
            if (found is { IsDirectory: true })
            {
                current = found.DocId;
                continue;
            }

            if (found is not null)
            {
                throw Fail(ErrorKind.Conflict, $"'{segment}' is a file, not a directory.");
            }

            var created = DocumentsContract.CreateDocument(_resolver, DocUri(current), DirMimeType, segment)
                          ?? throw Fail(ErrorKind.Unknown, $"Provider refused to create the directory '{segment}'.");
            var createdId = GetDocId(created) ?? throw Fail(ErrorKind.Unknown, $"Provider returned no document-id for the directory '{segment}'.");
            // The result is validated by a reverse lookup — a provider that misreads the MIME (a
            // zero-byte file instead of a folder) must stop the run before side effects, not poison the rest of the tree.
            var createdNode = FindChild(current, segment, ct);
            if (createdNode is not { IsDirectory: true })
            {
                TryDeleteDoc(DocUri(createdId));
                throw Fail(ErrorKind.Conflict, $"Provider created '{segment}' as a non-directory — the folder is unavailable for sync.");
            }

            current = createdNode.DocId;
        }

        return current;
    }

    /// <summary>
    /// Document URI for an id in this same provider, in tree form ("/tree/{tree}/document/{doc}") —
    /// the form create/delete/rename/move accept with the folder's persistable grant; the raw
    /// "/document/{doc}" raises SecurityException on foreign providers.
    /// The UsingTree variant keeps the tree segment from the original picker URI;
    /// BuildTreeDocumentUri (authority + docId only) breaks CreateDocument with "Invalid URI"
    /// (ExternalStorageProvider).
    /// </summary>
    private Android.Net.Uri DocUri(string docId) =>
        DocumentsContract.BuildDocumentUriUsingTree(_treeUri, docId)!;

    /// <summary>
    /// Document-id from a URI (nullable-safe).
    /// </summary>
    private static string? GetDocId(Android.Net.Uri? documentUri) =>
        documentUri is null ? null : DocumentsContract.GetDocumentId(documentUri)?.ToString();

    /// <summary>
    /// Best-effort removal of the tmp document after a failed attempt (the primary failure stays primary).
    /// </summary>
    private void TryDeleteDoc(Android.Net.Uri uri)
    {
        try
        {
            DocumentsContract.DeleteDocument(_resolver, uri);
        }
        catch (Java.Lang.Throwable)
        {
            // the marked tmp will be cleaned by the next run or by the provider
        }
    }

    /// <summary>
    /// The gallery learns about the change (best-effort MediaScanner); a scanner error is not the user's problem.
    /// </summary>
    private void NotifyMedia(string? docId)
    {
        if (TryRealPath(docId) is not { } realPath)
        {
            return;
        }

        try
        {
            Android.Media.MediaScannerConnection.ScanFile(Android.App.Application.Context, [realPath], null, null);
        }
        catch (Java.Lang.Throwable)
        {
            // the scanner is unavailable — the data on disk is already consistent
        }
    }

    /// <summary>
    /// The plain file system outside SAF for external storage: <c>primary:Docs/file</c> →
    /// <c>/storage/emulated/0/Docs/file</c>. Other providers (SD, cloud, media) do not reveal a real
    /// path — <see langword="null"/>.
    /// </summary>
    private string? TryRealPath(string? docId)
    {
        if (docId is not { Length: > 0 }
            || !_authority.Equals("com.android.externalstorage.documents", StringComparison.Ordinal)
            || !docId.StartsWith("primary:", StringComparison.Ordinal))
        {
            return null;
        }

        // Fallback root of the primary volume. This binding has no GetDirectory(String);
        // ExternalStorageDirectory is deprecated for ACCESS, but here it is only a hint string for
        // the media scanner (access itself goes through the SAF URI).
#pragma warning disable CS0618 // type or member is obsolete
        if (Android.OS.Environment.ExternalStorageDirectory?.Path is not { Length: > 0 } root)
#pragma warning restore CS0618
        {
            return null;
        }

        return root + "/" + docId["primary:".Length..].Replace(':', '/');
    }

    /// <summary>
    /// Stream pumping with byte counting and cancellation (SAF streams are blocking — pool thread).
    /// </summary>
    private static long Pump(Stream source, Stream destination, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            destination.Write(buffer, 0, read);
            total += read;
        }

        destination.Flush();
        return total;
    }

    /// <summary>
    /// Expected failure of category <paramref name="kind"/> — raised to the guard as SafFailureException.
    /// </summary>
    private static SafFailureException Fail(ErrorKind kind, string message) => new(SyncError.Of(kind, message));

    /// <summary>
    /// Canonical "not found" (the same text as Plain — the contracts match).
    /// </summary>
    private static SafFailureException NotFound(RelativePath path) =>
        new(SyncError.Of(ErrorKind.NotFound, $"Entry '{(path.IsEmpty ? "." : path)}' not found."));

    /// <summary>
    /// Failure for reading a directory as a file.
    /// </summary>
    private static SafFailureException DirExpected(RelativePath path) =>
        new(SyncError.Of(ErrorKind.Conflict, $"'{path}' is a directory, not a file."));

    /// <summary>
    /// Marker of a full storage in a Java exception message (ENOSPC arrives as text).
    /// </summary>
    private static bool LowSpace(Java.IO.IOException io) =>
        (io.Message ?? string.Empty).Contains("No space left", StringComparison.OrdinalIgnoreCase)
        || (io.Message ?? string.Empty).Contains("ENOSPC", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Guard: work on a pool thread; expected failures become Result, unexpected ones go through exception mapping.
    /// </summary>
    private static Task<Result<T, SyncError>> GuardAsync<T>(RelativePath what, Func<Task<Result<T, SyncError>>> action, CancellationToken ct) =>
        GuardAsync(what.IsEmpty ? "." : what.ToString(), action, ct);

    /// <summary>
    /// Guard: work inside a pool thread; expected failures (SafFailureException) → Result,
    /// unexpected exceptions → the mapping of <see cref="FromException{TValue}"/>.
    /// </summary>
    private static async Task<Result<T, SyncError>> GuardAsync<T>(string what, Func<Task<Result<T, SyncError>>> action, CancellationToken ct)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (SafFailureException fail)
        {
            return Result<T, SyncError>.Failure(fail.Error);
        }
        catch (Exception ex)
        {
            return FromException<T>(ex, what);
        }
    }

    /// <summary>
    /// Mapping of SAF/platform exceptions to <see cref="ErrorKind"/> (matches Plain: cancellation →
    /// Network, Security → PermissionLost).
    /// </summary>
    private static Result<TValue, SyncError> FromException<TValue>(Exception ex, string what) => ex switch
    {
        OperationCanceledException => Result<TValue, SyncError>.Failure(SyncError.Of(ErrorKind.Network, "Cancelled.", ex)),
        Java.Lang.SecurityException => Result<TValue, SyncError>.Failure(SyncError.Of(ErrorKind.PermissionLost, $"SAF access to '{what}' was lost.", ex)),
        System.Security.SecurityException => Result<TValue, SyncError>.Failure(SyncError.Of(ErrorKind.PermissionLost, $"SAF access to '{what}' was lost.", ex)),
        Java.IO.IOException io when LowSpace(io) => Result<TValue, SyncError>.Failure(SyncError.Of(ErrorKind.StorageFull, $"Storage is full: '{what}'.", io)),
        Java.IO.IOException => Result<TValue, SyncError>.Failure(SyncError.Of(ErrorKind.Network, $"I/O error for '{what}'.", ex)),
        Java.Lang.IllegalArgumentException => Result<TValue, SyncError>.Failure(SyncError.Of(ErrorKind.Conflict, $"Provider rejected the operation on '{what}' (invalid document).", ex)),
        UnauthorizedAccessException => Result<TValue, SyncError>.Failure(SyncError.Of(ErrorKind.PermissionLost, $"No access to '{what}'.", ex)),
        IOException => Result<TValue, SyncError>.Failure(SyncError.Of(ErrorKind.Network, $"I/O error for '{what}'.", ex)),
        _ => Result<TValue, SyncError>.Failure(SyncError.Of(ErrorKind.Unknown, $"Unexpected SAF error for '{what}'.", ex)),
    };

    /// <summary>
    /// Carrier of an expected failure: the adapter raises a classified SyncError as an exception only for the guard.
    /// </summary>
    private sealed class SafFailureException(SyncError error) : Exception(error.Message)
    {
        /// <summary>
        /// Classified failure to surface as a Result.
        /// </summary>
        public SyncError Error { get; } = error;
    }
}
