using System.IO;
using System.Xml.Linq;

namespace ChronoIsle.UiTests;

public sealed class PackageIdentityContractTests
{
    [Fact]
    public void PackageIdentity_UsesMatchingExternalLocationManifests()
    {
        var workspace = FindWorkspace();
        var identityDirectory = Path.Combine(workspace, "installer", "package-identity");
        var packageManifestPath = Path.Combine(identityDirectory, "Package.appxmanifest");
        var applicationManifestPath = Path.Combine(identityDirectory, "ChronoIsle.exe.manifest");
        var buildToolsProjectPath = Path.Combine(identityDirectory, "ChronoIsle.PackageIdentity.Tools.csproj");
        var registrationScriptPath = Path.Combine(identityDirectory, "build-and-register-development-identity.ps1");

        Assert.True(File.Exists(packageManifestPath), "The external-location package manifest is missing.");
        Assert.True(File.Exists(applicationManifestPath), "The executable package-identity manifest is missing.");
        Assert.True(File.Exists(buildToolsProjectPath), "The SDK BuildTools project is missing.");
        Assert.True(File.Exists(registrationScriptPath), "The development identity registration script is missing.");

        var project = File.ReadAllText(Path.Combine(workspace, "src", "ChronoIsle.App", "ChronoIsle.App.csproj"));
        var packageManifest = File.ReadAllText(packageManifestPath);
        var applicationManifest = File.ReadAllText(applicationManifestPath);
        var buildToolsProject = File.ReadAllText(buildToolsProjectPath);
        var registrationScript = File.ReadAllText(registrationScriptPath);

        Assert.Contains("ChronoIsle.exe.manifest", project, StringComparison.Ordinal);
        Assert.Contains("Name=\"Tr11111.ChronoIsle\"", packageManifest, StringComparison.Ordinal);
        Assert.Contains("Publisher=\"CN=ChronoIsle Development\"", packageManifest, StringComparison.Ordinal);
        Assert.Contains("Id=\"ChronoIsle\" Executable=\"ChronoIsle.exe\"", packageManifest, StringComparison.Ordinal);
        Assert.Contains("<uap10:AllowExternalContent>true</uap10:AllowExternalContent>", packageManifest, StringComparison.Ordinal);
        Assert.Contains("<rescap:Capability Name=\"runFullTrust\" />", packageManifest, StringComparison.Ordinal);
        Assert.Contains("<rescap:Capability Name=\"unvirtualizedResources\" />", packageManifest, StringComparison.Ordinal);
        Assert.Contains("<uap3:Capability Name=\"userNotificationListener\" />", packageManifest, StringComparison.Ordinal);
        Assert.DoesNotContain("graphicsCaptureWithoutBorder", packageManifest, StringComparison.Ordinal);
        Assert.Contains("publisher=\"CN=ChronoIsle Development\"", applicationManifest, StringComparison.Ordinal);
        Assert.Contains("packageName=\"Tr11111.ChronoIsle\"", applicationManifest, StringComparison.Ordinal);
        Assert.Contains("applicationId=\"ChronoIsle\"", applicationManifest, StringComparison.Ordinal);
        Assert.Contains("requestedExecutionLevel level=\"requireAdministrator\"", applicationManifest, StringComparison.Ordinal);
        Assert.Contains(
            "<PackageReference Include=\"Microsoft.Windows.SDK.BuildTools\" Version=\"10.0.26100.7463\" PrivateAssets=\"all\" />",
            buildToolsProject,
            StringComparison.Ordinal);
        Assert.Contains("New-SelfSignedCertificate", registrationScript, StringComparison.Ordinal);
        Assert.Contains("Cert:\\LocalMachine\\TrustedPeople", registrationScript, StringComparison.Ordinal);
        Assert.Contains("Add-AppxPackage -Path $packagePath -ExternalLocation $PublishDirectory", registrationScript, StringComparison.Ordinal);
        Assert.Contains("Get-AppxPackage -Name $packageName", registrationScript, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageIdentity_OnlySharesNotificationRegistrySettings()
    {
        var manifest = XDocument.Load(Path.Combine(FindWorkspace(), "installer", "package-identity", "Package.appxmanifest"));
        XNamespace virtualization = "http://schemas.microsoft.com/appx/manifest/virtualization/windows10";
        var configuration = Assert.Single(manifest.Descendants(virtualization + "RegistryWriteVirtualization"));
        var excluded = Assert.Single(configuration.Descendants(virtualization + "ExcludedKey"));
        Assert.Equal(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings", excluded.Value);
        Assert.DoesNotContain(manifest.Descendants(), node =>
            node.Name.LocalName == "RegistryWriteVirtualization" && node.Value.Trim() == "disabled");
    }

    [Fact]
    public void PackageIdentity_ManifestLogosAreDeployedBesideExecutable()
    {
        var manifest = XDocument.Load(Path.Combine(FindWorkspace(), "installer", "package-identity", "Package.appxmanifest"));
        XNamespace foundation = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        XNamespace uap = "http://schemas.microsoft.com/appx/manifest/uap/windows10";
        var visual = Assert.Single(manifest.Descendants(uap + "VisualElements"));
        var logos = new[]
        {
            (Path: Assert.Single(manifest.Descendants(foundation + "Logo")).Value, Size: 50),
            (Path: visual.Attribute("Square44x44Logo")!.Value, Size: 44),
            (Path: visual.Attribute("Square150x150Logo")!.Value, Size: 150)
        };
        foreach (var logo in logos)
        {
            // Sparse package resources resolve at the external executable location.
            var deployedPath = Path.Combine(AppContext.BaseDirectory, logo.Path);
            Assert.True(File.Exists(deployedPath), $"Missing external-location logo: {deployedPath}");
            using var stream = File.OpenRead(deployedPath);
            var decoder = new System.Windows.Media.Imaging.PngBitmapDecoder(stream,
                System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            Assert.Equal(logo.Size, decoder.Frames[0].PixelWidth);
            Assert.Equal(logo.Size, decoder.Frames[0].PixelHeight);
        }
    }

    static string FindWorkspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "ChronoIsle.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("ChronoIsle.sln was not found from the UI test host.");
    }
}
