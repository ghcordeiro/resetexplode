using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace ResetExplode;

/// <summary>Verificação de assinatura Authenticode feita pelo Windows (WinVerifyTrust) para os instaladores baixados pelo gerenciador de drivers.</summary>
internal static class SignatureCheck
{
    // ---- WinVerifyTrust (verificação de assinatura Authenticode feita pelo próprio Windows) ----
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    /// <summary>Organização (campo O=) do certificado que assinou o arquivo, ou null se não der para ler.
    /// Só faz sentido depois que WinVerifyTrustFile devolveu 0 (assinatura válida e confiável).</summary>
    internal static string? SignerOrganization(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            return cert.SubjectName.EnumerateRelativeDistinguishedNames()
                .Where(r => !r.HasMultipleElements && r.GetSingleElementType().Value == "2.5.4.10") // OID de O (Organization)
                .Select(r => r.GetSingleElementValue()).FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>Compara o nome inteiro da organização, ignorando maiúsculas, espaços e pontuação ("Inc." = "INC").
    /// Não aceita nome que só contém o esperado (ex.: "Fake NVIDIA Corporation LLC").</summary>
    internal static bool SameOrganization(string? org, params string[] expected)
    {
        static string Norm(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return org != null && expected.Any(e => Norm(org) == Norm(e));
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
    private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, ref WINTRUST_DATA pWVTData);

    internal static int WinVerifyTrustFile(string path)
    {
        var file = new WINTRUST_FILE_INFO { pcwszFilePath = path };
        file.cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>();
        IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(file, pFile, false);
            var data = new WINTRUST_DATA
            {
                dwUIChoice = 2,          // WTD_UI_NONE
                fdwRevocationChecks = 0, // WTD_REVOKE_NONE
                dwUnionChoice = 1,       // WTD_CHOICE_FILE
                pFile = pFile,
                dwStateAction = 0,       // WTD_STATEACTION_IGNORE
                dwProvFlags = 0x10,      // WTD_REVOCATION_CHECK_NONE
            };
            data.cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>();
            return WinVerifyTrust(new IntPtr(-1), new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"), ref data);
        }
        finally
        {
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(pFile);
            Marshal.FreeHGlobal(pFile);
        }
    }
}
