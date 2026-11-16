using FileFerry.Core.Domain;

namespace FileFerry.Core.Tests.Domain;

/// <summary>
/// The name of the local folder as the interface shows it, derived from the pair's tree key.
/// The stored display name is the pair's title taken from the remote side, so a screen that names
/// the local folder cannot use it — and it must not read the raw key either.
/// </summary>
[Trait("Category", "Unit")]
public class LocalFolderNameTests
{
    [Theory]
    // SAF: the document path follows the provider id, both separated by an encoded colon.
    [InlineData("content://com.android.externalstorage.documents/tree/primary%3ADocuments", "Documents")]
    [InlineData("content://com.android.externalstorage.documents/tree/primary%3ADocuments%2FSync%2FProjects", "Projects")]
    [InlineData("content://com.android.externalstorage.documents/tree/primary%3ARemoteFolder/document/primary%3ALocal", "Local")]
    [InlineData("content://com.android.providers.downloads.documents/tree/msf%3Adownloads", "downloads")]
    // A grant that names no document path degrades to the provider id it does name.
    [InlineData("content://com.android.externalstorage.documents/tree/primary", "primary")]
    [InlineData("content://x/tree/", "tree")]
    // Plain file system: the key is a directory path.
    [InlineData("/home/user/Sync/Projects", "Projects")]
    [InlineData("C:\\Users\\user\\Sync", "Sync")]
    // A decoded key arrives the same way as an encoded one.
    [InlineData("content://…/tree/primary:Documents", "Documents")]
    public void Name_is_the_last_segment_of_the_granted_folder(string treeKey, string expected) =>
        Assert.Equal(expected, FolderPair.LocalFolderName(treeKey));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("::")]
    public void Name_of_an_unreadable_key_is_empty_for_the_caller_to_replace(string treeKey) =>
        Assert.Equal(string.Empty, FolderPair.LocalFolderName(treeKey));
}
