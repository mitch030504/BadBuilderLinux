namespace BadBuilder.UI;

internal sealed class ConsoleUserInterface : IUserInterface
{
    internal static ConsoleUserInterface Instance { get; } = new();

    public void PadLine() => Controls.PadLine();
    public void WriteInfo(string message) => Controls.WriteInfo(message);
    public void WriteWarning(string message) => Controls.WriteWarning(message);
    public void WriteError(string message) => Controls.WriteError(message);
    public void WriteSuccess(string message) => Controls.WriteSuccess(message);
    public bool Confirm(string title, bool defaultValue = true, bool warning = false) =>
        Controls.Confirm(title, defaultValue, warning);
    public string PromptText(string title, string? defaultValue = null) =>
        Controls.PromptText(title, defaultValue);
    public void Pause(string message = "Press enter to continue...") => Controls.Pause(message);
}
