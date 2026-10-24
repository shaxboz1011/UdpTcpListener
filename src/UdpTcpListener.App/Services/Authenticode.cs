using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;

namespace UdpTcpListener.App.Services;

/// <summary>Checks that a downloaded installer carries a valid Authenticode signature from the expected publisher.</summary>
[SupportedOSPlatform("windows")]
internal static class Authenticode
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public static bool IsSignedBy(string path, string expectedPublisher)
    {
        if (!VerifySignature(path) || SignerSubject(path) is not { } subject)
            return false;

        return subject.Split(',', StringSplitOptions.TrimEntries)
            .Any(part => part.Equals($"CN={expectedPublisher}", StringComparison.OrdinalIgnoreCase)
                         || part.Equals($"O={expectedPublisher}", StringComparison.OrdinalIgnoreCase));
    }

    private static string? SignerSubject(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057 // No replacement API reads the Authenticode signer certificate.
            return X509Certificate.CreateFromSignedFile(path).Subject;
#pragma warning restore SYSLIB0057
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool VerifySignature(string path)
    {
        var filePath = Marshal.StringToCoTaskMemUni(path);
        var fileInfo = new WinTrustFileInfo { FilePath = filePath };
        var fileInfoPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);
            var data = new WinTrustData { FileInfo = fileInfoPtr };
            var action = GenericVerifyV2;
            return WinVerifyTrust(IntPtr.Zero, ref action, data) == 0;
        }
        finally
        {
            Marshal.FreeCoTaskMem(fileInfoPtr);
            Marshal.FreeCoTaskMem(filePath);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid actionId, WinTrustData data);

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint StructSize;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;

        public WinTrustFileInfo() => StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>();
    }

    [StructLayout(LayoutKind.Sequential)]
    private sealed class WinTrustData
    {
        public uint StructSize = (uint)Marshal.SizeOf<WinTrustData>();
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice = 2;          // WTD_UI_NONE
        public uint RevocationChecks;      // WTD_REVOKE_NONE
        public uint UnionChoice = 1;       // WTD_CHOICE_FILE
        public IntPtr FileInfo;
        public uint StateAction;           // WTD_STATEACTION_IGNORE
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags = 0x1000; // WTD_CACHE_ONLY_URL_RETRIEVAL
        public uint UiContext;
        public IntPtr SignatureSettings;
    }
}
