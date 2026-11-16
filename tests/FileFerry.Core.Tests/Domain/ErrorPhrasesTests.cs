using FileFerry.Core.Domain;
using FileFerry.Core.Tests.Localization;

namespace FileFerry.Core.Tests.Domain;

/// <summary>
/// Every <see cref="ErrorKind"/> has a renderable phrase key, and every key exists in every
/// locale of the string resources (the same coverage discipline as the event phrases).
/// </summary>
[Trait("Category", "Unit")]
public class ErrorPhrasesTests
{
    [Fact]
    public void PhraseKey_is_the_kind_with_the_Error_prefix()
    {
        var error = SyncError.Of(ErrorKind.NotFound, "The file 'a.txt' does not exist on the server (404).");

        Assert.Equal("ErrorNotFound", error.PhraseKey());
    }

    [Fact]
    public void Every_error_kind_key_exists_in_every_locale()
    {
        var locales = AppStringsFiles.AllLocales();
        Assert.NotEmpty(locales);

        foreach (var kind in Enum.GetValues<ErrorKind>())
        {
            var key = "Error" + kind;
            foreach (var entries in locales.Values)
            {
                Assert.Contains(key, entries.Keys);
            }
        }
    }
}
