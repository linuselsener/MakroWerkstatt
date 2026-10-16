using System.Runtime.InteropServices;

namespace MakroWerkstatt;

public interface IDataProtector
{
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] protectedData);
}

public sealed class MacroProtectionException(int? win32ErrorCode = null)
    : Exception("Das Makro konnte nicht verschlüsselt werden.")
{
    public int? Win32ErrorCode { get; } = win32ErrorCode;
}

public sealed class MacroDecryptionException(int? win32ErrorCode = null)
    : Exception("Dieses Makro konnte nicht entschlüsselt werden. Es ist möglicherweise für ein anderes Windows-Benutzerkonto verschlüsselt oder beschädigt.")
{
    public int? Win32ErrorCode { get; } = win32ErrorCode;
}

public sealed class DpapiDataProtector : IDataProtector
{
    private const uint UiForbidden = 0x1;
    private static readonly byte[] ZeroBuffer = new byte[8192];

    public byte[] Protect(byte[] plaintext) => Transform(plaintext, protect: true);

    public byte[] Unprotect(byte[] protectedData) => Transform(protectedData, protect: false);

    private static byte[] Transform(byte[] input, bool protect)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows DPAPI ist nur unter Windows verfügbar.");

        var inputBlob = new DataBlob { Length = input.Length, Data = Marshal.AllocHGlobal(Math.Max(1, input.Length)) };
        DataBlob outputBlob = default;
        IntPtr description = IntPtr.Zero;
        try
        {
            if (input.Length > 0) Marshal.Copy(input, 0, inputBlob.Data, input.Length);

            var succeeded = protect
                ? CryptProtectData(ref inputBlob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out outputBlob)
                : CryptUnprotectData(ref inputBlob, out description, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out outputBlob);
            if (!succeeded)
            {
                var errorCode = Marshal.GetLastWin32Error();
                if (protect) throw new MacroProtectionException(errorCode);
                throw new MacroDecryptionException(errorCode);
            }

            if (outputBlob.Length < 0)
            {
                if (protect) throw new MacroProtectionException();
                throw new MacroDecryptionException();
            }

            var output = new byte[outputBlob.Length];
            if (output.Length > 0) Marshal.Copy(outputBlob.Data, output, 0, output.Length);
            return output;
        }
        finally
        {
            ZeroUnmanagedMemory(inputBlob.Data, Math.Max(1, inputBlob.Length));
            Marshal.FreeHGlobal(inputBlob.Data);
            if (outputBlob.Data != IntPtr.Zero)
            {
                ZeroUnmanagedMemory(outputBlob.Data, Math.Max(1, outputBlob.Length));
                _ = LocalFree(outputBlob.Data);
            }
            if (description != IntPtr.Zero) _ = LocalFree(description);
        }
    }

    private static void ZeroUnmanagedMemory(IntPtr destination, int length)
    {
        var offset = 0;
        while (offset < length)
        {
            var count = Math.Min(ZeroBuffer.Length, length - offset);
            Marshal.Copy(ZeroBuffer, 0, IntPtr.Add(destination, offset), count);
            offset += count;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr optionalEntropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, out IntPtr description, IntPtr optionalEntropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
