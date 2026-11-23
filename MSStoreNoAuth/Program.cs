using System.Text.RegularExpressions;

internal static partial class Program
{
    internal const string Version = "v1.4";

    private static Task<int> Main(string[] args) => RunAsync(args, WingetInstaller.RunAsync, StoreWebInstaller.RunAsync);

    internal static async Task<int> RunAsync(
        string[] args,
        Func<string, bool, Task<WingetResult>> runWinget,
        Func<string, Task<StoreInstallerResult>> runStoreWebInstaller)
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
        Console.WriteLine(StoreServices.IsAdmin() ? "[Running as Administrator]\n" : "[Running as standard user]\n");

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
            var result = await runWinget(storeId, autoAccept);
            var usedWebInstaller = false;
            string? webInstallerError = null;

            if (autoAccept && !result.ReachedDesiredState && WingetInstaller.RequiresInteraction(result.HResult))
            {
                Console.WriteLine("\nwinget requires interaction; retrying in manual mode...\n");
                result = await runWinget(storeId, false);
            }

            if (result.HResult is WingetInstaller.NoApplicationsFound or WingetInstaller.NetworkUnreachable)
            {
                Console.WriteLine(result.HResult == WingetInstaller.NetworkUnreachable
                    ? "\nwinget reported a Store network error (0x800704CF)."
                    : "\nThis app is not exposed through winget's Microsoft Store catalog.");
                Console.WriteLine("Trying Microsoft's official Store Web Installer...\n");
                var webInstallerResult = await runStoreWebInstaller(storeId);
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
                WingetInstaller.PrintFailure(result);
                if (!string.IsNullOrWhiteSpace(webInstallerError))
                    Console.Error.WriteLine($"Store Web Installer error: {webInstallerError}");
                finalExitCode = 1;
            }

            if (!result.ReachedDesiredState && result.HResult == 0x80070422)
            {
                var serviceResult = await StoreServices.HandleDisabledAsync(storeId, autoAccept, runWinget);
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
                        WingetInstaller.PrintFailure(result);
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

    [GeneratedRegex("^[A-Za-z0-9]{8,20}$", RegexOptions.CultureInvariant)]
    private static partial Regex StoreIdRegex();

    private readonly record struct CliOptions(string? Input, bool? AutoAccept, bool ShowHelp, string? Error);
}
