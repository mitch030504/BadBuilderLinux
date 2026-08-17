using BadBuilder.UI;
using BadBuilder.Services;
using BadBuilder.Configuration;

namespace BadBuilder.Application;

internal static partial class BuilderApp
{
    private static readonly ArtifactCatalog Catalog = ArtifactCatalog.CreateDefault();
    private static readonly BuilderConfig Config    = new(Catalog.Homebrew);

    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        bool running = true;
        while (running && !cancellationToken.IsCancellationRequested)
        {
            Controls.RenderHeader();
            Controls.ShowConfigurationSummary(Config);

            RootAction action = Controls.PromptSelection(
                "Select an action",
                [
                    new MenuOption<RootAction>(RootAction.ConfigureDrive, "Target drive"),
                    new MenuOption<RootAction>(RootAction.ConfigureExploit, "Exploit"),
                    new MenuOption<RootAction>(RootAction.ConfigureBootstrap, "Post-exploit bootstrap"),
                    new MenuOption<RootAction>(RootAction.ConfigureHomebrew, "Homebrew"),
                    new MenuOption<RootAction>(RootAction.Install, "Install"),
                    new MenuOption<RootAction>(RootAction.Exit, "Exit")
                ]);

            switch (action)
            {
                case RootAction.ConfigureDrive:
                    ConfigureDrive();
                    break;
                case RootAction.ConfigureExploit:
                    ConfigureExploit();
                    break;
                case RootAction.ConfigureBootstrap:
                    ConfigureBootstrap();
                    break;
                case RootAction.ConfigureHomebrew:
                    ConfigureHomebrew();
                    break;
                case RootAction.Install:
                    await InstallAsync(cancellationToken);
                    break;
                case RootAction.Exit:
                    running = false;
                    break;
            }
        }
    }

    private static async Task InstallAsync(CancellationToken cancellationToken)
    {
        Controls.RenderHeader();
        Controls.ShowConfigurationSummary(Config);

        try
        {
            if (string.IsNullOrWhiteSpace(Config.MountPoint))
                throw new InvalidOperationException("Select a target drive.");

            if (Config.LaunchHomebrewId is not null)
            {
                if (Config.SelectedBootstrap != BootstrapOption.XeUnshackle)
                    throw new InvalidOperationException("Automatic homebrew launch requires the XeUnshackle bootstrap.");

                var launchHomebrew = Config.Homebrew.FirstOrDefault(homebrew => homebrew.ID == Config.LaunchHomebrewId);
                if (launchHomebrew?.EntryPointRelativePath is null)
                    throw new InvalidOperationException("The selected default homebrew has no valid entry point.");
            }

            bool format = Controls.Confirm($"Are you sure you would like to format [bold]{Config.MountPoint}[/]? All data on this drive will be lost.", false, warning: true);


            var artifacts       = Catalog.GetSelectedArtifacts(Config);
            string workRoot     = Path.Combine(AppContext.BaseDirectory, "Work");
            string downloadRoot = Path.Combine(workRoot, "Downloads");
            string stagingRoot  = Path.Combine(workRoot, "Staging");

            Directory.CreateDirectory(downloadRoot);
            DownloadResult download = await DownloadService.DownloadAsync(artifacts, downloadRoot, cancellationToken);

            if (download.DownloadedCount > 0)
                Controls.WriteSuccess($"[bold]{download.DownloadedCount}[/] download(s) completed.");
            else
                Controls.WriteSuccess("All downloads are already up to date.");

            Controls.WriteInfo("Extracting files.");
            Directory.CreateDirectory(stagingRoot);

            var staged = await Task.WhenAll(download.Artifacts.Select(async item =>
                (item.Artifact, StagingPath: await ArchiveService.ExtractAsync(item.ArchivePath, stagingRoot, cancellationToken))));

            Controls.WriteSuccess("Files extracted.");
            Controls.PadLine();
            Controls.WriteInfo("Copying files.");

            string configText = $"Configuration:\n{Config}";

            var extraOperations = new List<InstallOperation>
            {
                new(InstallOperationKind.WriteFile, "name.txt", Contents: "USB Storage Device"),
                new(InstallOperationKind.WriteFile, "info.txt", Contents: "This drive was created with BadBuilder by Pdawg.\nFind more info here: https://github.com/Pdawg-bytes/BadBuilder" + $"\n\n{configText}")
            };

            await InstallService.ExecuteAsync(staged, extraOperations, Config.MountPoint!, cancellationToken);

            Controls.WriteSuccess("Your USB drive is ready for use.");

            Controls.Pause("Press enter to exit...");
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            Controls.WriteError($"Install failed: {ex.Message}");
            Controls.Pause();
        }
    }
}

internal enum RootAction
{
    ConfigureDrive,
    ConfigureExploit,
    ConfigureBootstrap,
    ConfigureHomebrew,
    Install,
    Exit,
}