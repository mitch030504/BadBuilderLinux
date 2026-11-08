namespace BadBuilder.UI;

internal interface IUserInterface
{
    void PadLine();
    void WriteInfo(string message);
    void WriteWarning(string message);
    void WriteError(string message);
    void WriteSuccess(string message);
    bool Confirm(string title, bool defaultValue = true, bool warning = false);
    string PromptText(string title, string? defaultValue = null);
    void Pause(string message = "Press enter to continue...");
}
