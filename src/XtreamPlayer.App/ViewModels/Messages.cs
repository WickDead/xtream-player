namespace XtreamPlayer.App.ViewModels;

/// <summary>
/// Raised when the set of providers, or which one is active, has changed.
/// </summary>
/// <remarks>
/// The Providers page can add, import or delete a profile, but only the shell
/// owns the active profile, the favorites store and catalog syncing. Without
/// this the first import appeared to do nothing: rows landed in the database and
/// the shell carried on believing there were no providers at all.
///
/// Sent through WeakReferenceMessenger so a cached page on the navigation stack
/// cannot keep the shell alive through a subscription.
/// </remarks>
public sealed record ProfilesChangedMessage(string? ActivateProfileId = null);

/// <summary>
/// Asks the shell to hide or restore its chrome for fullscreen playback.
/// </summary>
/// <remarks>
/// The window going to <c>WindowState.FullScreen</c> is only half of it: the
/// sidebar, breadcrumb bar and status bar still hold their space, so the video
/// ends up letterboxed inside a chromeless window rather than filling the
/// screen. Only the shell can give that space up, and the player has no
/// reference to it.
/// </remarks>
public sealed record FullscreenChangedMessage(bool IsFullscreen);
