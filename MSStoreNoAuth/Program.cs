using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.RegularExpressions;

internal static partial class Program
{
    private const string Version = "v1.3";
    private const uint WingetNoApplicationsFound = 0x8A150014;
    private const uint WingetUpdateNotApplicable = 0x8A15002B;
    private const uint WingetPackageAlreadyInstalled = 0x8A150061;
    private const uint WingetInstallerAlreadyInstalled = 0x8A15010D;

    private static readonly Dictionary<uint, string> WingetErrors = new()
    {
        { 0x80070005, "Access denied. Try running as Administrator." },
        { 0x800704C7, "Operation canceled. The install may have been aborted." },
        { 0x80073CF3, "The package failed dependency, conflict, or validation checks." },
        { 0x80073D02, "Another install is in progress. Wait for it to finish." },
        { 0x80070057, "Invalid argument. Verify the Store ID or URL." },
        { 0x80070422, "A required Windows service is disabled." },
        { 0x8A150010, "This package is not compatible with this system." },
        { 0x8A150014, "No package with this ID was found in the Microsoft Store source." },
        { 0x8A15001B, "The Microsoft Store is blocked by system policy." },
        { 0x8A15001C, "This Microsoft Store app is blocked by system policy." },
        { 0x8A15001E, "The Microsoft Store installation failed. See the winget output above." },
        { 0x8A150041, "The package agreements were not accepted." },
        { 0x8A150045, "The Microsoft Store source could not be opened." },
        { 0x8A150046, "The Microsoft Store source agreements were not accepted." },
        { 0x8A150056, "This installer cannot run from an Administrator session." },
        { 0x8A15006D, "A required service is busy or unavailable. Try again later." },
        { 0x8A150076, "This package requires interactive authentication." },
        { 0x8A15007D, "This user-scoped package cannot be changed from an Administrator session." },
        { 0x8A15007F, "winget could not read the Microsoft Store catalog." },
        { 0x8A150080, "No compatible Microsoft Store package is available for this system." },
        { 0x8A150083, "The Microsoft Store license could not be retrieved." },
        { 0x8A150085, "The current account is not permitted to retrieve this Store license." },
        { 0x8A150107, "The app requires a working network connection." },
    };

    private static readonly HashSet<uint> ManualRetryErrors =
    [
        0x8A150041, // package agreements not accepted
        0x8A150042, // prompt input error
        0x8A150076, // interactive authentication required
    ];

    private static readonly HttpClient HttpClient = CreateHttpClient();

    private static bool IsAdmin()
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

    private static async Task<int> Main(string[] args)
    {
        var options = ParseOptions(args);
        if (options.ShowHelp)
        {
            PrintHelp();
            return 0;
        }

        if (options.Error is not null)
        {
            Console.Error.WriteLine($"Error: {options.Error}\n");
            PrintHelp();
            return 2;
        }

        var interactiveSession = args.Length == 0;
        Console.Title = $"MSStoreNoAuth by primetime43 {Version}";
        Console.WriteLine($"MSStoreNoAuth by primetime43 {Version}. https://github.com/primetime43/MSStoreNoAuth");
        Console.WriteLine(IsAdmin() ? "[Running as Administrator]\n" : "[Running as standard user]\n");

        var finalExitCode = 0;
        do
        {
            var input = interactiveSession
                ? Prompt("Paste the Microsoft Store URL or just the Store ID:")
                : options.Input!;

            var storeId = ParseStoreId(input);
            if (storeId is null)
            {
                Console.Error.WriteLine("Could not parse a valid Microsoft Store ID.");
                return 2;
            }

            Console.WriteLine($"\nTarget app ID: {storeId}\n");

            var autoAccept = options.AutoAccept ?? (interactiveSession ? PromptForMode() : true);
            var result = await RunWinget(storeId, autoAccept);
            var usedWebInstaller = false;
            string? webInstallerError = null;

            if (autoAccept && !result.ReachedDesiredState && ManualRetryErrors.Contains(result.HResult))
            {
                Console.WriteLine("\nwinget requires interaction; retrying in manual mode...\n");
                result = await RunWinget(storeId, autoAccept: false);
            }

            if (result.HResult == WingetNoApplicationsFound)
            {
                Console.WriteLine("\nThis app is not exposed through winget's Microsoft Store catalog.");
                Console.WriteLine("Trying Microsoft's official Store Web Installer...\n");
                var webInstallerResult = await RunStoreWebInstaller(storeId);
                if (webInstallerResult.Success)
                {
                    result = new(0);
                    usedWebInstaller = true;
                }
                else
                {
                    webInstallerError = webInstallerResult.Error;
                }
            }

            if (result.AlreadyInstalled)
            {
                Console.WriteLine("Already installed; no newer applicable version is available.");
            }
            else if (result.ExitCode == 0)
            {
                Console.WriteLine(usedWebInstaller
                    ? "Successfully installed using Microsoft Store Web Installer."
                    : "Successfully installed.");
            }
            else
            {
                PrintWingetFailure(result);
                if (!string.IsNullOrWhiteSpace(webInstallerError))
                    Console.Error.WriteLine($"Store Web Installer error: {webInstallerError}");
                finalExitCode = 1;
            }

            if (!result.ReachedDesiredState && result.HResult == 0x80070422)
            {
                var serviceResult = await HandleDisabledServices(storeId, autoAccept);
                if (serviceResult is not null)
                {
                    result = serviceResult.Value;
                    if (result.ReachedDesiredState)
                    {
                        Console.WriteLine(result.AlreadyInstalled
                            ? "Already installed; no newer applicable version is available."
                            : "Successfully installed.");
                        finalExitCode = 0;
                    }
                    else
                    {
                        PrintWingetFailure(result);
                        finalExitCode = 1;
                    }
                }
            }

            if (!interactiveSession)
                break;

            Console.Write("\nInstall another? (Y/N): ");
            if (!string.Equals(Console.ReadLine()?.Trim(), "Y", StringComparison.OrdinalIgnoreCase))
                break;

            Console.Clear();
            finalExitCode = 0;
        } while (true);

        return finalExitCode;
    }

    private static CliOptions ParseOptions(string[] args)
    {
        if (args.Length == 0)
            return new(null, null, false, null);

        string? input = null;
        bool? autoAccept = null;

        foreach (var argument in args)
        {
            switch (argument.ToLowerInvariant())
            {
                case "-h":
                case "--help":
                case "/?":
                    return new(null, null, true, null);
                case "--auto":
                    if (autoAccept == false)
                        return new(null, null, false, "--auto and --manual cannot be used together.");
                    autoAccept = true;
                    break;
                case "--manual":
                    if (autoAccept == true)
                        return new(null, null, false, "--auto and --manual cannot be used together.");
                    autoAccept = false;
                    break;
                default:
                    if (argument.StartsWith('-'))
                        return new(null, null, false, $"Unknown option: {argument}");
                    if (input is not null)
                        return new(null, null, false, "Provide only one Microsoft Store URL or ID.");
                    input = argument;
                    break;
            }
        }

        return input is null
            ? new(null, null, false, "A Microsoft Store URL or ID is required.")
            : new(input, autoAccept, false, null);
    }

    private static void PrintHelp()
    {
        Console.WriteLine("MSStoreNoAuth - install Microsoft Store apps without an interactive account sign-in\n");
        Console.WriteLine("Usage:");
        Console.WriteLine("  MSStoreNoAuth.exe");
        Console.WriteLine("  MSStoreNoAuth.exe [--auto|--manual] <Store URL or ID>\n");
        Console.WriteLine("Options:");
        Console.WriteLine("  --auto      Accept package agreements and disable winget prompts (default with an argument)");
        Console.WriteLine("  --manual    Let winget prompt for package agreements");
        Console.WriteLine("  -h, --help  Show this help");
    }

    private static bool PromptForMode()
    {
        Console.WriteLine("Select install mode:");
        Console.WriteLine("  0) Auto-accept agreements");
        Console.WriteLine("  1) Manual (you'll confirm in winget)");
        Console.Write("Choice [0]: ");
        return Console.ReadLine()?.Trim() != "1";
    }

    private static string Prompt(string message)
    {
        Console.WriteLine(message);
        Console.Write("-> ");
        return Console.ReadLine()?.Trim() ?? string.Empty;
    }

    internal static string? ParseStoreId(string input)
    {
        input = input.Trim().Trim('"');
        if (input.Length == 0)
            return null;

        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri))
            return StoreIdRegex().IsMatch(input) ? input.ToUpperInvariant() : null;

        var supportedHost = uri.Host.Equals("apps.microsoft.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("www.microsoft.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("microsoft.com", StringComparison.OrdinalIgnoreCase);
        if (!supportedHost)
            return null;

        foreach (var segment in uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Reverse())
        {
            var candidate = Uri.UnescapeDataString(segment);
            if (StoreIdRegex().IsMatch(candidate))
                return candidate.ToUpperInvariant();
        }

        return null;
    }

    private static async Task<WingetResult> RunWinget(string id, bool autoAccept)
    {
        Console.WriteLine(autoAccept
            ? $"[Auto] Installing {id}...\n"
            : $"[Manual] Installing {id}...\n");

        var startInfo = new ProcessStartInfo("winget")
        {
            UseShellExecute = false,
            CreateNoWindow = false,
            // Manual mode must inherit the console so prompts that do not end in a
            // newline remain visible and can read directly from the user's input.
            RedirectStandardOutput = autoAccept,
            RedirectStandardError = autoAccept,
        };

        foreach (var argument in new[] { "install", "--id", id, "--exact", "--source", "msstore", "--accept-source-agreements" })
            startInfo.ArgumentList.Add(argument);

        if (autoAccept)
        {
            startInfo.ArgumentList.Add("--accept-package-agreements");
            startInfo.ArgumentList.Add("--disable-interactivity");
        }

        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start winget.");

            if (!autoAccept)
            {
                await process.WaitForExitAsync();
                return new(process.ExitCode);
            }

            var stdoutTask = PumpOutput(process.StandardOutput, Console.Out);
            var stderrTask = PumpOutput(process.StandardError, Console.Error);

            await process.WaitForExitAsync();
            await Task.WhenAll(stdoutTask, stderrTask);
            return new(process.ExitCode);
        }
        catch (Exception ex)
        {
            return new(1, ex.Message);
        }
    }

    private static async Task PumpOutput(StreamReader reader, TextWriter writer)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            await writer.WriteLineAsync(line);
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(2),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"MSStoreNoAuth/{Version.TrimStart('v')}");
        return client;
    }

    private static async Task<StoreInstallerResult> RunStoreWebInstaller(string storeId)
    {
        const int maximumInstallerSize = 20 * 1024 * 1024;
        var installerUri = new Uri(
            $"https://get.microsoft.com/installer/download/{Uri.EscapeDataString(storeId)}?cid=MSStoreNoAuth");
        var installerPath = Path.Combine(
            Path.GetTempPath(),
            $"MSStoreNoAuth-{storeId}-{Guid.NewGuid():N}.exe");

        try
        {
            using var response = await HttpClient.GetAsync(installerUri);
            if (!response.IsSuccessStatusCode)
                return new(false, $"Microsoft returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");

            var installerBytes = await response.Content.ReadAsByteArrayAsync();
            if (installerBytes.Length == 0)
                return new(false, "Microsoft returned an empty installer.");
            if (installerBytes.Length > maximumInstallerSize)
                return new(false, "The downloaded Store installer was unexpectedly large.");

            await File.WriteAllBytesAsync(installerPath, installerBytes);
            if (!HasTrustedAuthenticodeSignature(installerPath))
                return new(false, "The downloaded installer did not have a valid trusted signature.");

            var versionInfo = FileVersionInfo.GetVersionInfo(installerPath);
            if (!string.Equals(versionInfo.CompanyName, "Microsoft Corporation", StringComparison.OrdinalIgnoreCase))
                return new(false, "The downloaded installer was not published by Microsoft Corporation.");

            Console.WriteLine("Downloaded and verified Microsoft Store Installer. Starting installation...\n");
            using var process = Process.Start(new ProcessStartInfo(installerPath)
            {
                UseShellExecute = true,
            });
            if (process is null)
                return new(false, "Windows could not start the Store installer.");

            await process.WaitForExitAsync();
            return process.ExitCode == 0
                ? new(true, null)
                : new(false, $"The Store installer exited with code {process.ExitCode}.");
        }
        catch (Exception ex)
        {
            return new(false, ex.Message);
        }
        finally
        {
            try
            {
                if (File.Exists(installerPath))
                    File.Delete(installerPath);
            }
            catch
            {
                // Windows may briefly retain the installer after it exits. The
                // uniquely named file can safely remain in the temporary folder.
            }
        }
    }

    private static bool HasTrustedAuthenticodeSignature(string filePath)
    {
        var fileInfo = new WinTrustFileInfo
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            FilePath = filePath,
        };
        var fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());

        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, fDeleteOld: false);
            var trustData = new WinTrustData
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = 2,       // WTD_UI_NONE
                UnionChoice = 1,    // WTD_CHOICE_FILE
                FileInfoPointer = fileInfoPointer,
                StateAction = 0,    // WTD_STATEACTION_IGNORE
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
            return WinVerifyTrust(new IntPtr(-1), ref action, ref trustData) == 0;
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPointer);
            Marshal.FreeHGlobal(fileInfoPointer);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint WinVerifyTrust(
        IntPtr windowHandle,
        ref Guid actionId,
        ref WinTrustData trustData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint StructSize;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string FilePath;

        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfoPointer;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    private static void PrintWingetFailure(WingetResult result)
    {
        Console.Error.WriteLine($"winget exited {result.ExitCode} (0x{result.HResult:X8})");

        if (WingetErrors.TryGetValue(result.HResult, out var friendly))
            Console.Error.WriteLine($"Error: {friendly}");
        else if (!string.IsNullOrWhiteSpace(result.LaunchError))
            Console.Error.WriteLine($"Error launching winget: {result.LaunchError}");
        else
            Console.Error.WriteLine("winget did not provide more details. Run 'winget --info' to locate its diagnostic logs.");
    }

    private static async Task<WingetResult?> HandleDisabledServices(string storeId, bool autoAccept)
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
        return await RunWinget(storeId, autoAccept);
    }

    private static readonly string[] StoreServices =
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

        foreach (var service in StoreServices)
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

    [GeneratedRegex("^[A-Za-z0-9]{8,20}$", RegexOptions.CultureInvariant)]
    private static partial Regex StoreIdRegex();

    private readonly record struct CliOptions(string? Input, bool? AutoAccept, bool ShowHelp, string? Error);

    private readonly record struct WingetResult(int ExitCode, string? LaunchError = null)
    {
        public uint HResult => unchecked((uint)ExitCode);
        public bool AlreadyInstalled => HResult is WingetUpdateNotApplicable
            or WingetPackageAlreadyInstalled
            or WingetInstallerAlreadyInstalled;
        public bool ReachedDesiredState => ExitCode == 0 || AlreadyInstalled;
    }

    private readonly record struct StoreInstallerResult(bool Success, string? Error);
}
