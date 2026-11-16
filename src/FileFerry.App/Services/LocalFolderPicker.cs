namespace FileFerry.App.Services;

/// <summary>
/// Result of the system folder pick: the persistable tree URI (the <c>LocalTreeKey</c> key) + a
/// name for the UI.
/// </summary>
/// <param name="TreeUri">Full <c>content://…tree…</c> URI with the persist grant (consumed as the tree identifier).</param>
/// <param name="DisplayName">Folder name as the provider answered at pick time: shown on the wizard step
/// that asked for the folder. Screens later re-read the name from the persisted tree key, so this value
/// lives only for the moment of choosing.</param>
public sealed record PickedFolder(string TreeUri, string DisplayName);

/// <summary>
/// Port for the system folder picker (add-pair wizard step 2): declared by the consumer (the wizard VM); the
/// Android adapter is SAF <c>OpenDocumentTree</c> in Platforms/Android. Deliberately policy-free:
/// whether this folder may be picked is a question for the VM/engine, not for the picker.
/// </summary>
public interface ILocalFolderPicker
{
    /// <summary>
    /// Open the system picker; <see langword="null"/> — the user cancelled.
    /// </summary>
    Task<PickedFolder?> PickAsync(CancellationToken ct);
}
