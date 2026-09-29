using System.Security.Cryptography;
using System.Text;

namespace Guardian.Core.Auth;

/// <summary>DPAPI (LocalMachine scope, only readable as SYSTEM/admin on this PC) on Windows; a marked plain fallback elsewhere for tests.</summary>
public static class Secrets
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Guardian.v1");

    public static string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return null;
        if (!OperatingSystem.IsWindows()) return "plain:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(plain));
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.LocalMachine);
        return "dpapi:" + Convert.ToBase64String(bytes);
    }

    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;
        try
        {
            if (stored.StartsWith("plain:")) return Encoding.UTF8.GetString(Convert.FromBase64String(stored[6..]));
            if (stored.StartsWith("dpapi:") && OperatingSystem.IsWindows())
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored[6..]), Entropy, DataProtectionScope.LocalMachine));
        }
        catch (CryptographicException) { }
        return null;
    }
}
