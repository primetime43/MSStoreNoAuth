using System.Diagnostics;
using System.Security.Principal;

internal static class StoreServices
{
    internal static bool IsAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void RelaunchAsAdmin(IEnumerable<string> arguments)
    {
        try
        {
            var exePath = Environment.ProcessPath ?? "MSStoreNoAuth.exe";
            var startInfo = new ProcessStartInfo(exePath)
            {
                UseShellExecute = true,
                Verb = "runas",
            };

            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);

            Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to relaunch as Administrator: {ex.Message}");
            Console.WriteLine("Please right-click the executable and select 'Run as administrator'.");
        }
    }

    internal static async Task<WingetResult?> HandleDisabledAsync(
        string storeId, bool autoAccept, Func<string, bool, Task<WingetResult>> runWinget)
    {
        if (!IsAdmin())
        {
            Console.WriteLine("\nThis error usually requires Administrator privileges to resolve.");
            Console.Write("Relaunch this app as Administrator? (Y/N): ");
            if (string.Equals(Console.ReadLine()?.Trim(), "Y", StringComparison.OrdinalIgnoreCase))
                RelaunchAsAdmin(new[] { autoAccept ? "--auto" : "--manual", storeId });

            return null;
        }

        Console.WriteLine("\nMicrosoft Store installs require several Windows services to be running.");
        Console.Write("Enable them and retry? (Y/N): ");
        if (!string.Equals(Console.ReadLine()?.Trim(), "Y", StringComparison.OrdinalIgnoreCase))
            return null;

        if (!await TryEnableStoreServices())
            return null;

        Console.WriteLine("\nRetrying install...\n");
        return await runWinget(storeId, autoAccept);
    }

    private static readonly string[] ServiceIds =
    [
        "wuauserv", "BITS", "UsoSvc", "DoSvc", "TokenBroker", "wlidsvc",
        "LicenseManager", "InstallService", "ClipSVC", "AppXSvc",
    ];

    private static readonly Dictionary<string, string> ServiceNames = new()
    {
        { "wuauserv", "Windows Update" },
        { "BITS", "Background Intelligent Transfer Service" },
        { "UsoSvc", "Update Orchestrator Service" },
        { "DoSvc", "Delivery Optimization" },
        { "TokenBroker", "Web Account Manager" },
        { "wlidsvc", "Microsoft Account Sign-in Assistant" },
        { "LicenseManager", "Windows License Manager Service" },
        { "InstallService", "Microsoft Store Install Service" },
        { "ClipSVC", "Client License Service" },
        { "AppXSvc", "AppX Deployment Service" },
    };

    private static async Task<bool> TryEnableStoreServices()
    {
        Console.WriteLine("Enabling required services...\n");
        var failedServices = new List<string>();

        foreach (var service in ServiceIds)
        {
            var name = ServiceNames.GetValueOrDefault(service, service);
            await RunProcess("sc", $"config {service} start= demand");
            var startResult = await RunProcess("net", $"start {service}");

            if (startResult is 0 or 2)
                Console.WriteLine($"  [{service}] {name} - OK");
            else
            {
                Console.WriteLine($"  [{service}] {name} - FAILED to start");
                failedServices.Add(service);
            }
        }

        Console.WriteLine();
        if (failedServices.Count == 0)
        {
            Console.WriteLine("All required services are running.");
            return true;
        }

        Console.WriteLine("Some services could not be started:");
        foreach (var service in failedServices)
            Console.WriteLine($"  sc config {service} start= demand && net start {service}");
        Console.WriteLine("\nRun those commands manually in an elevated terminal, or reset the source with:");
        Console.WriteLine("  winget source reset --force");
        return false;
    }

    private static async Task<int> RunProcess(string fileName, string arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Failed to start {fileName}.");
            await process.WaitForExitAsync();
            return process.ExitCode;
        }
        catch
        {
            return -1;
        }
    }
}
