using BadBuilder.Services;
using BadBuilder.Services.Disks;

namespace BadBuilder.Application;

internal interface IPendingCleanupStore
{
    PreparedTarget? TryLoad();
    void Save(PreparedTarget target);
    void Clear();
}

internal sealed class FilePendingCleanupStore : IPendingCleanupStore
{
    internal static FilePendingCleanupStore Instance { get; } = new();

    public PreparedTarget? TryLoad() => PendingCleanupStore.TryLoad();
    public void Save(PreparedTarget target) => PendingCleanupStore.Save(target);
    public void Clear() => PendingCleanupStore.Clear();
}
