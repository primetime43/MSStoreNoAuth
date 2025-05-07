using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

class Program
{
    private static string _version = "v1.0";
    // HRESULT → friendly message map
    static readonly Dictionary<uint, string> WingetErrors = new()
    {
        { 0x80070005, "Access denied. Try running as Administrator." },
        { 0x800704C7, "Operation canceled. The install may have been aborted." },
        { 0x80073CF3, "Package not found in msstore source. Check the ID/URL." },
        { 0x80073D02, "Another install is in progress. Wait for it to finish." },
        { 0x80070057, "Invalid argument. Verify the Store ID or URL." },
    };

    static async Task<int> Main(string[] args)
    {
        Console.Title = $"MSStoreNoAuth by primetime43 {_version}";
        Console.WriteLine($"MSStoreNoAuth by primetime43 {_version}. https://github.com/primetime43/MSStoreNoAuth \n");
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
                await RunWinget(storeId, autoAccept: false);
            }

            // 5) Ask to repeat
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
}
