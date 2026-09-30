using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SwitchPilot.Infrastructure.Dependencies;

[SupportedOSPlatform("windows")]
internal static class Authenticode
{
    // Subject observed in the official 1.89 installer. A future publisher change requires review.
    private const string Publisher = "Nmap Software LLC";
    public static bool VerifyNpcap(string path)
    {
        var file = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = path };
        var memory = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        Marshal.StructureToPtr(file, memory, false);
        var data = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2, RevocationChecks = 1, UnionChoice = 1, File = memory, StateAction = 1, ProviderFlags = 0x80 };
        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        try
        {
            if (WinVerifyTrust(new IntPtr(-1), ref action, ref data) != 0) return false;
            var provider = WTHelperProvDataFromStateData(data.State);
            var signerPointer = WTHelperGetProvSignerFromChain(provider, 0, false, 0);
            if (signerPointer == IntPtr.Zero) return false;
            var signer = Marshal.PtrToStructure<ProviderSigner>(signerPointer);
            if (signer.CertificateCount == 0 || signer.Certificates == IntPtr.Zero) return false;
            var chain = Marshal.PtrToStructure<ProviderCertificate>(signer.Certificates);
            var context = Marshal.PtrToStructure<CertificateContext>(chain.Certificate);
            if (context.Length is 0 or > 65536) return false;
            var encoded = new byte[context.Length]; Marshal.Copy(context.Encoded, encoded, 0, encoded.Length);
            using var certificate = X509CertificateLoader.LoadCertificate(encoded);
            return certificate.GetNameInfo(X509NameType.SimpleName, false) == Publisher &&
                certificate.SubjectName.EnumerateRelativeDistinguishedNames().Any(r => r.GetSingleElementType().Value == "2.5.4.10" && r.GetSingleElementValue() == Publisher);
        }
        catch (CryptographicException) { return false; }
        finally
        {
            data.StateAction = 2; WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            Marshal.DestroyStructure<TrustFile>(memory); Marshal.FreeHGlobal(memory);
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProviderSigner { public uint Size, TimeLow, TimeHigh, CertificateCount; public IntPtr Certificates; }
    [StructLayout(LayoutKind.Sequential)] private struct ProviderCertificate { public uint Size; public IntPtr Certificate; }
    [StructLayout(LayoutKind.Sequential)] private struct CertificateContext { public uint Encoding; public IntPtr Encoded; public uint Length; }
    [DllImport("wintrust.dll", ExactSpelling = true)] private static extern IntPtr WTHelperProvDataFromStateData(IntPtr state);
    [DllImport("wintrust.dll", ExactSpelling = true)] private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr provider, uint signer, [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterSignerIndex);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct TrustFile { public uint Size; [MarshalAs(UnmanagedType.LPWStr)] public string Path; public IntPtr FileHandle, KnownSubject; }
    [StructLayout(LayoutKind.Sequential)] private struct TrustData
    {
        public uint Size; public IntPtr Policy, Sip; public uint UiChoice, RevocationChecks, UnionChoice; public IntPtr File;
        public uint StateAction; public IntPtr State, Url; public uint ProviderFlags, UiContext; public IntPtr SignatureSettings;
    }
    [DllImport("wintrust.dll", ExactSpelling = true)] private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
}
