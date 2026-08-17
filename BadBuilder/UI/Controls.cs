using Spectre.Console;
using BadBuilder.Configuration;

namespace BadBuilder.UI;

internal static class AppTheme
{
    public static readonly Style OrangeStyle      = new(new Color(255, 114, 0));
    public static readonly Style LightOrangeStyle = new(new Color(255, 172, 77));
    public static readonly Style PeachStyle       = new(new Color(255, 216, 153));
    public static readonly Style GreenStyle       = new(new Color(118, 185, 0));
}

internal static class Controls
{
    public static void RenderHeader()
    {
        AnsiConsole.Clear();
        AnsiConsole.Markup(
        """
        [#4D8C00]██████╗  █████╗ ██████╗ ██████╗ ██╗   ██╗██╗██╗     ██████╗ ███████╗██████╗[/]
        [#65A800]██╔══██╗██╔══██╗██╔══██╗██╔══██╗██║   ██║██║██║     ██╔══██╗██╔════╝██╔══██╗[/]
        [#76B900]██████╔╝███████║██║  ██║██████╔╝██║   ██║██║██║     ██║  ██║█████╗  ██████╔╝[/]
        [#A1CF3E]██╔══██╗██╔══██║██║  ██║██╔══██╗██║   ██║██║██║     ██║  ██║██╔══╝  ██╔══██╗[/]
        [#CCE388]██████╔╝██║  ██║██████╔╝██████╔╝╚██████╔╝██║███████╗██████╔╝███████╗██║  ██║[/]
        [#CCE388]╚═════╝ ╚═╝  ╚═╝╚═════╝ ╚═════╝  ╚═════╝ ╚═╝╚══════╝╚═════╝ ╚══════╝╚═╝  ╚═╝[/]

        [#76B900]───────────────────────────────────────────────────────────────────────v0.31[/]
        ───────────────────────Xbox 360 [#FF7200]BadUpdate[/] USB Builder───────────────────────
                                    [#848589]Created by Pdawg[/]
        [#76B900]────────────────────────────────────────────────────────────────────────────[/]

        """);
        AnsiConsole.WriteLine();
    }

    public static void PadLine() => AnsiConsole.WriteLine();

    public static void WriteInfo(string message)    => AnsiConsole.MarkupLine($"[yellow]{Escape("[*]")}[/] {message}");
    public static void WriteWarning(string message) => AnsiConsole.MarkupLine($"[{ToMarkupColor(AppTheme.LightOrangeStyle.Foreground)}]{Escape("[!]")}[/] {message}");
    public static void WriteError(string message)   => AnsiConsole.MarkupLine($"[red]{Escape("[-]")}[/] {message}");
    public static void WriteSuccess(string message) => AnsiConsole.MarkupLine($"[{ToMarkupColor(AppTheme.GreenStyle.Foreground)}]{Escape("[+]")}[/] {message}");

    public static Dictionary<string, string> PromptArchivePathOverrides(
        string title,
        IReadOnlyList<ArchivePathEntry> entries,
        string? details = null)
    {
        var paths = entries.ToDictionary(
            entry => entry.ArtifactID,
            entry => entry.SuggestedPath ?? string.Empty,
            StringComparer.OrdinalIgnoreCase);

        while (true)
        {
            RenderHeader();

            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn(new TableColumn("Artifact").LeftAligned());
            table.AddColumn(new TableColumn("Status").LeftAligned());
            table.AddColumn(new TableColumn("Archive path").LeftAligned());

            foreach (var entry in entries)
            {
                var currentPath = paths[entry.ArtifactID];
                var required = entry.Required && string.IsNullOrWhiteSpace(currentPath)
                    ? "[red]Required[/]"
                    : entry.Required
                        ? "[yellow]Required (set)[/]"
                        : "[gray]Optional[/]";

                var shownPath = string.IsNullOrWhiteSpace(currentPath)
                    ? "[gray](not set)[/]"
                    : $"[gray]{Escape(currentPath)}[/]";

                table.AddRow(Escape(entry.DisplayName), required, shownPath);
            }

            AnsiConsole.MarkupLine($"[{ToMarkupColor(AppTheme.OrangeStyle.Foreground)}]{Escape(title)}[/]");
            if (!string.IsNullOrWhiteSpace(details))
                AnsiConsole.MarkupLine($"[gray]{Escape(details)}[/]");

            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();

            var options = entries
                .Select(entry => new MenuOption<string>(entry.ArtifactID, $"Edit {entry.DisplayName}", "Set or clear a local archive path"))
                .Append(new MenuOption<string>("__continue", "Continue", "Start install with these values"))
                .ToArray();

            var action = PromptSelection("Select an action", options);
            if (string.Equals(action, "__continue", StringComparison.Ordinal))
            {
                var missing = entries
                    .Where(entry => entry.Required && string.IsNullOrWhiteSpace(paths[entry.ArtifactID]))
                    .Select(entry => entry.DisplayName)
                    .ToArray();

                if (missing.Length > 0)
                {
                    WriteError($"Missing required archive path(s): {string.Join(", ", missing)}");
                    Pause();
                    continue;
                }

                return paths;
            }

            var selectedEntry = entries.First(entry => string.Equals(entry.ArtifactID, action, StringComparison.OrdinalIgnoreCase));
            string? current = paths[selectedEntry.ArtifactID];
            var updatedPath = PromptText(
                $"Path for {selectedEntry.DisplayName} archive (leave blank to clear)",
                string.IsNullOrWhiteSpace(current) ? null : current);
            paths[selectedEntry.ArtifactID] = updatedPath;
        }
    }

    public static T PromptSelection<T>(string title, IReadOnlyList<MenuOption<T>> options, string? details = null) => AnsiConsole.Prompt(
        new SelectionPrompt<MenuOption<T>>()
            .Title($"[{ToMarkupColor(AppTheme.OrangeStyle.Foreground)}]{Escape(title)}[/]" +
                   $"{(!string.IsNullOrWhiteSpace(details) ? $"\n[gray]{details}[/]" : "")}")
            .PageSize(10)
            .HighlightStyle(AppTheme.GreenStyle)
            .UseConverter(option =>
            {
                string label = Escape(option.Label);

                if (string.IsNullOrWhiteSpace(option.Description))
                    return label;

                return $"{label} [gray]- {Escape(option.Description)}[/]";
            })
            .AddChoices(options)
    ).Value;

    public static IReadOnlyList<T> PromptMultiSelection<T>(
        string title,
        IReadOnlyList<MenuOption<T>> options,
        IReadOnlyCollection<T> selectedValues,
        string? details = null)
    {
        var prompt = new MultiSelectionPrompt<MenuOption<T>>()
            .Title($"[{ToMarkupColor(AppTheme.OrangeStyle.Foreground)}]{Escape(title)}[/]" +
                   $"{(!string.IsNullOrWhiteSpace(details) ? $"\n[gray]{details}[/]" : "")}")
            .PageSize(10)
            .NotRequired()
            .HighlightStyle(AppTheme.GreenStyle)
            .InstructionsText("[gray](Use [white]<space>[/] to toggle, [white]<enter>[/] to confirm)[/]")
            .UseConverter(option =>
            {
                var label = Escape(option.Label);

                if (string.IsNullOrWhiteSpace(option.Description))
                    return label;

                return $"{label} [gray]- {Escape(option.Description)}[/]";
            })
            .AddChoices(options);

        foreach (var option in options.Where(option => selectedValues.Contains(option.Value)))
            prompt.Select(option);

        return [..AnsiConsole.Prompt(prompt).Select(option => option.Value)];
    }

    public static bool Confirm(string title, bool defaultValue = true, bool warning = false) => AnsiConsole.Prompt(
        new TextPrompt<bool>($"{(warning ? $"[{ToMarkupColor(AppTheme.OrangeStyle.Foreground)} bold]WARNING:[/] {title}" : title)}")
            .AddChoice(true)
            .AddChoice(false)
            .DefaultValue(defaultValue)
            .ChoicesStyle(AppTheme.GreenStyle.Foreground)
            .DefaultValueStyle(AppTheme.OrangeStyle.Foreground)
            .WithConverter(choice => choice ? "y" : "n")
    );

    public static string PromptText(string title, string? defaultValue = null)
    {
        var prompt = new TextPrompt<string>($"[{ToMarkupColor(AppTheme.OrangeStyle.Foreground)}]{Escape(title)}[/]")
            .AllowEmpty();

        if (defaultValue is not null)
            prompt.DefaultValue(defaultValue);

        return AnsiConsole.Prompt(prompt).Trim().Trim('"', '\'');
    }

    public static void ShowConfigurationSummary(BuilderConfig config)
    {
        string green = ToMarkupColor(AppTheme.GreenStyle.Foreground);
        Table table  = new Table().Border(TableBorder.Rounded);

        table.AddColumn(new TableColumn(new Markup($"[bold {green}]Section[/]")).LeftAligned());
        table.AddColumn(new TableColumn(new Markup($"[bold {green}]Selection[/]")).LeftAligned());

        table.AddRow("Drive", $"[gray]{Escape(config.MountPoint ?? "None selected")}[/]");
        table.AddRow("Exploit", $"[gray]{Escape(config.SelectedExploit.ToString())}[/]");
        table.AddRow("Bootstrap", $"[gray]{Escape(config.SelectedBootstrap.ToString())}[/]");

        string[] homebrew = [..config.Homebrew
            .Select(homebrew => homebrew.ID == config.LaunchHomebrewId
                ? $"[{green}]{Escape(homebrew.DisplayName)}[/] [gray](launch)[/]"
                : $"[gray]{Escape(homebrew.DisplayName)}[/]")];

        table.AddRow("Homebrew", string.Join(", ", homebrew.DefaultIfEmpty("[gray]None[/]")));

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    public static void Pause(string message = "Press enter to continue...")
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Markup($"[gray]{Escape(message)}[/]");
        Console.ReadLine();
    }

    public static Task RunProgressAsync(IReadOnlyList<ProgressOperation> operations, CancellationToken cancellationToken)
    {
        return AnsiConsole.Progress()
            .AutoClear(false)
            .HideCompleted(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn().FinishedStyle(AppTheme.GreenStyle.Foreground).CompletedStyle(AppTheme.LightOrangeStyle.Foreground),
                new PercentageColumn().CompletedStyle(AppTheme.GreenStyle.Foreground),
                new RemainingTimeColumn().Style(Color.Gray),
                new TransferSpeedColumn()
            )
            .StartAsync(async context =>
            {
                var tasks = operations.Select(operation =>
                {
                    var task = context.AddTask(Escape(operation.Description), autoStart: true);
                    return operation.Action(new SpectreProgressTask(task), cancellationToken);
                });

                await Task.WhenAll(tasks);
            });
    }


    private static string Escape(string text)         => Markup.Escape(text);
    private static string ToMarkupColor(Color? color) => color is null ? "white" : $"rgb({color.Value.R},{color.Value.G},{color.Value.B})";


    private sealed class SpectreProgressTask : IProgressTask
    {
        private readonly ProgressTask _task;

        public SpectreProgressTask(ProgressTask task)
        {
            _task = task;
        }

        public void SetMaxValue(double value) => _task.MaxValue = Math.Max(value, 1);

        public void Increment(double value) => _task.Increment(value);
    }
}


internal sealed record MenuOption<TValue>(TValue Value, string Label, string Description = "");
internal sealed record ProgressOperation(string Description, Func<IProgressTask, CancellationToken, Task> Action);
internal sealed record ArchivePathEntry(string ArtifactID, string DisplayName, string? SuggestedPath, bool Required);

internal interface IProgressTask
{
    void SetMaxValue(double value);
    void Increment(double value);
}