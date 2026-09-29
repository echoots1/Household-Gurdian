using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace Guardian.Core.Auth;

/// <summary>argon2id with a per-hash salt. Format: argon2id$iterations$memoryKB$parallelism$salt$hash (base64).</summary>
public static class PasswordHasher
{
    public const int MinLength = 12;

    public static string Hash(string password, int iterations = 3, int memoryKb = 65536, int parallelism = 2)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Derive(password, salt, iterations, memoryKb, parallelism);
        return $"argon2id${iterations}${memoryKb}${parallelism}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return false;
        var parts = stored.Split('$');
        if (parts.Length != 6 || parts[0] != "argon2id") return false;
        var salt = Convert.FromBase64String(parts[4]);
        var expected = Convert.FromBase64String(parts[5]);
        var actual = Derive(password, salt, int.Parse(parts[1]), int.Parse(parts[2]), int.Parse(parts[3]));
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] Derive(string password, byte[] salt, int iterations, int memoryKb, int parallelism)
    {
        using var a = new Argon2id(Encoding.UTF8.GetBytes(password)) { Salt = salt, Iterations = iterations, MemorySize = memoryKb, DegreeOfParallelism = parallelism };
        return a.GetBytes(32);
    }

    public static string? Validate(string? password) =>
        password is null || password.Length < MinLength ? $"Password must be at least {MinLength} characters." : null;
}
