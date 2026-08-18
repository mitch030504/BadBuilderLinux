using System.Text;

namespace BadBuilder.Services;

internal static class LaunchIniService
{
    internal static async Task<bool> UpdateDefaultAsync(
        string path,
        string defaultXboxPath,
        CancellationToken cancellationToken)
    {
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
            if (trimmed.Equals("[Paths]", StringComparison.OrdinalIgnoreCase))
            {
                pathsStart = index;
                for (int next = index + 1; next < lines.Count; next++)
                {
                    string candidate = lines[next].Trim();
                    if (candidate.Length >= 2 && candidate[0] == '[' && candidate[^1] == ']')
                    {
                        pathsEnd = next;
                        break;
                    }
                }
                break;
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
                    defaultLine = index;
                    break;
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
