using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using AndroidX.Activity.Result;
using AndroidX.Activity.Result.Contract;
using AndroidX.Core.View;
using FileFerry.App.Services;
using AndroidView = Android.Views.View;

namespace FileFerry.App;

/// <summary>
/// Main Activity: SingleTop + configuration changes handled by the system, so a screen rotation
/// neither restarts the navigation stack nor cancels an active run.
/// <para>
/// The SAF folder-picker launcher (<see cref="ActivityResultContracts.OpenDocumentTree"/>) is
/// registered here too: registration is mandatory before <c>OnStart</c>, hence it happens 
/// <see cref="OnCreate"/>. Registration happens once per Activity; requests may follow in any
/// sequence (one pending at a time).
/// </para>
/// </summary>
[Activity(
    MainLauncher = true,
    // MAUI's Material theme (a Theme.AppCompat descendant): without it the Shell flyout crashes on
    // AppBarLayout — "requires your app theme to be Theme.AppCompat".
    // The NoActionBar variant: the base Maui.MainTheme carries an AppCompat ActionBar that drew a
    // parasitic "FileFerry" row above the content (action_bar_container visible in the UI dump).
    Theme = "@style/Maui.MainTheme.NoActionBar",
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode
        | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public sealed class MainActivity : MauiAppCompatActivity
{
    /// <summary>
    /// Pending folder-picker result (at most one at a time).
    /// </summary>
    private static TaskCompletionSource<PickedFolder?>? _pendingTreePick;

    /// <summary>
    /// OpenDocumentTree launcher; available after <see cref="OnCreate"/>.
    /// </summary>
    internal ActivityResultLauncher? TreeLauncher { get; private set; }

    /// <summary>
    /// Creates the SAF folder-picker launcher, binds the callback to the bridge and closes the bottom inset.
    /// </summary>
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        ApplyBottomBarInset();
        // a fresh toolbar (first start, light/dark recreation) must wear the current tokens at once
        Window?.DecorView.Post(AndroidAppBar.Repaint);
        TreeLauncher = RegisterForActivityResult(
            new ActivityResultContracts.OpenDocumentTree(),
            new TreePickCallback(this));
    }

    /// <summary>
    /// After the move to NoActionBar, the top inset (status bar) is held correctly by Shell's own
    /// AppBarLayout, but the Android gesture bar still floats over content (the FAB slips under the
    /// pill). We pad the decorView on the bottom and sides only (landscape navigation); the top is
    /// deliberately zero, otherwise there would be double padding under the status bar.
    /// IME is out of scope: the keyboard is MAUI's responsibility.
    /// </summary>
    private void ApplyBottomBarInset()
    {
        if (Window is null)
        {
            return;
        }

        var decor = Window.DecorView;
        ViewCompat.SetOnApplyWindowInsetsListener(decor, new BottomBarPaddingListener());
        ViewCompat.RequestApplyInsets(decor);
    }

    /// <summary>
    /// Pads the decorView by the navigation bar (bottom/sides), never by the status bar.
    /// </summary>
    private sealed class BottomBarPaddingListener : Java.Lang.Object, IOnApplyWindowInsetsListener
    {
        /// <summary>
        /// Height of the system back-swipe strip Android reserves (48dp): taps in it are unreliable
        /// because the system intercepts the back gesture — a dialog action such as "Delete pair" would not respond.
        /// </summary>
        private const float BackSwipeStripDp = 48f;

        public WindowInsetsCompat? OnApplyWindowInsets(AndroidView? view, WindowInsetsCompat? insets)
        {
            if (view is null || insets is null)
            {
                return insets;
            }

            var bars = insets.GetInsets(WindowInsetsCompat.Type.SystemBars());
            if (bars is not null)
            {
                var density = view.Resources?.DisplayMetrics?.Density ?? 1f;
                var backStrip = (int)(BackSwipeStripDp * density + 0.5f);
                view.SetPadding(bars.Left, 0, bars.Right, bars.Bottom + backStrip);
            }

            return insets;
        }
    }

    /// <summary>
    /// Java bridge for the ActivityResult callback (the launcher requires an IActivityResultCallback instance).
    /// </summary>
    private sealed class TreePickCallback : Java.Lang.Object, IActivityResultCallback
    {
        private readonly MainActivity _owner;

        public TreePickCallback(MainActivity owner) => _owner = owner;

        public void OnActivityResult(Java.Lang.Object? result) => CompleteTreePick(_owner, result as Android.Net.Uri);
    }

    /// <summary>
    /// Completes the pending pick: persist grant + displayName → <see cref="PickedFolder"/>.
    /// </summary>
    private static void CompleteTreePick(MainActivity activity, Android.Net.Uri? treeUri)
    {
        var pending = Interlocked.Exchange(ref _pendingTreePick, null);
        if (pending is null)
        {
            return; // nobody was waiting (e.g. the Activity was recreated) — nothing to deliver
        }

        if (treeUri is null)
        {
            pending.TrySetResult(null); // the user cancelled
            return;
        }

        try
        {
            var resolver = activity.ContentResolver
                ?? throw new InvalidOperationException("Activity without a ContentResolver — the Android runtime is broken.");
            resolver.TakePersistableUriPermission(
                treeUri,
                ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
            pending.TrySetResult(new PickedFolder(treeUri.ToString() ?? string.Empty, ReadDisplayName(resolver, treeUri)));
        }
        catch (Exception ex)
        {
            pending.TrySetException(ex); // a broken grant is a platform/ROM bug — we do not hide it
        }
    }

    /// <summary>
    /// Human-readable folder name: query DisplayName, fallback — the last segment of the
    /// tree-document-id. Some providers do not answer a query on tree URIs — an expected boundary,
    /// so nothing is thrown upward.
    /// </summary>
    private static string ReadDisplayName(Android.Content.ContentResolver resolver, Android.Net.Uri treeUri)
    {
        try
        {
            using var cursor = resolver.Query(
                treeUri, [Android.Provider.IOpenableColumns.DisplayName], null, null, null);
            if (cursor is not null && cursor.MoveToFirst() && cursor.GetString(0) is { Length: > 0 } name)
            {
                return name;
            }
        }
        catch (Exception)
        {
            // the provider does not answer a query for tree URIs — fall through to the fallback
        }

        var documentId = Android.Provider.DocumentsContract.GetTreeDocumentId(treeUri) ?? string.Empty;
        var colon = documentId.LastIndexOf(':');
        var path = colon >= 0 ? documentId[(colon + 1)..] : documentId;
        return path.TrimEnd('/').Split('/')[^1] is { Length: > 0 } tail ? tail : documentId;
    }

    /// <summary>
    /// Prepares the TCS awaiting the picker result (used by <c>AndroidSafFolderPicker</c>).
    /// </summary>
    internal static TaskCompletionSource<PickedFolder?> PrepareTreePick()
    {
        var tcs = new TaskCompletionSource<PickedFolder?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingTreePick = tcs;
        return tcs;
    }
}
