using FileFerry.App.Services;
using FileFerry.Core.Domain;
using FileFerry.Core.Ports;
using FileFerry.Core.Sync;
using FileFerry.Data;

namespace FileFerry.App;

/// <summary>
/// Factory for the <c>SyncRun</c> environment: pair + account + "live" token →
/// <see cref="SyncRunScope"/> with the SAF side, an indexed stash buffer, retrying WEBDAV and a
/// probe. Every missing piece of configuration yields an expected <see cref="SyncError"/>
/// and the run simply does not start; the factory never lets exceptions escape.
/// </summary>
public sealed class AndroidSyncScopeFactory : ISyncScopeFactory
{
    private readonly IFolderPairRepository _pairs;
    private readonly IAccountRepository _accounts;
    private readonly RemoteStoreFactory _stores;
    private readonly ISecretStore _secrets;
    private readonly IStashRepository _stashIndex;
    private readonly IStashPolicy _stashPolicy;
    private readonly IClock _clock;

    /// <summary>
    /// Assembles the factory from configuration, secrets, the stash index, stash policy and the clock.
    /// </summary>
    public AndroidSyncScopeFactory(
        IFolderPairRepository pairs,
        IAccountRepository accounts,
        RemoteStoreFactory stores,
        ISecretStore secrets,
        IStashRepository stashIndex,
        IStashPolicy stashPolicy,
        IClock clock)
    {
        _pairs = pairs;
        _accounts = accounts;
        _stores = stores;
        _secrets = secrets;
        _stashIndex = stashIndex;
        _stashPolicy = stashPolicy;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<Result<SyncRunScope, SyncError>> OpenAsync(Guid pairId, CancellationToken ct)
    {
        var pair = await _pairs.FindAsync(pairId, ct).ConfigureAwait(false);
        if (pair is null)
        {
            return Result<SyncRunScope, SyncError>.Failure(
                SyncError.Of(ErrorKind.NotFound, "Pair not found — an account cascade delete may have happened."));
        }

        var account = await _accounts.FindAsync(pair.AccountId, ct).ConfigureAwait(false);
        if (account is null)
        {
            return Result<SyncRunScope, SyncError>.Failure(
                SyncError.Of(ErrorKind.NotFound, $"The account of pair '{pair.DisplayName}' is missing."));
        }

        if (await _secrets.GetAsync(account.TokenRef, ct).ConfigureAwait(false) is not { Length: > 0 })
        {
            return Result<SyncRunScope, SyncError>.Failure(
                SyncError.Of(ErrorKind.AuthExpired, "The app token is missing or revoked — sign in again."));
        }

        var treeUri = Android.Net.Uri.Parse(pair.LocalTreeKey);
        if (treeUri is null)
        {
            return Result<SyncRunScope, SyncError>.Failure(
                SyncError.Of(ErrorKind.PermissionLost, "The saved local folder URI is corrupt — re-pick the folder on the pair details screen."));
        }

        // SAF grant liveness is checked before the run starts: PersistedUriPermissions
        // lists only live persistent grants — revoked access yields an honest PermissionLost
        var resolver = Android.App.Application.Context.ContentResolver;
        if (resolver is null)
        {
            return Result<SyncRunScope, SyncError>.Failure(
                SyncError.Of(ErrorKind.Unknown, "ContentResolver is unavailable."));
        }

        if (!resolver.PersistedUriPermissions.Any(p => treeUri.Equals(p.Uri)))
        {
            return Result<SyncRunScope, SyncError>.Failure(
                SyncError.Of(ErrorKind.PermissionLost, $"Access to the folder '{pair.DisplayName}' was revoked — re-pick it on the pair details screen."));
        }

        ILocalFileSystem local = new StashIndexingLocalFileSystem(
            new SafLocalFileSystem(resolver, treeUri), _stashIndex, pair.Id, _stashPolicy.StashRoot, _clock);
        return Result<SyncRunScope, SyncError>.Success(
            new SyncRunScope(pair, local, _stores.CreateStore(account), _stores.CreateProbe(account)));
    }
}
