using System.Text.Json;
using BadBuilder.Configuration;
using BadBuilder.Services;
using BadBuilder.Services.Disks;
using BadBuilder.UI;

namespace BadBuilder.Application;

internal static partial class BuilderApp
{
    private static readonly BuilderConfig Config = new(ArtifactCatalog.Homebrew);

    internal static async Task RunAsync(CancellationToken cancellationToken)
    {
        bool running = true;
        while (running && !cancellationToken.IsCancellationRequested)
        {
            Controls.RenderHeader();
            Controls.ShowConfigurationSummary(Config);

            List<MenuOption<RootAction>> options =
            [
                new(RootAction.ConfigureDrive, "Target drive"),
                new(RootAction.ConfigureExploit, "Exploit"),
                new(RootAction.ConfigureBootstrap, "Post-exploit bootstrap"),
                new(RootAction.ConfigureHomebrew, "Homebrew"),
                new(RootAction.UpdateXbox, "Update Xbox Dashboard"),
                new(RootAction.Install, "Install"),
                new(RootAction.Exit, "Exit"),
            ];

            if (Config.FirmwareUpdateEnabled)
            {
                options.RemoveAll(item => item.Value is not
                    (RootAction.ConfigureDrive or RootAction.UpdateXbox or RootAction.Install or RootAction.Exit));
            }
            else if (Config.IsPeerPressureInstall)
            {
                options.RemoveAll(item => item.Value == RootAction.ConfigureBootstrap);
            }

            PreparedTarget? pendingCleanup = PendingCleanupStore.TryLoad();
            if (pendingCleanup is not null)
                options.Insert(options.Count - 1, new(RootAction.RetryCleanup, "Retry USB cleanup"));

            try
            {
                switch (Controls.PromptSelection("Select an action", options))
                {
                    case RootAction.ConfigureDrive:
                        await ConfigureDriveAsync(cancellationToken);
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
                    case RootAction.UpdateXbox:
                        PromptUpdate();
                        break;
                    case RootAction.Install:
                        await InstallAsync(cancellationToken);
                        break;
                    case RootAction.RetryCleanup:
                        if (pendingCleanup is not null)
                            await RetryCleanupAsync(pendingCleanup, cancellationToken);
                        break;
                    case RootAction.Exit:
                        running = false;
                        break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Error(ex, "Handled menu action failure.");
                Controls.WriteError($"The action could not be completed: {ex.Message}");
                Controls.Pause();
            }
        }
    }

    private static async Task RetryCleanupAsync(PreparedTarget pending, CancellationToken cancellationToken)
    {
        Controls.RenderHeader();
        Controls.WriteInfo($"Device: {pending.Identity.DevicePath} ({pending.Identity.Model})");
        Controls.WriteInfo($"Mount: {pending.MountRoot}");

        try
        {
            FinalizeResult result = await DiskService.FinalizeAsync(pending, CancellationToken.None);
            if (result.Success)
            {
                try
                {
                    PendingCleanupStore.Clear();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    DiagnosticLog.Error(ex, "Could not clear the pending USB cleanup record.");
                    Controls.WriteWarning(
                        "The USB was cleaned up, but the pending cleanup record could not be removed. Retry cleanup to clear it.");
                }

                Controls.WriteSuccess(OperatingSystem.IsLinux()
                    ? "USB cleanup completed and the drive was safely unmounted."
                    : "USB cleanup completed.");
            }
            else
            {
                string message = result.Error ?? "USB cleanup failed.";
                Controls.WriteWarning(result.StillMounted
                    ? $"{message} The USB remains mounted at {pending.MountRoot}."
                    : message);
                DiagnosticLog.Info($"USB finalization retry failed: {message}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            DiagnosticLog.Error(ex, "USB cleanup retry failed.");
            Controls.WriteError($"USB cleanup could not be completed: {ex.Message}");
        }

        Controls.Pause();
    }

    private static async Task InstallAsync(CancellationToken cancellationToken)
    {
        Controls.RenderHeader();
        Controls.ShowConfigurationSummary(Config);

        if (PendingCleanupStore.TryLoad() is not null)
        {
            throw new InvalidOperationException(
                "A previous USB cleanup is still pending. Run Retry USB cleanup from the main menu before starting a new installation.");
        }

        string? runRoot = null;
        bool copiedSuccessfully = false;
        bool finalizedSuccessfully = true;
        bool failureNeedsPause = false;

        try
        {
            ValidateConfiguration();
            if (Config.IsPeerPressureInstall)
            {
                Controls.WriteWarning("Peer Pressure requires a supported Xbox 360 and an HDD (mechanical recommended). Corona and Winchester are unsupported.");
                Controls.WriteWarning("The console installer modifies NAND and HDD security sectors. Create and keep a system backup before installing; failure may require a hardware NAND flasher.");
                Controls.WriteInfo("This prepares the official installer USB. Follow https://github.com/grimdoomer/Xbox360PeerPressure/wiki/Installation");
            }
            DiskInfo selected = Config.TargetDisk!;
            IReadOnlyList<ArtifactDefinition> artifacts = ArtifactCatalog.GetSelectedArtifacts(Config);
            if (artifacts.Count == 0)
                throw new InvalidOperationException("No artifacts are configured for installation.");

            runRoot = AppPaths.CreateRunRoot();
            string stagingRoot = Path.Combine(runRoot, "staging");
            Directory.CreateDirectory(stagingRoot);
            Directory.CreateDirectory(AppPaths.DownloadRoot);

            DownloadResult download = await DownloadService.DownloadAsync(
                artifacts,
                AppPaths.DownloadRoot,
                cancellationToken);
            Controls.WriteSuccess(download.DownloadedCount > 0
                ? $"{download.DownloadedCount} download(s) completed and verified."
                : "All required artifacts passed cache and checksum validation.");

            Controls.WriteInfo("Inspecting and extracting required archives.");
            List<(ArtifactDefinition Artifact, string StagingPath)> staged = [];
            foreach ((ArtifactDefinition artifact, string archivePath) in download.Artifacts)
            {
                ArchiveExtractionLimits limits = ArchiveExtractionLimits.CreateDefault(stagingRoot, selected.Size);
                string path = await ArchiveService.ExtractAsync(
                    artifact.ID,
                    archivePath,
                    stagingRoot,
                    limits,
                    cancellationToken);
                ArchiveService.ValidateLayout(artifact, path);
                staged.Add((artifact, path));
            }

            if (Config.IsPeerPressureInstall)
                staged = PeerPressureHomebrewService.PrepareArtifacts(Config, staged);

            if (!Config.FirmwareUpdateEnabled && !Config.IsPeerPressureInstall && Config.LaunchHomebrew is not null)
            {
                string relativeLaunchPath = LaunchIniService.ValidateStagedEntryPoint(staged, Config.LaunchHomebrew);
                string xboxPath = XboxPath.Combine("Usb", [.. relativeLaunchPath.Split('/')]);
                string bootstrapId = ArtifactCatalog.Bootstraps[Config.SelectedBootstrap].ID;

                bool configured = await LaunchIniService.ConfigureStagedLaunchAsync(
                    staged,
                    bootstrapId,
                    Config.LaunchHomebrew,
                    xboxPath,
                    cancellationToken);
                if (!configured)
                {
                    Controls.WriteWarning(
                        "launch.ini was not present. Automatic startup will not be configured.");
                }
            }

            string configText = $"Configuration:{Environment.NewLine}{Config}";
            List<InstallOperation> extraOperations =
            [
                new(InstallOperationKind.WriteFile, "name.txt", Contents: "USB Storage Device"),
                new(
                    InstallOperationKind.WriteFile,
                    "info.txt",
                    Contents: "This drive was created with BadBuilder by Pdawg.\n" +
                              "https://github.com/Pdawg-bytes/BadBuilder\n\n" + configText),
            ];

            InstallPlan installPlan = InstallService.BuildPlan(staged, extraOperations, selected.Size);
            InstallCoordinator coordinator = new(
                DiskService.Backend,
                ConsoleUserInterface.Instance,
                FilePendingCleanupStore.Instance);
            InstallExecutionResult execution = await coordinator.ExecuteAsync(
                selected,
                installPlan,
                cancellationToken);
            Config.TargetDisk = execution.RevalidatedDisk;

            if (execution.CancelledBeforePrepare)
            {
                Controls.WriteWarning(
                    "Installation was cancelled before disk preparation. The selected disk was not formatted.");
                Controls.Pause();
                return;
            }

            if (execution.Error is not null)
            {
                DiagnosticLog.Error(execution.Error, "Handled installation failure.");
                Controls.WriteError(execution.FormatCompleted
                    ? $"The USB was formatted, but copying failed: {execution.Error.Message}"
                    : $"Installation failed: {execution.Error.Message}");
                failureNeedsPause = true;
            }

            copiedSuccessfully = execution.CopiedSuccessfully;
            finalizedSuccessfully = execution.FinalizedSuccessfully;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Controls.WriteWarning(
                "Installation was cancelled before disk preparation. The selected disk was not formatted.");
            Controls.Pause();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error(ex, "Handled installation failure.");
            Controls.WriteError($"Installation failed: {ex.Message}");
            failureNeedsPause = true;
        }
        finally
        {
            if (runRoot is not null)
            {
                try
                {
                    AppPaths.DeleteRunRoot(runRoot);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    DiagnosticLog.Error(ex, "Could not remove transient staging data.");
                }
            }
        }

        if (copiedSuccessfully)
        {
            Controls.WriteSuccess(finalizedSuccessfully
                ? OperatingSystem.IsLinux()
                    ? "Your USB drive is ready for use and was safely unmounted."
                    : "Your USB drive is ready for use. Eject it normally from Windows."
                : "All files were copied, but review the cleanup warning before removing the USB.");
            if (Config.IsPeerPressureInstall)
                Controls.WriteInfo("On the Xbox 360, launch the Rock Band Blitz demo and press A at the title screen. Create and keep a system backup. Select Install Extras and choose the packaged homebrew, including your selected default dashboard.");
            Controls.Pause();
        }
        else if (failureNeedsPause)
        {
            Controls.Pause();
        }
    }

    private static void ValidateConfiguration()
    {
        if (Config.TargetDisk is null)
            throw new InvalidOperationException("Select a target drive.");

        if (Config.FirmwareUpdateEnabled || Config.IsPeerPressureInstall)
            return;

        if (Config.LaunchHomebrew is null)
            return;
        if (!BootstrapCapabilities.SupportsDefaultHomebrewLaunch(Config.SelectedBootstrap))
            throw new InvalidOperationException("Automatic homebrew launch requires XeUnshackle or XeUnshackle Max.");
        if (string.IsNullOrWhiteSpace(Config.LaunchHomebrew.EntryPointRelativePath))
            throw new InvalidOperationException("The selected default homebrew has no valid entry point.");
    }
}

internal enum RootAction
{
    ConfigureDrive,
    ConfigureExploit,
    ConfigureBootstrap,
    ConfigureHomebrew,
    UpdateXbox,
    Install,
    RetryCleanup,
    Exit,
}
