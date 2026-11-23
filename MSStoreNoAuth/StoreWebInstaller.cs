using System.Diagnostics;
using System.Runtime.InteropServices;

internal static class StoreWebInstaller
{
    private static readonly HttpClient HttpClient = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(2),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"MSStoreNoAuth/{Program.Version.TrimStart('v')}");
        return client;
    }

    internal static async Task<StoreInstallerResult> RunAsync(string storeId)
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
}

internal readonly record struct StoreInstallerResult(bool Success, string? Error);
