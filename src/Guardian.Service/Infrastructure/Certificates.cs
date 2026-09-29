using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Guardian.Service.Infrastructure;

/// <summary>
/// One self-signed certificate for https://&lt;hostname&gt;:47131, valid 10 years, with SANs for the hostname,
/// localhost, 127.0.0.1 and every LAN address. On Windows it is also placed in the machine's Trusted Root store so
/// the child's browser on this PC trusts https://localhost without a warning. The parent trusts it once per device.
/// </summary>
public static class Certificates
{
    public static X509Certificate2 LoadOrCreate(string pfxPath, ILogger log)
    {
        if (File.Exists(pfxPath))
        {
            try
            {
                var existing = new X509Certificate2(pfxPath, (string?)null, X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
                if (existing.NotAfter > DateTime.UtcNow.AddDays(30)) return existing;
                log.LogWarning("Certificate expires soon; generating a new one");
            }
            catch (Exception ex) { log.LogWarning(ex, "Could not load {Path}; generating a new certificate", pfxPath); }
        }
        var cert = Create();
        File.WriteAllBytes(pfxPath, cert.Export(X509ContentType.Pfx));
        var reloaded = new X509Certificate2(pfxPath, (string?)null, X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
        TrustOnThisMachine(reloaded, log);
        return reloaded;
    }

    public static X509Certificate2 Create()
    {
        var host = Dns.GetHostName();
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={host}, O=Guardian", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(host);
        san.AddDnsName(host.ToLowerInvariant());
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.IPv6Loopback);
        foreach (var ip in LanAddresses()) san.AddIpAddress(ip);
        try { var fqdn = Dns.GetHostEntry(host).HostName; if (!string.Equals(fqdn, host, StringComparison.OrdinalIgnoreCase)) san.AddDnsName(fqdn); } catch { }
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        var now = DateTimeOffset.UtcNow.AddDays(-1);
        return req.CreateSelfSigned(now, now.AddYears(10));
    }

    public static IEnumerable<IPAddress> LanAddresses()
    {
        var list = new List<IPAddress>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork) list.Add(ua.Address);
        }
        catch { }
        return list;
    }

    private static void TrustOnThisMachine(X509Certificate2 cert, ILogger log)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);
            foreach (var old in store.Certificates.Find(X509FindType.FindBySubjectName, "Guardian", false).Where(c => c.Subject.Contains("O=Guardian") && c.Thumbprint != cert.Thumbprint).ToList())
                store.Remove(old);
            using var pub = new X509Certificate2(cert.Export(X509ContentType.Cert));
            if (!store.Certificates.Contains(pub)) store.Add(pub);
        }
        catch (Exception ex) { log.LogWarning(ex, "Could not add the certificate to the machine root store; https://localhost will warn on this PC"); }
    }

    public static string Fingerprint(X509Certificate2 c) => string.Join(':', Enumerable.Range(0, c.Thumbprint.Length / 2).Select(i => c.Thumbprint.Substring(i * 2, 2)));
    public static byte[] PublicPem(X509Certificate2 c) => System.Text.Encoding.ASCII.GetBytes(new string(PemEncoding.Write("CERTIFICATE", c.Export(X509ContentType.Cert))));
}
