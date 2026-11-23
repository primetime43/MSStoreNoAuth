using System.Diagnostics;

internal static class WingetInstaller
{
    internal const uint NetworkUnreachable = 0x800704CF;
    internal const uint NoApplicationsFound = 0x8A150014;

    private static readonly Dictionary<uint, string> WingetErrors = new()
    {
        { 0x80070005, "Access denied. Try running as Administrator." },
        { 0x800704C7, "Operation canceled. The install may have been aborted." },
        { NetworkUnreachable, "Windows reported an unreachable network location during the Store install." },
        { 0x80073CF3, "The package failed dependency, conflict, or validation checks." },
        { 0x80073D02, "Another install is in progress. Wait for it to finish." },
        { 0x80070057, "Invalid argument. Verify the Store ID or URL." },
        { 0x80070422, "A required Windows service is disabled." },
        { 0x8A150010, "This package is not compatible with this system." },
        { NoApplicationsFound, "No package with this ID was found in the Microsoft Store source." },
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

    internal static bool RequiresInteraction(uint hResult) => ManualRetryErrors.Contains(hResult);

    internal static async Task<WingetResult> RunAsync(string id, bool autoAccept)
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

    internal static void PrintFailure(WingetResult result)
    {
        Console.Error.WriteLine($"winget exited {result.ExitCode} (0x{result.HResult:X8})");

        if (WingetErrors.TryGetValue(result.HResult, out var friendly))
            Console.Error.WriteLine($"Error: {friendly}");
        else if (!string.IsNullOrWhiteSpace(result.LaunchError))
            Console.Error.WriteLine($"Error launching winget: {result.LaunchError}");
        else
            Console.Error.WriteLine("winget did not provide more details. Run 'winget --info' to locate its diagnostic logs.");

        if (result.HResult == NetworkUnreachable)
        {
            Console.Error.WriteLine("Check that Windows can reach Microsoft Store and Windows Update through any proxy or firewall.");
            Console.Error.WriteLine("Run 'winget --info' to locate its diagnostic logs.");
        }
    }
}

internal readonly record struct WingetResult(int ExitCode, string? LaunchError = null)
{
    private const uint WingetUpdateNotApplicable = 0x8A15002B;
    private const uint WingetPackageAlreadyInstalled = 0x8A150061;
    private const uint WingetInstallerAlreadyInstalled = 0x8A15010D;

    public uint HResult => unchecked((uint)ExitCode);
    public bool AlreadyInstalled => HResult is WingetUpdateNotApplicable
        or WingetPackageAlreadyInstalled
        or WingetInstallerAlreadyInstalled;
    public bool ReachedDesiredState => ExitCode == 0 || AlreadyInstalled;
}
