using FileFerry.App.Services;
using Microsoft.Maui.ApplicationModel;

namespace FileFerry.App;

/// <summary>
/// Android adapter for <see cref="ILocalFolderPicker"/>: SAF <c>OPEN_DOCUMENT_TREE</c> through a
/// launcher registered in <see cref="MainActivity.OnCreate"/>; the persist read|write grant is
/// issued by <c>MainActivity</c>, so the <c>TreeUri</c> is directly usable as the <c>LocalTreeKey</c>.
/// </summary>
public sealed class AndroidSafFolderPicker : ILocalFolderPicker
{
    /// <inheritdoc />
    public async Task<PickedFolder?> PickAsync(CancellationToken ct)
    {
        if (Platform.CurrentActivity is not MainActivity { TreeLauncher: { } } activity)
        {
            return null; // Activity not yet / no longer available — cancel the pick without blowing up the UI
        }

        var pending = MainActivity.PrepareTreePick();
        using (ct.Register(() => pending.TrySetCanceled(ct)))
        {
            activity.TreeLauncher.Launch((Java.Lang.Object?)null); // initial folder — left to the SAF default
            return await pending.Task.ConfigureAwait(false);
        }
    }
}
