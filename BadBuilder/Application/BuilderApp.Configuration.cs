using BadBuilder.UI;
using BadBuilder.Configuration;

namespace BadBuilder.Application;

internal static partial class BuilderApp
{
    private static void ConfigureDrive()
    {
        Controls.RenderHeader();

        var x = DriveInfo.GetDrives();

        DriveInfo[] drives = [..DriveInfo.GetDrives()
            .Where(drive => drive.IsReady)
            .OrderBy(drive => drive.Name, StringComparer.OrdinalIgnoreCase)];

        if (drives.Length == 0)
        {
            Controls.WriteError("No mounted, ready drives were found.");
            Controls.Pause();
            return;
        }

        static string FormatDriveLabel(DriveInfo drive)
        {
            double sizeGigabytes = drive.TotalSize / 1024d / 1024d / 1024d;
            return $"{drive.RootDirectory.FullName} ({sizeGigabytes:0.00} GB) - {drive.DriveType}";
        }

        Config.MountPoint = Controls.PromptSelection(
            "Choose target drive",
            [..drives.Select(drive => new MenuOption<string>(drive.RootDirectory.FullName, FormatDriveLabel(drive)))],
            "[bold white]All data will be lost on this drive.[/] Make sure to select the correct drive."
        );
    }

    private static void ConfigureExploit()
    {
        Controls.RenderHeader();

        Config.SelectedExploit = Controls.PromptSelection(
            "Choose exploit",
            [..Catalog.Exploits.Select(pair => new MenuOption<ExploitOption>(pair.Key, pair.Value.DisplayName, pair.Value.Description))],
            "Executes the console exploit and unlocks the hypervisor, allowing further unsigned code execution."
        );
    }

    private static void ConfigureBootstrap()
    {
        Controls.RenderHeader();

        var selectedBootstrap = Controls.PromptSelection(
            "Choose post-exploit bootstrap",
            [..Catalog.Bootstraps.Select(pair => new MenuOption<BootstrapOption>(pair.Key, pair.Value.DisplayName, pair.Value.Description))],
            "The payload executed immediately after a successful hypervisor exploit to patch the kernel and initialize homebrew capabilities."
        );

        Config.SelectedBootstrap = selectedBootstrap;
        if (selectedBootstrap != BootstrapOption.XeUnshackle && Config.LaunchHomebrewId is not null)
        {
            Config.LaunchHomebrewId = null;
            Controls.WriteWarning("The default homebrew launch selection was cleared because automatic launching requires XeUnshackle.");
            Controls.Pause();
        }
    }
}