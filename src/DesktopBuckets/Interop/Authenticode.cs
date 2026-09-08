using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace DesktopBuckets.Interop
{
    /// <summary>
    /// Authenticode checks for the downloaded installer. Two independent questions:
    /// <list type="number">
    /// <item><see cref="Verify"/> — is the signature intact (the file hash matches what
    /// was signed)? Answered by WinVerifyTrust. A self-signed publisher certificate
    /// makes the <i>chain</i> untrusted, which is reported separately from a tampered
    /// file, so those two outcomes are kept apart.</item>
    /// <item><see cref="SignerThumbprint"/> — who signed it? Compared by the caller
    /// against a thumbprint pinned in code.</item>
    /// </list>
    /// </summary>
    internal static class Authenticode
    {
        public enum Status
        {
            /// <summary>Signature intact and the chain is trusted by Windows.</summary>
            Valid,
            /// <summary>Signature intact; the chain isn't trusted (self-signed / expired
            /// / unknown CA). Fine when the signer is pinned by thumbprint.</summary>
            IntactUntrustedChain,
            NoSignature,
            /// <summary>Signature present but the file no longer matches it.</summary>
            Tampered,
            Other,
        }

        private const uint CERT_E_EXPIRED = 0x800B0101;
        private const uint CERT_E_UNTRUSTEDROOT = 0x800B0109;
        private const uint CERT_E_CHAINING = 0x800B010A;
        private const uint CERT_E_UNTRUSTEDCA = 0x800B0112;
        private const uint TRUST_E_NOSIGNATURE = 0x800B0100;
        private const uint TRUST_E_BAD_DIGEST = 0x80096010;
        private const uint TRUST_E_SUBJECT_FORM_UNKNOWN = 0x800B0003;

        public static Status Verify(string path, out int hresult)
        {
            hresult = WinVerify(path);
            return unchecked((uint)hresult) switch
            {
                0 => Status.Valid,
                CERT_E_UNTRUSTEDROOT or CERT_E_EXPIRED or CERT_E_CHAINING or CERT_E_UNTRUSTEDCA
                    => Status.IntactUntrustedChain,
                TRUST_E_NOSIGNATURE or TRUST_E_SUBJECT_FORM_UNKNOWN => Status.NoSignature,
                TRUST_E_BAD_DIGEST => Status.Tampered,
                _ => Status.Other,
            };
        }

        /// <summary>SHA-1 thumbprint (upper-case hex) of the certificate that signed the
        /// file, or null when it carries no signature. Does <b>not</b> validate the
        /// signature — pair with <see cref="Verify"/>.</summary>
        public static string? SignerThumbprint(string path)
        {
            try
            {
                using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
                return cert.Thumbprint;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ---- WinVerifyTrust --------------------------------------------

        private static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 =
            new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        private const uint WTD_UI_NONE = 2;
        private const uint WTD_REVOKE_NONE = 0;
        private const uint WTD_CHOICE_FILE = 1;
        private const uint WTD_STATEACTION_VERIFY = 1;
        private const uint WTD_STATEACTION_CLOSE = 2;
        private const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x1000; // never hit the network

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WINTRUST_FILE_INFO
        {
            public uint cbStruct;
            public string pcwszFilePath;
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

        [DllImport("wintrust.dll", ExactSpelling = true)]
        private static extern int WinVerifyTrust(IntPtr hwnd, [In] ref Guid pgActionID, ref WINTRUST_DATA pWVTData);

        private static int WinVerify(string path)
        {
            var fileInfo = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                pcwszFilePath = path,
            };
            IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
            try
            {
                Marshal.StructureToPtr(fileInfo, pFile, false);
                var data = new WINTRUST_DATA
                {
                    cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                    dwUIChoice = WTD_UI_NONE,
                    fdwRevocationChecks = WTD_REVOKE_NONE,
                    dwUnionChoice = WTD_CHOICE_FILE,
                    pFile = pFile,
                    dwStateAction = WTD_STATEACTION_VERIFY,
                    // Not WTD_SAFER_FLAG: with it, a hash mismatch is reported as
                    // TRUST_E_NOSIGNATURE and "tampered" is indistinguishable from "unsigned".
                    dwProvFlags = WTD_CACHE_ONLY_URL_RETRIEVAL,
                };
                var action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
                int hr = WinVerifyTrust(IntPtr.Zero, ref action, ref data);

                data.dwStateAction = WTD_STATEACTION_CLOSE;
                WinVerifyTrust(IntPtr.Zero, ref action, ref data);
                return hr;
            }
            finally
            {
                Marshal.DestroyStructure<WINTRUST_FILE_INFO>(pFile);
                Marshal.FreeHGlobal(pFile);
            }
        }
    }
}
