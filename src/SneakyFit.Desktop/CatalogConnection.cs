using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SneakyFit.Application;
using SneakyFit.Infrastructure;

namespace SneakyFit.Desktop;

public static class CatalogConnection
{
    public const string DefaultEndpoint = "https://185.74.44.232:8443/";
    public const string CertificatePin = "7C7736BDFDAC4AEA79E5669914AC26C873745831756A04AF6AB437FC833D0C09";
    private sealed record StoredCredentials(string Endpoint, string Pin, string Token, string UserId);
    public static string CredentialsPath(Guid profileId) => Path.Combine(PortablePaths.RootDirectory, "UserData", "Secrets", profileId.ToString("N") + ".json");
    public static DeviceCredentials? LoadCredentials(Guid profileId)
    {
        var path = CredentialsPath(profileId); if (!File.Exists(path)) return null;
        try
        {
            if (new FileInfo(path).Length > 16384) return null;
            var encrypted = Convert.FromBase64String(File.ReadAllText(path)); var plain = Protect(encrypted, false);
            try
            {
                var stored = JsonSerializer.Deserialize<StoredCredentials>(plain);
                return stored is not null && stored.Endpoint == DefaultEndpoint && stored.Pin == CertificatePin ? new(stored.Token, stored.UserId) : null;
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (Exception e) when (e is IOException or FormatException or JsonException or Win32Exception or CryptographicException) { return null; }
    }
    public static void SaveCredentials(Guid profileId, string token, string userId)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 1024 || string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("Некорректный ключ устройства.");
        var plain = JsonSerializer.SerializeToUtf8Bytes(new StoredCredentials(DefaultEndpoint, CertificatePin, token, userId));
        try
        {
            var path = CredentialsPath(profileId); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllText(temporary, Convert.ToBase64String(Protect(plain, true))); File.Move(temporary, path, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public static void ForgetCredentials(Guid profileId) { var path = CredentialsPath(profileId); if (File.Exists(path)) File.Delete(path); }
    private static byte[] Protect(byte[] source, bool encrypt)
    {
        var input = new Blob { Length = source.Length, Data = Marshal.AllocHGlobal(source.Length) }; Blob output = default;
        try
        {
            Marshal.Copy(source, 0, input.Data, source.Length);
            var ok = encrypt ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
            var bytes = new byte[output.Length]; Marshal.Copy(output.Data, bytes, 0, bytes.Length); return bytes;
        }
        finally
        {
            for (var i = 0; i < source.Length; i++) Marshal.WriteByte(input.Data, i, 0);
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero) { for (var i = 0; i < output.Length; i++) Marshal.WriteByte(output.Data, i, 0); LocalFree(output.Data); }
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
