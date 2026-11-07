using BadBuilder.Configuration;
using BadBuilder.Services;

namespace BadBuilder.Tests;

public sealed class PeerPressureFatxTests
{
    private const string Device = "Hdd1";

    [Fact]
    public void Extras_RejectADestinationDirectoryNameLongerThan42Characters()
    {
        using TemporaryDirectory temporary = new();
        BuilderConfig config = CreateConfig("Apps/" + new string('a', 43));

        Assert.Throws<InvalidDataException>(() => PeerPressureHomebrewService.PrepareArtifacts(
            config, CreateStagedArtifacts(temporary.Path, config)));
    }

    [Fact]
    public void Extras_AcceptADestinationDirectoryNameOf42Characters()
    {
        using TemporaryDirectory temporary = new();
        BuilderConfig config = CreateConfig("Apps/" + new string('a', 42));

        var prepared = PeerPressureHomebrewService.PrepareArtifacts(
            config, CreateStagedArtifacts(temporary.Path, config));

        Assert.Contains(prepared, item => item.Artifact.ID == "homebrew-aurora");
    }

    [Theory]
    [InlineData("directory")]
    [InlineData("file")]
    public void Extras_RejectACopiedItemWhoseNameIsLongerThan42Characters(string kind)
    {
        using TemporaryDirectory temporary = new();
        BuilderConfig config = CreateConfig("Apps/Aurora");
        string longName = new('a', 43);
        string stagedPath = kind == "directory" ? longName + "/Default.xex" : longName;

        Assert.Throws<InvalidDataException>(() => PeerPressureHomebrewService.PrepareArtifacts(
            config, CreateStagedArtifacts(temporary.Path, config, stagedPath)));
    }

    [Fact]
    public void Extras_RejectANestedCopiedPathLongerThan240Characters()
    {
        using TemporaryDirectory temporary = new();
        BuilderConfig config = CreateConfig("Apps/Aurora");
        string nested = string.Join('/', Enumerable.Repeat(new string('d', 40), 6)) + "/Default.xex";

        Assert.Throws<InvalidDataException>(() => PeerPressureHomebrewService.PrepareArtifacts(
            config, CreateStagedArtifacts(temporary.Path, config, nested)));
    }

    [Fact]
    public void FatxPath_AcceptsA42CharacterComponent()
    {
        string component = new('a', 42);

        Assert.Equal(@"Hdd1:\Apps\" + component, XboxPath.ValidateFatxPath(Device, "Apps/" + component));
    }

    [Fact]
    public void FatxPath_RejectsA43CharacterComponent()
    {
        Assert.Throws<InvalidDataException>(() => XboxPath.ValidateFatxPath(Device, "Apps/" + new string('a', 43)));
    }

    [Fact]
    public void FatxPath_AcceptsACompletePathOf240Characters()
    {
        string relative = RelativePathForCompleteLength(240);

        Assert.Equal(240, XboxPath.ValidateFatxPath(Device, relative).Length);
    }

    [Fact]
    public void FatxPath_RejectsACompletePathOf241Characters()
    {
        string relative = RelativePathForCompleteLength(241);

        Assert.Throws<InvalidDataException>(() => XboxPath.ValidateFatxPath(Device, relative));
    }

    private static string RelativePathForCompleteLength(int completeLength)
    {
        // The complete path is "Hdd1:\" (six characters) plus the relative path with backslashes.
        int remaining = completeLength - (Device.Length + 2);
        List<string> components = [];
        while (remaining > 0)
        {
            int length = Math.Min(42, remaining);
            components.Add(new string((char)('a' + components.Count), length));
            remaining -= length;
            if (remaining > 0)
                remaining--;
        }

        string relative = string.Join('/', components);
        Assert.Equal(completeLength - (Device.Length + 2), relative.Length);
        return relative;
    }

    private static BuilderConfig CreateConfig(string destinationPath)
    {
        HomebrewEntry aurora = ArtifactCatalog.Homebrew[0];
        HomebrewEntry app = aurora with
        {
            Artifact = aurora.Artifact with
            {
                Operations = [new InstallOperation(InstallOperationKind.CopyDirectory, destinationPath, ".")]
            }
        };
        return new BuilderConfig([app]) { SelectedExploit = ExploitOption.PeerPressure };
    }

    private static List<(ArtifactDefinition Artifact, string StagingPath)> CreateStagedArtifacts(
        string root,
        BuilderConfig config,
        params string[] appFiles)
    {
        ArtifactDefinition installer = ArtifactCatalog.Exploits[ExploitOption.PeerPressure];
        string installerRoot = Path.Combine(root, installer.ID);
        foreach (string path in installer.Layout!.RequiredPaths!)
            WriteFile(installerRoot, path, "installer");
        WriteFile(installerRoot, "BadUpdatePayload/SoftmodExtras/SoftmodExtras.ini", "; upstream comments\r\n[Xell]\r\nSource = \"Xell\"\r\n");

        List<(ArtifactDefinition, string)> staged = [(installer, installerRoot)];
        foreach (HomebrewEntry entry in config.Homebrew)
        {
            string appRoot = Path.Combine(root, entry.Artifact.ID);
            Directory.CreateDirectory(appRoot);
            foreach (string path in appFiles)
                WriteFile(appRoot, path, "content");
            staged.Add((entry.Artifact, appRoot));
        }
        return staged;
    }

    private static void WriteFile(string root, string relative, string text)
    {
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}
