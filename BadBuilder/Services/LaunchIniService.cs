using System.Text;
using BadBuilder.Configuration;

namespace BadBuilder.Services;

internal static class LaunchIniService
{
    private static readonly StringComparer StagedPathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal static string ValidateStagedEntryPoint(
        IReadOnlyList<(ArtifactDefinition Artifact, string StagingPath)> staged,
        HomebrewEntry launchHomebrew)
    {
        string artifactId = launchHomebrew.Artifact.ID;
        int stagedIndex = FindStagedIndex(staged, artifactId);
        if (stagedIndex < 0)
            throw new InvalidOperationException($"The selected homebrew artifact was not staged: {artifactId}");

        (ArtifactDefinition artifact, string stagingPath) = staged[stagedIndex];
        InstallOperation? copyOperation = null;
        foreach (InstallOperation operation in artifact.Operations ?? [])
        {
            if (operation.Kind != InstallOperationKind.CopyDirectory)
                continue;
            if (copyOperation is not null)
                throw new InvalidDataException($"{artifact.DisplayName} has more than one directory installation operation.");
            copyOperation = operation;
        }

        if (copyOperation is null)
            throw new InvalidDataException($"{artifact.DisplayName} does not install a homebrew directory.");

        string source = InstallService.ResolveStagedSource(stagingPath, copyOperation);
        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException($"Expected staged directory was not found: {source}");

        if (string.IsNullOrWhiteSpace(launchHomebrew.EntryPointRelativePath))
            throw new InvalidOperationException("The selected default homebrew has no valid entry point.");

        string entryPoint = PathSafety.NormalizeRelativePath(launchHomebrew.EntryPointRelativePath);
        FileServices.ValidateFatRelativePath(entryPoint);
        FileServices.EnsureFile(PathSafety.ResolveInside(source, entryPoint));

        // The launch path must be the path the plan will actually install to,
        // not a name derived from the display name.
        string destination = PathSafety.NormalizeRelativePath(copyOperation.DestinationPath);
        string destinationEntryPoint = destination == "."
            ? entryPoint
            : $"{destination}/{entryPoint}";
        FileServices.ValidateFatRelativePath(destinationEntryPoint);
        return destinationEntryPoint;
    }

    internal static string? FindStagedLaunchIni(
        IReadOnlyList<(ArtifactDefinition Artifact, string StagingPath)> staged,
        string bootstrapArtifactId)
    {
        int stagedIndex = FindStagedIndex(staged, bootstrapArtifactId);
        if (stagedIndex < 0)
            return null;

        (ArtifactDefinition bootstrap, string stagingPath) = staged[stagedIndex];
        HashSet<string> candidates = new(StagedPathComparer);
        foreach (InstallOperation operation in bootstrap.Operations ?? [])
        {
            if (operation.Kind == InstallOperationKind.CopyFile &&
                PathSafety.NormalizeRelativePath(operation.DestinationPath)
                    .Equals("launch.ini", StringComparison.OrdinalIgnoreCase))
            {
                string source = InstallService.ResolveStagedSource(stagingPath, operation);
                if (File.Exists(source))
                    candidates.Add(Path.GetFullPath(source));
            }
            else if (operation.Kind == InstallOperationKind.CopyDirectory &&
                     PathSafety.NormalizeRelativePath(operation.DestinationPath) == ".")
            {
                string source = InstallService.ResolveStagedSource(stagingPath, operation);
                string candidate = Path.Combine(source, "launch.ini");
                if (File.Exists(candidate))
                    candidates.Add(Path.GetFullPath(candidate));
            }
        }

        if (candidates.Count > 1)
        {
            throw new InvalidDataException(
                $"Multiple staged launch.ini candidates were found for {bootstrap.DisplayName}: " +
                string.Join(", ", candidates));
        }

        return candidates.Count == 1 ? candidates.First() : null;
    }

    private static int FindStagedIndex(
        IReadOnlyList<(ArtifactDefinition Artifact, string StagingPath)> staged,
        string artifactId)
    {
        for (int index = 0; index < staged.Count; index++)
        {
            if (staged[index].Artifact.ID == artifactId)
                return index;
        }

        return -1;
    }

    internal static async Task<bool> ConfigureStagedLaunchAsync(
        IReadOnlyList<(ArtifactDefinition Artifact, string StagingPath)> staged,
        string bootstrapArtifactId,
        HomebrewEntry launchHomebrew,
        string defaultXboxPath,
        CancellationToken cancellationToken)
    {
        string validated = ValidateStagedEntryPoint(staged, launchHomebrew);
        string expectedSuffix = validated.Replace('/', '\\');
        if (!defaultXboxPath.EndsWith(expectedSuffix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The launch path {defaultXboxPath} does not match the planned install path {validated}.");
        }

        string? iniPath = FindStagedLaunchIni(staged, bootstrapArtifactId);
        if (iniPath is null)
            return false;

        return await UpdateDefaultAsync(iniPath, defaultXboxPath, cancellationToken);
    }

    internal static async Task<bool> UpdateDefaultAsync(
        string path,
        string defaultXboxPath,
        CancellationToken cancellationToken)
    {
        ValidateDefaultPath(defaultXboxPath);
        if (!File.Exists(path))
            return false;

        byte[] original = await File.ReadAllBytesAsync(path, cancellationToken);
        (Encoding encoding, int preambleLength) = DetectEncoding(original);
        string text = encoding.GetString(original, preambleLength, original.Length - preambleLength);
        string lineEnding = DetectLineEnding(text);
        bool finalNewline = EndsWithLineEnding(text);
        List<string> lines = SplitLines(text);

        int pathsStart = -1;
        int pathsEnd = lines.Count;
        for (int index = 0; index < lines.Count; index++)
        {
            string trimmed = lines[index].Trim();
            if (!trimmed.Equals("[Paths]", StringComparison.OrdinalIgnoreCase))
                continue;

            if (pathsStart >= 0)
                throw new InvalidDataException("launch.ini contains more than one [Paths] section.");

            pathsStart = index;
            pathsEnd = lines.Count;
            for (int next = index + 1; next < lines.Count; next++)
            {
                string candidate = lines[next].Trim();
                if (candidate.Length >= 2 && candidate[0] == '[' && candidate[^1] == ']')
                {
                    pathsEnd = next;
                    break;
                }
            }
        }

        if (pathsStart < 0)
        {
            if (lines.Count > 0 && lines[^1].Length != 0)
                lines.Add(string.Empty);
            lines.Add("[Paths]");
            lines.Add($"Default = {defaultXboxPath}");
        }
        else
        {
            int defaultLine = -1;
            for (int index = pathsStart + 1; index < pathsEnd; index++)
            {
                string trimmed = lines[index].TrimStart();
                if (trimmed.StartsWith(';') || trimmed.StartsWith('#'))
                    continue;
                int equals = trimmed.IndexOf('=');
                if (equals >= 0 && trimmed[..equals].Trim().Equals("Default", StringComparison.OrdinalIgnoreCase))
                {
                    if (defaultLine >= 0)
                        throw new InvalidDataException("launch.ini contains more than one Default entry in [Paths].");
                    defaultLine = index;
                }
            }

            if (defaultLine >= 0)
            {
                string indentation = lines[defaultLine][..(lines[defaultLine].Length - lines[defaultLine].TrimStart().Length)];
                lines[defaultLine] = $"{indentation}Default = {defaultXboxPath}";
            }
            else
            {
                lines.Insert(pathsEnd, $"Default = {defaultXboxPath}");
            }
        }

        string updated = string.Join(lineEnding, lines);
        if (finalNewline && !updated.EndsWith(lineEnding, StringComparison.Ordinal))
            updated += lineEnding;

        byte[] body = encoding.GetBytes(updated);
        byte[] preamble = original.AsSpan(0, preambleLength).ToArray();
        byte[] output = new byte[preamble.Length + body.Length];
        preamble.CopyTo(output, 0);
        body.CopyTo(output, preamble.Length);
        await File.WriteAllBytesAsync(path, output, cancellationToken);
        return true;
    }

    private static void ValidateDefaultPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            path.Any(character => character < 32 || character is '"' or ';' or '#' or '='))
        {
            throw new InvalidDataException(
                $"The launch path contains characters that cannot be written safely to launch.ini: {path}");
        }
    }

    private static (Encoding Encoding, int PreambleLength) DetectEncoding(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()))
            return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true), 3);
        if (bytes.AsSpan().StartsWith(Encoding.Unicode.GetPreamble()))
            return (new UnicodeEncoding(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: true), 2);
        if (bytes.AsSpan().StartsWith(Encoding.BigEndianUnicode.GetPreamble()))
            return (new UnicodeEncoding(bigEndian: true, byteOrderMark: true, throwOnInvalidBytes: true), 2);

        try
        {
            _ = new UTF8Encoding(false, true).GetString(bytes);
            return (new UTF8Encoding(false, true), 0);
        }
        catch (DecoderFallbackException)
        {
            // Latin-1 preserves every original byte when no BOM/valid UTF-8 signal exists.
            return (Encoding.Latin1, 0);
        }
    }

    private static string DetectLineEnding(string text)
    {
        int crlf = text.IndexOf("\r\n", StringComparison.Ordinal);
        int lf = text.IndexOf('\n');
        int cr = text.IndexOf('\r');
        if (crlf >= 0 && (lf < 0 || crlf <= lf) && (cr < 0 || crlf <= cr))
            return "\r\n";
        if (lf >= 0 && (cr < 0 || lf < cr))
            return "\n";
        if (cr >= 0)
            return "\r";
        return "\r\n";
    }

    private static bool EndsWithLineEnding(string text) =>
        text.EndsWith("\r\n", StringComparison.Ordinal) ||
        text.EndsWith('\n') ||
        text.EndsWith('\r');

    private static List<string> SplitLines(string text)
    {
        if (text.Length == 0)
            return [];
        List<string> result = [];
        int start = 0;
        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] is not ('\r' or '\n'))
                continue;
            result.Add(text[start..index]);
            if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
                index++;
            start = index + 1;
        }
        if (start < text.Length)
            result.Add(text[start..]);
        return result;
    }
}
