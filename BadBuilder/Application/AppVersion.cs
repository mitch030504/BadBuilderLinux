using System.Reflection;

namespace BadBuilder.Application;

internal static class AppVersion
{
    internal static string Informational { get; } =
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
        ?? "unknown";

    internal static string Display => Informational.Split('+', 2)[0];
}
