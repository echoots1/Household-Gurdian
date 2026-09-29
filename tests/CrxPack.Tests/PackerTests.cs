using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using CrxPack;
using Guardian.Service.Web;
using Xunit;

namespace CrxPack.Tests;

public class PackerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crxpack-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _ext;
    private readonly string _key;
    private readonly string _crx;

    public PackerTests()
    {
        _ext = Path.Combine(_root, "ext");
        _key = Path.Combine(_root, "key.pem");
        _crx = Path.Combine(_root, "out", "guardian.crx");
        Directory.CreateDirectory(Path.Combine(_ext, "icons"));
        File.WriteAllText(Path.Combine(_ext, "manifest.json"), """{ "manifest_version": 3, "name": "t", "version": "1.2.3" }""");
        File.WriteAllText(Path.Combine(_ext, "background.js"), "console.log('hi');");
        File.WriteAllBytes(Path.Combine(_ext, "icons", "icon16.png"), new byte[] { 1, 2, 3 });
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void Pack_writes_crx3_with_magic_version_and_sidecars()
    {
        var r = Packer.Pack(_ext, _key, _crx);
        var bytes = File.ReadAllBytes(_crx);

        Assert.Equal("Cr24", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal(3u, BitConverter.ToUInt32(bytes, 4));
        var headerLen = BitConverter.ToUInt32(bytes, 8);
        Assert.True(headerLen > 0 && 12 + headerLen < bytes.Length);

        Assert.Equal("1.2.3", r.Version);
        Assert.Equal(r.Id, File.ReadAllText(_crx + ".id").Trim());
        Assert.Equal("1.2.3", File.ReadAllText(_crx + ".version").Trim());
        Assert.Matches("^[a-p]{32}$", r.Id);
        Assert.True(File.Exists(_key));
        Assert.Contains("BEGIN PRIVATE KEY", File.ReadAllText(_key)); // PKCS#8
    }

    [Fact]
    public void Service_ExtensionHost_reads_same_id()
    {
        var r = Packer.Pack(_ext, _key, _crx);
        Assert.Equal(r.Id, ExtensionHost.IdFromCrx(File.ReadAllBytes(_crx)));
        Assert.Equal(r.Id, ExtensionHost.IdFromPublicKey(r.PublicKeySpki));
    }

    [Fact]
    public void Signature_verifies_with_public_key_and_key_is_reused()
    {
        var first = Packer.Pack(_ext, _key, _crx);
        var second = Packer.Pack(_ext, _key, _crx); // same key file -> same id
        Assert.Equal(first.Id, second.Id);

        var bytes = File.ReadAllBytes(_crx);
        var headerLen = (int)BitConverter.ToUInt32(bytes, 8);
        var header = bytes.AsSpan(12, headerLen);
        var zip = bytes.AsSpan(12 + headerLen).ToArray();

        byte[]? pub = null, sig = null, signedHeaderData = null;
        var i = 0;
        while (Proto.ReadField(header, ref i, out var field, out var value))
        {
            if (field == 2)
            {
                var j = 0;
                while (Proto.ReadField(value, ref j, out var f2, out var v2))
                {
                    if (f2 == 1) pub = v2;
                    if (f2 == 2) sig = v2;
                }
            }
            else if (field == 10000) signedHeaderData = value;
        }
        Assert.NotNull(pub); Assert.NotNull(sig); Assert.NotNull(signedHeaderData);

        // SignedData.crx_id must equal the first 16 bytes of SHA-256(SPKI).
        var k = 0;
        Assert.True(Proto.ReadField(signedHeaderData!, ref k, out var idField, out var crxId));
        Assert.Equal(1, idField);
        Assert.Equal(SHA256.HashData(pub!).AsSpan(0, 16).ToArray(), crxId);

        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(pub!, out _);
        Assert.True(rsa.VerifyData(Packer.SignedBytes(signedHeaderData!, zip), sig!, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

        // The payload is a readable zip with forward-slash paths and no directory entries.
        using var za = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        var names = za.Entries.Select(e => e.FullName).ToList();
        Assert.Equal(new[] { "background.js", "icons/icon16.png", "manifest.json" }, names);
        Assert.DoesNotContain(names, n => n.EndsWith('/'));
    }
}
