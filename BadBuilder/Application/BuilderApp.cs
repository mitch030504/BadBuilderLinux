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

    private static async Task InstallAsync(CancellationToken cancellationToken)
    {
        Controls.RenderHeader();
        Controls.ShowConfigurationSummary(Config);

        string? runRoot = null;
        PreparedTarget? preparedTarget = null;
        IDisposable? cancellationBlock = null;
        bool copiedSuccessfully = false;
        bool finalizedSuccessfully = true;
        bool failureNeedsPause = false;

        try
        {
            ValidateConfiguration();
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
            selected = await DiskService.RevalidateAsync(selected.Identity, cancellationToken);
            Config.TargetDisk = selected;

            Controls.PadLine();
            Controls.WriteWarning("The next step permanently erases the selected disk.");
            Controls.WriteInfo($"Device: {selected.DevicePath}");
            Controls.WriteInfo($"Model: {selected.Name}");
            Controls.WriteInfo($"Size: {selected.Size / 1024d / 1024d / 1024d:0.00} GiB");
            Controls.WriteInfo($"Serial/WWN: {selected.Serial ?? selected.Wwn ?? "not reported"}");
            string confirmation = Controls.PromptText($"Type the exact device path '{selected.DevicePath}' to continue");
            StringComparison comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!string.Equals(confirmation, selected.DevicePath, comparison))
            {
                Controls.WriteWarning("Device-path confirmation did not match. Nothing was formatted.");
                Controls.Pause();
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            Controls.WriteWarning("Disk preparation has started. Ctrl+C cancellation is disabled until cleanup finishes.");
            cancellationBlock = CancellationGate.Block();
            preparedTarget = await DiskService.PrepareAsync(selected.Identity, CancellationToken.None);

            Controls.WriteInfo("Copying the fully validated installation plan.");
            await InstallService.ExecuteAsync(installPlan, preparedTarget.MountRoot, CancellationToken.None);

            if (Config.LaunchHomebrew is not null)
            {
                string iniPath = Path.Combine(preparedTarget.MountRoot, "launch.ini");
                string appName = FileServices.SanitizeFatName(Config.LaunchHomebrew.Artifact.DisplayName);
                string xboxPath = XboxPath.Combine(
                    "Usb",
                    "Apps",
                    appName,
                    Config.LaunchHomebrew.EntryPointRelativePath!);

                if (!await LaunchIniService.UpdateDefaultAsync(iniPath, xboxPath, CancellationToken.None))
                {
                    Controls.WriteWarning("launch.ini was not present. Dashlaunch automatic startup was not configured.");
                }
            }

            copiedSuccessfully = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && preparedTarget is null)
        {
            Controls.WriteWarning("Installation was cancelled before disk preparation. The selected disk was not formatted.");
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
            try
            {
                if (preparedTarget?.RequiresFinalize == true)
                {
                    try
                    {
                        FinalizeResult result = await DiskService.FinalizeAsync(preparedTarget, CancellationToken.None);
                        finalizedSuccessfully = result.Success;
                        if (!result.Success)
                        {
                            string message = result.Error ?? "USB cleanup failed.";
                            Controls.WriteWarning(result.StillMounted
                                ? $"{message} The USB remains mounted at {preparedTarget.MountRoot}."
                                : message);
                            DiagnosticLog.Info($"USB finalization failed: {message}");
                        }
                    }
                    catch (Exception ex)
                    {
                        finalizedSuccessfully = false;
                        DiagnosticLog.Error(ex, "Unexpected USB finalization failure.");
                        Controls.WriteWarning(
                            $"USB cleanup failed unexpectedly. Treat the USB as still mounted at {preparedTarget.MountRoot} and close programs using it before retrying.");
                    }
                }
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

                cancellationBlock?.Dispose();
            }
        }

        if (copiedSuccessfully)
        {
            Controls.WriteSuccess(finalizedSuccessfully
                ? OperatingSystem.IsLinux()
                    ? "Your USB drive is ready for use and was safely unmounted."
                    : "Your USB drive is ready for use. Eject it normally from Windows."
                : "All files were copied, but review the cleanup warning before removing the USB.");
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

        if (Config.FirmwareUpdateEnabled)
            return;

        if (Config.LaunchHomebrew is null)
            return;
        if (Config.SelectedBootstrap != BootstrapOption.XeUnshackle)
            throw new InvalidOperationException("Automatic homebrew launch requires the XeUnshackle bootstrap.");
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
    Exit,
}
