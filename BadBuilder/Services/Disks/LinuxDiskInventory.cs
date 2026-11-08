using System.Text.Json;

namespace BadBuilder.Services.Disks;

internal sealed record LinuxInventory(IReadOnlyList<DiskInfo> EligibleDisks, IReadOnlyList<LinuxBlockNode> AllNodes);

internal sealed class LinuxBlockNode
{
    internal required string Path { get; init; }
    internal required string Name { get; init; }
    internal string? ParentName { get; init; }
    internal required string Type { get; init; }
    internal long Size { get; init; }
    internal string? Model { get; init; }
    internal string? Serial { get; init; }
    internal string? Wwn { get; init; }
    internal string? Transport { get; init; }
    internal bool Removable { get; init; }
    internal bool HotPlug { get; init; }
    internal bool ReadOnly { get; init; }
    internal string? FileSystem { get; init; }
    internal string? Label { get; init; }
    internal string? Uuid { get; init; }
    internal string? MdUuid { get; init; }
    internal IReadOnlyList<string> MountPoints { get; init; } = [];
    internal LinuxBlockNode? Parent { get; set; }
    internal List<LinuxBlockNode> Children { get; } = [];
}

internal static class LinuxDiskInventory
{
    internal static LinuxInventory Parse(
        string lsblkJson,
        string findmntJson,
        string swapsText,
        IEnumerable<string> protectedPaths,
        Func<string, string?>? stableIdResolver = null)
    {
        List<LinuxBlockNode> nodes = ParseLsblk(lsblkJson);
        LinkFlatParents(nodes);
        Dictionary<string, LinuxBlockNode> byDevice = BuildDeviceMap(nodes);
        HashSet<string> protectedPathSet = BuildProtectedPaths(protectedPaths);

        HashSet<LinuxBlockNode> protectedDisks = [];
        HashSet<string> protectedUuids = new(StringComparer.OrdinalIgnoreCase);
        foreach (string source in GetProtectedMountSources(findmntJson, protectedPathSet))
        {
            if (TryFindNode(byDevice, source, out LinuxBlockNode node))
            {
                protectedDisks.Add(GetWholeDisk(node));
                AddUuid(node, protectedUuids);
            }
        }

        // lsblk mount data provides an independent safety signal when findmnt uses a source
        // alias that cannot be mapped back to a block node.
        foreach (LinuxBlockNode node in nodes)
        {
            foreach (string mountPoint in node.MountPoints)
            {
                string? target = LinuxProtectedPaths.TryResolveRealPath(mountPoint);
                if (target is null)
                    continue;
                if (protectedPathSet.Any(protectedPath => IsAtOrBelow(protectedPath, target)))
                {
                    protectedDisks.Add(GetWholeDisk(node));
                    AddUuid(node, protectedUuids);
                }
            }
        }

        foreach (string swapSource in ParseSwapSources(swapsText))
        {
            if (TryFindNode(byDevice, swapSource, out LinuxBlockNode node))
            {
                protectedDisks.Add(GetWholeDisk(node));
                AddUuid(node, protectedUuids);
            }
        }

        foreach (LinuxBlockNode swapNode in nodes.Where(node =>
            string.Equals(node.FileSystem, "swap", StringComparison.OrdinalIgnoreCase)))
        {
            protectedDisks.Add(GetWholeDisk(swapNode));
            AddUuid(swapNode, protectedUuids);
        }

        // Multi-device filesystems (btrfs, mdraid) expose the same UUID on every
        // member. Protect all of them, not just the member that carries the mount.
        foreach (LinuxBlockNode node in nodes)
        {
            if (HasProtectedUuid(node, protectedUuids))
                protectedDisks.Add(GetWholeDisk(node));
        }

        List<DiskInfo> eligible = [];
        foreach (LinuxBlockNode disk in nodes.Where(node => string.Equals(node.Type, "disk", StringComparison.Ordinal)))
        {
            // HOTPLUG is set on many internal SATA ports, so it is not evidence of
            // a removable device. Require a USB transport or the removable flag.
            bool external =
                string.Equals(disk.Transport, "usb", StringComparison.OrdinalIgnoreCase) ||
                disk.Removable;

            if (!external || disk.ReadOnly || !DiskFormatter.IsSupportedDiskSize(disk.Size) || protectedDisks.Contains(disk))
                continue;

            string? stableId = stableIdResolver?.Invoke(disk.Path);
            if (string.IsNullOrWhiteSpace(disk.Serial) &&
                string.IsNullOrWhiteSpace(disk.Wwn) &&
                string.IsNullOrWhiteSpace(stableId))
            {
                // Without a hardware or by-id identifier two identical sticks are
                // indistinguishable, so a post-confirmation swap cannot be detected.
                continue;
            }

            DiskIdentity identity = DiskIdentity.Create(
                disk.Path,
                disk.Model ?? disk.Name,
                disk.Serial,
                disk.Wwn,
                disk.Size,
                disk.Transport,
                stableId);

            IReadOnlyList<VolumeInfo> volumes = [.. Descendants(disk)
                .Where(node => !string.Equals(node.Type, "disk", StringComparison.Ordinal))
                .Select(node => new VolumeInfo(node.Path, node.Type, node.FileSystem, node.Label, node.MountPoints))];

            eligible.Add(new DiskInfo(identity, disk.Removable, disk.HotPlug, disk.ReadOnly, volumes));
        }

        return new LinuxInventory(eligible, nodes);
    }

    private static void AddUuid(LinuxBlockNode node, HashSet<string> uuids)
    {
        // Walk the parent chain: mdraid reports MD_UUID on the members, while the
        // mounted device is the array node above them.
        for (LinuxBlockNode? current = node; current is not null; current = current.Parent)
        {
            if (!string.IsNullOrWhiteSpace(current.Uuid))
                uuids.Add(current.Uuid);
            if (!string.IsNullOrWhiteSpace(current.MdUuid))
                uuids.Add(current.MdUuid);
        }
    }

    private static bool HasProtectedUuid(LinuxBlockNode node, HashSet<string> uuids) =>
        (!string.IsNullOrWhiteSpace(node.Uuid) && uuids.Contains(node.Uuid)) ||
        (!string.IsNullOrWhiteSpace(node.MdUuid) && uuids.Contains(node.MdUuid));

    internal static List<LinuxBlockNode> ParseLsblk(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("blockdevices", out JsonElement devices) || devices.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("lsblk JSON does not contain a blockdevices array.");

            List<LinuxBlockNode> nodes = [];
            foreach (JsonElement element in devices.EnumerateArray())
                ParseNode(element, null, nodes);
            return nodes;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("lsblk returned malformed JSON.", ex);
        }
    }

    private static LinuxBlockNode ParseNode(JsonElement element, LinuxBlockNode? parent, List<LinuxBlockNode> nodes)
    {
        string name = GetString(element, "kname") ?? GetString(element, "name")
            ?? throw new InvalidDataException("lsblk returned a device without a name.");
        string path = GetString(element, "path") ?? (name.StartsWith("/dev/", StringComparison.Ordinal) ? name : "/dev/" + name);

        LinuxBlockNode node = new()
        {
            Name = name,
            Path = path,
            ParentName = GetString(element, "pkname"),
            Type = GetString(element, "type") ?? string.Empty,
            Size = GetInt64(element, "size"),
            Model = GetString(element, "model"),
            Serial = GetString(element, "serial"),
            Wwn = GetString(element, "wwn"),
            Transport = GetString(element, "tran"),
            Removable = GetBoolean(element, "rm"),
            HotPlug = GetBoolean(element, "hotplug"),
            ReadOnly = GetBoolean(element, "ro"),
            FileSystem = GetString(element, "fstype"),
            Label = GetString(element, "label"),
            Uuid = GetString(element, "uuid"),
            MdUuid = GetString(element, "mduuid"),
            MountPoints = GetStringArray(element, "mountpoints"),
            Parent = parent,
        };

        nodes.Add(node);
        parent?.Children.Add(node);

        if (element.TryGetProperty("children", out JsonElement children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in children.EnumerateArray())
                ParseNode(child, node, nodes);
        }

        return node;
    }

    private static void LinkFlatParents(IReadOnlyList<LinuxBlockNode> nodes)
    {
        Dictionary<string, LinuxBlockNode> byName = nodes
            .GroupBy(node => node.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        foreach (LinuxBlockNode node in nodes.Where(node => node.Parent is null && !string.IsNullOrWhiteSpace(node.ParentName)))
        {
            string parentName = Path.GetFileName(node.ParentName!);
            if (byName.TryGetValue(parentName, out LinuxBlockNode? parent) && !ReferenceEquals(parent, node))
            {
                node.Parent = parent;
                if (!parent.Children.Contains(node))
                    parent.Children.Add(node);
            }
        }
    }

    private static Dictionary<string, LinuxBlockNode> BuildDeviceMap(IEnumerable<LinuxBlockNode> nodes)
    {
        Dictionary<string, LinuxBlockNode> result = new(StringComparer.Ordinal);
        foreach (LinuxBlockNode node in nodes)
        {
            result[node.Path] = node;
            result["/dev/" + Path.GetFileName(node.Name)] = node;
            result[node.Name] = node;
        }
        return result;
    }

    private static bool TryFindNode(Dictionary<string, LinuxBlockNode> devices, string source, out LinuxBlockNode node)
    {
        string normalized = source;
        int subvolume = normalized.IndexOf('[', StringComparison.Ordinal);
        if (subvolume >= 0)
            normalized = normalized[..subvolume];
        return devices.TryGetValue(normalized, out node!);
    }

    private static LinuxBlockNode GetWholeDisk(LinuxBlockNode node)
    {
        HashSet<LinuxBlockNode> seen = [];
        while (node.Parent is not null && seen.Add(node))
            node = node.Parent;
        return node;
    }

    private static IEnumerable<LinuxBlockNode> Descendants(LinuxBlockNode root)
    {
        Stack<LinuxBlockNode> pending = new(root.Children.Reverse<LinuxBlockNode>());
        while (pending.TryPop(out LinuxBlockNode? current))
        {
            yield return current;
            foreach (LinuxBlockNode child in current.Children.AsEnumerable().Reverse())
                pending.Push(child);
        }
    }

    private static HashSet<string> BuildProtectedPaths(IEnumerable<string> protectedPaths)
    {
        return new HashSet<string>(
            LinuxProtectedPaths.Resolve(protectedPaths.Concat(["/", "/boot", "/boot/efi", "/home"])),
            StringComparer.Ordinal);
    }

    private static List<string> GetProtectedMountSources(string json, IReadOnlySet<string> protectedPaths)
    {
        List<string> sources = [];
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("filesystems", out JsonElement fileSystems) || fileSystems.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("findmnt JSON does not contain a filesystems array.");

            foreach ((string Source, string Target) mount in FlattenFindmnt(fileSystems))
            {
                string? target = LinuxProtectedPaths.TryResolveRealPath(mount.Target);
                if (target is null)
                    continue;

                if (protectedPaths.Any(protectedPath => IsAtOrBelow(protectedPath, target)))
                    sources.Add(mount.Source);
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("findmnt returned malformed JSON.", ex);
        }

        return sources;
    }

    private static IEnumerable<(string Source, string Target)> FlattenFindmnt(JsonElement fileSystems)
    {
        foreach (JsonElement fileSystem in fileSystems.EnumerateArray())
        {
            string? source = GetString(fileSystem, "source");
            string? target = GetString(fileSystem, "target");
            if (!string.IsNullOrWhiteSpace(source) && !string.IsNullOrWhiteSpace(target))
                yield return (source, target);

            if (fileSystem.TryGetProperty("children", out JsonElement children) && children.ValueKind == JsonValueKind.Array)
            {
                foreach ((string Source, string Target) child in FlattenFindmnt(children))
                    yield return child;
            }
        }
    }

    private static bool IsAtOrBelow(string path, string mountTarget)
    {
        if (string.Equals(path, mountTarget, StringComparison.Ordinal))
            return true;
        string prefix = mountTarget == Path.DirectorySeparatorChar.ToString()
            ? mountTarget
            : mountTarget.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.Ordinal);
    }

    private static IEnumerable<string> ParseSwapSources(string text)
    {
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            string[] fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length > 0 && fields[0].StartsWith("/dev/", StringComparison.Ordinal))
                yield return fields[0];
        }
    }

    private static string? GetString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out JsonElement value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : value.ToString().Trim();
    }

    private static long GetInt64(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out JsonElement value))
            return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number))
            return number;
        return long.TryParse(GetString(element, property), out number) ? number : 0;
    }

    private static bool GetBoolean(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out JsonElement value))
            return false;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.TryGetInt32(out int number) && number != 0,
            JsonValueKind.String => value.GetString() is string text &&
                (text == "1" || bool.TryParse(text, out bool parsed) && parsed),
            _ => false,
        };
    }

    private static IReadOnlyList<string> GetStringArray(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out JsonElement value))
            return [];
        if (value.ValueKind == JsonValueKind.Array)
            return [..value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                .Select(item => item.GetString()!)];
        string? single = GetString(element, property);
        return string.IsNullOrWhiteSpace(single) ? [] : [single];
    }
}
