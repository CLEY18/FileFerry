using FileFerry.Core.Tests.Localization;

namespace FileFerry.Core.Tests.Device;

/// <summary>
/// Device-data invariants read from the Android manifest at source level: what may leave the
/// device is decided there, and only a test turns that promise into a build failure.
/// </summary>
[Trait("Category", "Unit")]
public sealed class AndroidManifestTests
{
    private static readonly string ManifestPath = Path.Combine(
        AppStringsFiles.Root, "src", "FileFerry.App", "Platforms", "Android", "AndroidManifest.xml");

    [Fact]
    public void Manifest_BackupAndDeviceTransferAreOff()
    {
        // the config DB (server address, login name, SAF trees, the sync journal of every file
        // name) must not ride out of the device in a cloud backup, a phone-to-phone copy or an
        // adb extraction: the token sits in the Keystore and is not exportable anyway, but the
        // journal is personal the same way
        var manifest = File.ReadAllText(ManifestPath);

        Assert.Contains("android:allowBackup=\"false\"", manifest, StringComparison.Ordinal);
    }
}
