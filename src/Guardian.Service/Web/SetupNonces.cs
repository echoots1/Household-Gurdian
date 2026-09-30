using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Guardian.Service.Web;

/// <summary>
/// Synchronizer tokens for the one-shot setup form: a random value issued with the page and remembered in memory,
/// consumed by the submit. A cross-site page cannot read it, so it is a CSRF defense; it needs no cookie and no key ring.
/// </summary>
public sealed class SetupNonces
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(2);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _issued = new();

    public string Issue()
    {
        foreach (var (k, at) in _issued) if (DateTimeOffset.UtcNow - at > Lifetime) _issued.TryRemove(k, out _);
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        _issued[nonce] = DateTimeOffset.UtcNow;
        return nonce;
    }

    /// <summary>True once per issued nonce, within its lifetime.</summary>
    public bool Consume(string? nonce) =>
        !string.IsNullOrEmpty(nonce) && _issued.TryRemove(nonce, out var at) && DateTimeOffset.UtcNow - at <= Lifetime;
}
