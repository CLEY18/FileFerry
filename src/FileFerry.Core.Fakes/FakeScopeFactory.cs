using FileFerry.Core.Domain;
using FileFerry.Core.Ports;

namespace FileFerry.Core.Fakes;

/// <summary>
/// Probe with a fixed response — the availability gate in early tests and "server
/// vanished" scenarios.
/// </summary>
public sealed class FixedProbe : IAvailabilityProbe
{
    /// <summary>
    /// Answer to every <c>ProbeAsync</c>; a test rewrites it mid-run to simulate "server
    /// vanished".
    /// </summary>
    public Result<ServerStatus, SyncError> Response { get; set; } = Result<ServerStatus, SyncError>
        .Success(new ServerStatus("30.0.0", MaintenanceMode: false));

    /// <summary>
    /// Call count — for the "a dead server generates no IO" assertion.
    /// </summary>
    public int ProbeCount { get; private set; }

    /// <inheritdoc cref="IAvailabilityProbe.ProbeAsync"/>
    public Task<Result<ServerStatus, SyncError>> ProbeAsync(CancellationToken ct)
    {
        ProbeCount++;
        return Task.FromResult(Response);
    }
}

/// <summary>
/// In-memory <see cref="ISyncScopeFactory"/>: pre-staged pair environments plus an open
/// counter (gate and race tests) and a controllable open failure.
/// </summary>
public sealed class FakeSyncScopeFactory : ISyncScopeFactory
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, SyncRunScope> _scopes = new();

    /// <summary>
    /// Register a ready-made pair environment (the test seeds both sides itself).
    /// </summary>
    public void Add(SyncRunScope scope)
    {
        lock (_gate)
        {
            _scopes[scope.Pair.Id] = scope;
        }
    }

    /// <summary>
    /// If set, every open fails (simulates a lost pair or token).
    /// </summary>
    public SyncError? OpenFailure { get; set; }

    /// <summary>
    /// Successful opens — for the "the second run never reached IO" assertion.
    /// </summary>
    public int OpenCount { get; private set; }

    /// <inheritdoc cref="ISyncScopeFactory.OpenAsync"/>
    public Task<Result<SyncRunScope, SyncError>> OpenAsync(Guid pairId, CancellationToken ct)
    {
        lock (_gate)
        {
            if (OpenFailure is { } failure)
            {
                return Task.FromResult(Result<SyncRunScope, SyncError>.Failure(failure));
            }

            if (!_scopes.TryGetValue(pairId, out var scope))
            {
                return Task.FromResult(Result<SyncRunScope, SyncError>.Failure(
                    SyncError.Of(ErrorKind.NotFound, $"Pair '{pairId}' is not in the configuration.")));
            }

            OpenCount++;
            return Task.FromResult(Result<SyncRunScope, SyncError>.Success(scope));
        }
    }
}
