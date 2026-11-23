using System.Runtime.Versioning;

[assembly: SupportedOSPlatform("windows")]

internal static class RegressionTests
{
    private const string StoreId = "9PFHDD62MXS1";
    private const uint NetworkUnreachable = 0x800704CF;
    private const uint NoApplicationsFound = 0x8A150014;
    private const uint AuthenticationRequired = 0x8A150076;
    private const uint UpdateNotApplicable = 0x8A15002B;
    private const uint AccessDenied = 0x80070005;

    private static async Task<int> Main()
    {
        try
        {
            await RunTests();
            return 0;
        }
        catch (InvalidOperationException error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    private static async Task RunTests()
    {
        var installed = new StoreInstallerResult(true, null);
        var installerFailed = new StoreInstallerResult(false, "Microsoft returned HTTP 503.");

        await Verify("Auto mode uses the web installer after a network error",
            "--auto", [(true, NetworkUnreachable)], installed, 0,
            "Store network error (0x800704CF)", "Successfully installed using Microsoft Store Web Installer.");

        await Verify("Manual mode uses the web installer after a network error",
            "--manual", [(false, NetworkUnreachable)], installed, 0,
            "Store network error (0x800704CF)", "Successfully installed using Microsoft Store Web Installer.");

        await Verify("Failed fallback reports both errors",
            "--auto", [(true, NetworkUnreachable)], installerFailed, 1,
            "winget exited -2147023665 (0x800704CF)", "winget --info",
            "Store Web Installer error: Microsoft returned HTTP 503.");

        await Verify("Network error on the manual retry uses the web installer",
            "--auto", [(true, AuthenticationRequired), (false, NetworkUnreachable)], installed, 0,
            "retrying in manual mode", "Successfully installed using Microsoft Store Web Installer.");

        await Verify("Missing catalog entries still use the web installer",
            "--auto", [(true, NoApplicationsFound)], installed, 0,
            "not exposed through winget", "Successfully installed using Microsoft Store Web Installer.");

        await Verify("Successful WinGet installs skip fallback",
            "--auto", [(true, 0)], null, 0, "Successfully installed.");

        await Verify("Already-installed packages skip fallback",
            "--auto", [(true, UpdateNotApplicable)], null, 0, "Already installed");

        await Verify("Unrelated failures skip fallback",
            "--auto", [(true, AccessDenied)], null, 1, "Access denied");
    }

    private static async Task Verify(
        string name,
        string mode,
        (bool AutoAccept, uint HResult)[] expectedAttempts,
        StoreInstallerResult? installerResult,
        int expectedExitCode,
        params string[] expectedMessages)
    {
        var attempts = new Queue<(bool AutoAccept, uint HResult)>(expectedAttempts);
        var webInstallerCalls = 0;
        var previousOut = Console.Out;
        var previousError = Console.Error;
        using var output = new StringWriter();
        int exitCode;

        try
        {
            Console.SetOut(output);
            Console.SetError(output);
            exitCode = await Program.RunAsync([mode, StoreId], (id, autoAccept) =>
            {
                if (id != StoreId || !attempts.TryDequeue(out var attempt) || attempt.AutoAccept != autoAccept)
                    throw new InvalidOperationException($"{name}: unexpected WinGet call for {id} with auto-accept {autoAccept}.");

                return Task.FromResult(new WingetResult(unchecked((int)attempt.HResult)));
            }, id =>
            {
                if (id != StoreId || installerResult is null || ++webInstallerCalls != 1)
                    throw new InvalidOperationException($"{name}: unexpected Store Web Installer call for {id}.");

                return Task.FromResult(installerResult.Value);
            });
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
        }

        var log = output.ToString();
        var expectedWebInstallerCalls = installerResult.HasValue ? 1 : 0;
        if (exitCode != expectedExitCode || attempts.Count != 0 || webInstallerCalls != expectedWebInstallerCalls)
            throw new InvalidOperationException(
                $"{name}: exit code {exitCode}, {attempts.Count} WinGet calls missing, {webInstallerCalls} web installer calls.\n{log}");

        foreach (var message in expectedMessages)
        {
            if (!log.Contains(message, StringComparison.Ordinal))
                throw new InvalidOperationException($"{name}: missing '{message}'.\n{log}");
        }

        Console.WriteLine($"PASS {name}");
    }
}
