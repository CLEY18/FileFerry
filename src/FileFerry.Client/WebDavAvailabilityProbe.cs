using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using FileFerry.Core.Domain;
using FileFerry.Core.Ports;

namespace FileFerry.Client;

/// <summary>
/// Probe <c>GET {base}/status.php</c> — a cheap "server alive" signal (<see cref="IAvailabilityProbe"/>).
/// Transport failures/timeouts → <see cref="ErrorKind.ServerGone"/>; an HTTP error that still carries a
/// response means the server answers, so it has not "gone" (success with the version from the body,
/// or Unknown when the body could not be parsed).
/// </summary>
/// <param name="http">Configured HttpClient (base address + probe timeout are set in the Composition Root).</param>
public sealed class WebDavAvailabilityProbe(HttpClient http) : IAvailabilityProbe
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <inheritdoc cref="IAvailabilityProbe.ProbeAsync"/>
    public async Task<Result<ServerStatus, SyncError>> ProbeAsync(CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync("status.php", HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

            var payload = await JsonSerializer.DeserializeAsync<StatusResponse>(body, Json, ct).ConfigureAwait(false);
            if (payload is null || string.IsNullOrWhiteSpace(payload.Version))
            {
                return Result<ServerStatus, SyncError>.Failure(SyncError.Of(
                    ErrorKind.Unknown, $"status.php answered unexpectedly (HTTP {(int)response.StatusCode})."));
            }

            return Result<ServerStatus, SyncError>.Success(new ServerStatus(payload.Version, payload.Maintenance));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Result<ServerStatus, SyncError>.Failure(SyncError.Of(ErrorKind.ServerGone, "status.php did not answer in time."));
        }
        catch (HttpRequestException ex)
        {
            // diagnostic, not a label: the localized label comes from ErrorKind at render
            return Result<ServerStatus, SyncError>.Failure(SyncError.Of(
                ErrorKind.ServerGone, $"status.php request failed: {ex.Message}", ex));
        }
        catch (JsonException ex)
        {
            return Result<ServerStatus, SyncError>.Failure(SyncError.Of(ErrorKind.Unknown, "The status.php response could not be parsed.", ex));
        }
    }

    /// <summary>
    /// The real Nextcloud <c>status.php</c>: top-level fields only (34.0.3 has no <c>data</c>
    /// node — that shape belongs to the OCS API, not status.php).
    /// </summary>
    private sealed record StatusResponse(
        [property: JsonPropertyName("version")] string? Version,
        [property: JsonPropertyName("maintenance")] bool Maintenance);
}
