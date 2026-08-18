using BadBuilder.Application;
using System.Security.Principal;
using System.Runtime.InteropServices;

internal static class Program
{
    static async Task Main()
    {
        if (!IsElevated())
        {
            Console.WriteLine("This application must be run with elevated privileges (as Administrator or root).");
            Console.Write("Press any key to exit...");
            Console.ReadKey();
            return;
        }

        await BuilderApp.RunAsync(CancellationToken.None);
    }

    static bool IsElevated()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        else
            return GetEffectiveUserID() == 0;
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserID();
}