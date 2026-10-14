using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace RVNEX;

/// <summary>Matches RVTools 4.5+ RVToolsPasswordEncryption.ps1 (DPAPI, CurrentUser).</summary>
internal static class RvToolsPasswordEncryptor
{
    private const string RvToolsPasswordPrefix = "_RVToolsV3PWD";
    private const int CryptProtectUiForbidden = 0x1;

    public static string EncryptForRvTools(string password)
    {
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("A password is required.", nameof(password));

        byte[] clearBytes = Encoding.Unicode.GetBytes(password);
        GCHandle pinned = default;
        DataBlob encrypted = default;
        try
        {
            pinned = GCHandle.Alloc(clearBytes, GCHandleType.Pinned);
            var input = new DataBlob { Size = clearBytes.Length, Data = pinned.AddrOfPinnedObject() };
            if (!CryptProtectData(ref input, string.Empty, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out encrypted))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows DPAPI could not protect the RVTools password.");

            byte[] cipherBytes = new byte[encrypted.Size];
            Marshal.Copy(encrypted.Data, cipherBytes, 0, cipherBytes.Length);
            try { return RvToolsPasswordPrefix + Convert.ToHexString(cipherBytes).ToLowerInvariant(); }
            finally { CryptographicOperations.ZeroMemory(cipherBytes); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clearBytes);
            if (pinned.IsAllocated) pinned.Free();
            if (encrypted.Data != IntPtr.Zero) _ = LocalFree(encrypted.Data);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob input,
        string description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        out DataBlob output);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
