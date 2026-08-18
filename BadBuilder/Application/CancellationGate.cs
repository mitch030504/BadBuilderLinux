namespace BadBuilder.Application;

internal static class CancellationGate
{
    private static int _blocked;

    internal static bool IsBlocked => Volatile.Read(ref _blocked) != 0;

    internal static IDisposable Block()
    {
        Interlocked.Increment(ref _blocked);
        return new Releaser();
    }

    private sealed class Releaser : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Interlocked.Decrement(ref _blocked);
        }
    }
}
