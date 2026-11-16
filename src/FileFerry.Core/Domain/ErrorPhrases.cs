namespace FileFerry.Core.Domain;

/// <summary>
/// An error maps to a phrase key for App rendering (AppStrings). Same contract as
/// <c>EventPhrases</c>: Core knows only the intent (<see cref="ErrorKind"/>), the localized
/// text lives in resources; the raw <see cref="SyncError.Message"/> is an English diagnostic
/// appended after the label — never wording to be reworded per screen.
/// Keys <c>Error{Kind}</c> must exist in every locale of AppStrings — covered by a test.
/// </summary>
public static class ErrorPhrases
{
    /// <summary>
    /// Resource key of the localized label for this error kind.
    /// </summary>
    public static string PhraseKey(this SyncError error) => "Error" + error.Kind;
}
