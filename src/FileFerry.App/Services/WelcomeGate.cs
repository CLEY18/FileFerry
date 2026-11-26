namespace FileFerry.App.Services;

/// <summary>
/// One-shot pass to the welcome screen. The startup gate and the pair-list gate race on a fresh
/// launch — the pass goes to exactly one of them, so the screen is pushed once per account-less
/// episode. Finding an account re-arms the pass: the next account-less episode — the sign-out —
/// opens the welcome again instead of waiting for a restart.
/// </summary>
public sealed class WelcomeGate
{
    private int _open = 1; // the launch race crosses threads — the counter is Interlocked

    /// <summary>
    /// True for the one caller that takes the screen; the pass closes until <see cref="Rearm"/>.
    /// </summary>
    public bool TryOpen() => Interlocked.Exchange(ref _open, 0) == 1;

    /// <summary>
    /// An account exists again: the next account-less episode gets its pass back.
    /// </summary>
    public void Rearm() => Interlocked.Exchange(ref _open, 1);
}
