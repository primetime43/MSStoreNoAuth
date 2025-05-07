using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

class Program
{
    // Common winget error codes → friendly text
    static readonly Dictionary<uint, string> WingetErrors = new()
    {
        { 0x80070005, "Access denied. Try running as Administrator." },
        { 0x800704C7, "Operation canceled. The install may have been aborted." },
        { 0x80073CF3, "Package not found in msstore source. Check the ID/URL." },
        { 0x80073D02, "Another install is in progress. Wait for it to finish." },
        { 0x80070057, "Invalid argument. Verify the Store ID or URL." },
        // add more as you encounter them…
    };

    static async Task<int> Main(string[] args)
    {
        // 1) Get input
        var input = args.Length == 1
            ? args[0].Trim()
            : Prompt("Paste the Microsoft Store URL or just the Store ID:");

        if (string.IsNullOrEmpty(input))
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

        // 3) Pick mode
        Console.WriteLine("Select install mode:");
        Console.WriteLine("  0) Auto-accept agreements");
        Console.WriteLine("  1) Manual (you’ll confirm in winget)");
        Console.Write("Choice [0]: ");
        var modeInput = Console.ReadLine()?.Trim();
        bool autoMode = (modeInput == "1") ? false : true;

        // 4) Try your chosen mode (auto or manual)
        var result = await RunWinget(storeId, autoMode);

        // 5) If auto failed, fall back to manual
        if (autoMode && result.exitCode != 0)
        {
            Console.WriteLine("\nAuto-accept failed, falling back to manual mode…\n");
            result = await RunWinget(storeId, autoAccept: false);
        }

        return result.exitCode;
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
            // last segment of path, strip query
            var segs = uri.AbsolutePath.TrimEnd('/').Split('/');
            var last = segs[^1];
            var q = last.IndexOf('?');
            return (q >= 0) ? last[..q] : last;
        }
        return input; // assume raw ID
    }

    static async Task<(int exitCode, string stdOut, string stdErr)> RunWinget(string id, bool autoAccept)
    {
        Console.WriteLine(autoAccept
            ? $"[Auto-accept] Installing {id}…\n"
            : $"[Manual] Installing {id}…\n");

        // build args
        var args = autoAccept
            ? $"install {id} -s msstore --accept-source-agreements --accept-package-agreements"
            : $"install {id} -s msstore";

        var psi = new ProcessStartInfo("winget", args)
        {
            UseShellExecute = false,
            CreateNoWindow = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = autoAccept, // capture only if auto
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

        // show auto-mode output
        if (autoAccept && !string.IsNullOrWhiteSpace(stdOut))
            Console.WriteLine(stdOut);

        // handle errors
        if (exitCode != 0)
        {
            uint h = unchecked((uint)exitCode);
            Console.WriteLine($"winget exited {exitCode} (0x{h:X8})");

            if (WingetErrors.TryGetValue(h, out var friendly))
                Console.WriteLine($"Error: {friendly}");
            else if (autoAccept && !string.IsNullOrWhiteSpace(stdErr))
                Console.WriteLine(stdErr);
        }

        return (exitCode, stdOut, stdErr);
    }
}
