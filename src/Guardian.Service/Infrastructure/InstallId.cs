using System.Security.Cryptography;

namespace Guardian.Service.Infrastructure;

/// <summary>A short random id created alongside the data-protection keys and deleted with them; it namespaces cookie names per install.</summary>
public static class InstallId
{
    public static string LoadOrCreate(DirectoryInfo keysDir)
    {
        var file = Path.Combine(keysDir.FullName, "install-id.txt");
        try
        {
            if (File.Exists(file)) { var id = File.ReadAllText(file).Trim(); if (id.Length == 8 && id.All(char.IsAsciiLetterOrDigit)) return id; }
        }
        catch { }
        var fresh = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        try { File.WriteAllText(file, fresh); } catch { }
        return fresh;
    }
}
