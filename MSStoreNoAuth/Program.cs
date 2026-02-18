using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Principal;
using System.Threading.Tasks;

class Program
{
    private static string _version = "v1.1";
    // HRESULT → friendly message map
    static readonly Dictionary<uint, string> WingetErrors = new()
    {
        { 0x80070005, "Access denied. Try running as Administrator." },
        { 0x800704C7, "Operation canceled. The install may have been aborted." },
        { 0x80073CF3, "Package not found in msstore source. Check the ID/URL." },
        { 0x80073D02, "Another install is in progress. Wait for it to finish." },
        { 0x80070057, "Invalid argument. Verify the Store ID or URL." },
        { 0x80070422, "A required Windows service is disabled. Microsoft Store installs need several services running." },
    };

    static bool IsAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    static void RelaunchAsAdmin(string[] args)
    {
        try
        {
            var exePath = Environment.ProcessPath ?? "MSStoreNoAuth.exe";
            var psi = new ProcessStartInfo(exePath)
            {
                UseShellExecute = true,
                Verb = "runas"
            };
            if (args.Length > 0)
                psi.Arguments = string.Join(" ", args);

            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to relaunch as Administrator: {ex.Message}");
            Console.WriteLine("Please right-click the .exe and select 'Run as administrator'.");
        }
    }

    static async Task<int> Main(string[] args)
    {
        Console.Title = $"MSStoreNoAuth by primetime43 {_version}";
        Console.WriteLine($"MSStoreNoAuth by primetime43 {_version}. https://github.com/primetime43/MSStoreNoAuth");
        Console.WriteLine(IsAdmin() ? "[Running as Administrator]\n" : "[Running as standard user]\n");
        do
        {
            // 1) Get or prompt for input
            string input = (args.Length == 1)
                ? args[0].Trim()
                : Prompt("Paste the Microsoft Store URL or just the Store ID:");

            if (string.IsNullOrWhiteSpace(input))
            {
                Console.WriteLine("No input provided. Exiting.");
                return 1;
            }

            // 2) Extract Store ID
            var storeId = ParseStoreId(input);
            if (string.IsNullOrWhiteSpace(storeId))
            {
                Console.WriteLine("Couldn’t parse a valid Store ID. Exiting.");
                return 1;
            }

            Console.WriteLine($"\nTarget app ID: {storeId}\n");

            // 3) Choose auto vs manual
            Console.WriteLine("Select install mode:");
            Console.WriteLine("  0) Auto-accept agreements");
            Console.WriteLine("  1) Manual (you’ll confirm in winget)");
            Console.Write("Choice [0]: ");
            var mode = Console.ReadLine()?.Trim() == "1" ? false : true;

            // 4) Try install (and fallback if auto fails)
            var result = await RunWinget(storeId, mode);
            if (mode && result.exitCode != 0)
            {
                Console.WriteLine("\nAuto-accept failed; switching to manual mode…\n");
                result = await RunWinget(storeId, autoAccept: false);
            }

            // 5) If install failed with 0x80070422, offer to fix services or relaunch as admin
            if (result.exitCode != 0 && unchecked((uint)result.exitCode) == 0x80070422)
            {
                if (!IsAdmin())
                {
                    Console.WriteLine("\nThis error usually requires Administrator privileges to resolve.");
                    Console.Write("Would you like to relaunch this app as Administrator? (Y/N): ");
                    var relaunch = Console.ReadLine()?.Trim().ToUpperInvariant();
                    if (relaunch == "Y")
                    {
                        RelaunchAsAdmin(new[] { storeId });
                        return 0;
                    }
                }
                else
                {
                    Console.WriteLine("\nMicrosoft Store installs require several Windows services to be running.");
                    Console.Write("Would you like to enable them and retry? (Y/N): ");
                    var fix = Console.ReadLine()?.Trim().ToUpperInvariant();
                    if (fix == "Y")
                    {
                        if (await TryEnableStoreServices())
                        {
                            Console.WriteLine("\nRetrying install…\n");
                            var retryResult = await RunWinget(storeId, mode);
                            if (retryResult.exitCode != 0 && unchecked((uint)retryResult.exitCode) == 0x80070422)
                            {
                                Console.WriteLine("\nStill failing. Try resetting winget sources:");
                                Console.WriteLine("  winget source reset --force");
                                Console.WriteLine("Then restart this app and try again.");
                            }
                        }
                    }
                }
            }

            // 6) Ask to repeat
            Console.Write("\nInstall another? (Y/N): ");
            var again = Console.ReadLine()?.Trim().ToUpperInvariant();
            if (again != "Y")
                break;

            Console.Clear();
            // clear args so we always prompt next iteration
            args = Array.Empty<string>();

        } while (true);

        return 0;
    }

    static string Prompt(string message)
    {
        Console.WriteLine(message);
        Console.Write("→ ");
        return Console.ReadLine()?.Trim() ?? "";
    }

    static string ParseStoreId(string input)
    {
        if (Uri.TryCreate(input, UriKind.Absolute, out var uri))
        {
            var segs = uri.AbsolutePath.TrimEnd('/').Split('/');
            var last = segs[^1];
            var q = last.IndexOf('?');
            return q >= 0 ? last[..q] : last;
        }
        return input;
    }

    static async Task<(int exitCode, string stdOut, string stdErr)> RunWinget(string id, bool autoAccept)
    {
        Console.WriteLine(autoAccept
            ? $"[Auto-accept] Installing {id}…\n"
            : $"[Manual] Installing {id}…\n");

        var args = autoAccept
            ? $"install {id} -s msstore --accept-source-agreements --accept-package-agreements"
            : $"install {id} -s msstore";

        var psi = new ProcessStartInfo("winget", args)
        {
            UseShellExecute = false,
            CreateNoWindow = false,
            RedirectStandardOutput = autoAccept,
            RedirectStandardError = autoAccept
        };

        int exitCode;
        string stdOut = "", stdErr = "";

        try
        {
            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start winget.");
            if (autoAccept)
            {
                stdOut = await proc.StandardOutput.ReadToEndAsync();
                stdErr = await proc.StandardError.ReadToEndAsync();
            }
            await proc.WaitForExitAsync();
            exitCode = proc.ExitCode;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error launching winget: {ex.Message}");
            return (1, "", ex.Message);
        }

        if (autoAccept && !string.IsNullOrWhiteSpace(stdOut))
            Console.WriteLine(stdOut);

        if (exitCode != 0)
        {
            uint h = unchecked((uint)exitCode);
            Console.WriteLine($"winget exited {exitCode} (0x{h:X8})");

            if (WingetErrors.TryGetValue(h, out var friendly))
                Console.WriteLine($"Error: {friendly}");
            else if (!string.IsNullOrWhiteSpace(stdErr))
                Console.WriteLine(stdErr);
        }
        else
        {
            Console.WriteLine("Successfully installed.");
        }

        return (exitCode, stdOut, stdErr);
    }

    // Services required for Microsoft Store installs
    static readonly string[] StoreServices =
    {
        "wuauserv", "BITS", "UsoSvc", "DoSvc",
        "TokenBroker", "wlidsvc", "LicenseManager",
        "InstallService", "ClipSVC", "AppXSvc"
    };
    static readonly Dictionary<string, string> ServiceNames = new()
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

    static async Task<bool> TryEnableStoreServices()
    {
        Console.WriteLine("Enabling required services…\n");
        var failedServices = new List<string>();

        foreach (var svc in StoreServices)
        {
            var name = ServiceNames.GetValueOrDefault(svc, svc);

            // Try to configure service to demand-start (may fail for trigger-start services — that's OK)
            await RunProcess("sc", $"config {svc} start= demand");

            // Try to start the service (exit code 2 = already running, which is fine)
            var startResult = await RunProcess("net", $"start {svc}");
            if (startResult == 0 || startResult == 2)
                Console.WriteLine($"  [{svc}] {name} — OK");
            else
            {
                Console.WriteLine($"  [{svc}] {name} — FAILED to start");
                failedServices.Add(svc);
            }
        }

        Console.WriteLine();

        if (failedServices.Count > 0)
        {
            Console.WriteLine("Some services could not be started:");
            foreach (var svc in failedServices)
                Console.WriteLine($"  sc config {svc} start= demand && net start {svc}");
            Console.WriteLine("\nTry running the above commands manually in an elevated PowerShell/Command Prompt.");
            Console.WriteLine("You can also try: winget source reset --force");
            return false;
        }

        Console.WriteLine("All required services are running.");
        return true;
    }

    static async Task<int> RunProcess(string fileName, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException($"Failed to start {fileName}.");
            await proc.WaitForExitAsync();
            return proc.ExitCode;
        }
        catch
        {
            return -1;
        }
    }
}
