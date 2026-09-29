using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CrxPack;

public sealed record PackResult(string Id, string Version, byte[] PublicKeySpki, byte[] Crx);

/// <summary>
/// CRX3 packer with no dependencies: deterministic zip + hand-rolled protobuf header + RSA-PKCS1v1.5-SHA256 signature.
/// Format (all integers little-endian):
///   "Cr24" | uint32 version=3 | uint32 headerLength | CrxFileHeader (protobuf) | zip
/// CrxFileHeader   { repeated AsymmetricKeyProof sha256_with_rsa = 2; bytes signed_header_data = 10000; }
/// AsymmetricKeyProof { bytes public_key = 1; bytes signature = 2; }
/// SignedData      { bytes crx_id = 1; }   // crx_id = first 16 bytes of SHA-256(SPKI)
/// Signature is over "CRX3 SignedData\0" + uint32 LE len(signed_header_data) + signed_header_data + zip.
/// </summary>
public static class Packer
{
    private static readonly byte[] SignaturePrefix = Encoding.ASCII.GetBytes("CRX3 SignedData\0");

    public static PackResult Pack(string extensionDir, string keyPemPath, string outCrxPath)
    {
        if (!Directory.Exists(extensionDir)) throw new DirectoryNotFoundException($"extension directory not found: {extensionDir}");
        var manifestPath = Path.Combine(extensionDir, "manifest.json");
        if (!File.Exists(manifestPath)) throw new FileNotFoundException("manifest.json not found in extension directory", manifestPath);

        using var rsa = LoadOrCreateKey(keyPemPath);
        var zip = ZipDirectory(extensionDir);
        var crx = BuildCrx(rsa, zip, out var spki);
        var id = IdFromPublicKey(spki);
        var version = ReadManifestVersion(manifestPath);

        var outDir = Path.GetDirectoryName(Path.GetFullPath(outCrxPath));
        if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);
        File.WriteAllBytes(outCrxPath, crx);
        File.WriteAllText(outCrxPath + ".id", id + "\n");
        File.WriteAllText(outCrxPath + ".version", version + "\n");
        return new PackResult(id, version, spki, crx);
    }

    public static RSA LoadOrCreateKey(string keyPemPath)
    {
        var rsa = RSA.Create();
        if (File.Exists(keyPemPath))
        {
            rsa.ImportFromPem(File.ReadAllText(keyPemPath));
            return rsa;
        }
        rsa.Dispose();
        rsa = RSA.Create(2048);
        var dir = Path.GetDirectoryName(Path.GetFullPath(keyPemPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(keyPemPath, rsa.ExportPkcs8PrivateKeyPem() + "\n");
        return rsa;
    }

    /// <summary>Deterministic zip: sorted forward-slash paths, no directory entries, fixed timestamps.</summary>
    public static byte[] ZipDirectory(string dir)
    {
        var root = Path.GetFullPath(dir);
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var rel in files)
            {
                var entry = zip.CreateEntry(rel, CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var es = entry.Open();
                using var fs = File.OpenRead(Path.Combine(root, rel));
                fs.CopyTo(es);
            }
        }
        return ms.ToArray();
    }

    public static byte[] BuildCrx(RSA rsa, byte[] zip, out byte[] spki)
    {
        spki = rsa.ExportSubjectPublicKeyInfo();
        var crxId = SHA256.HashData(spki).AsSpan(0, 16).ToArray();

        // SignedData { bytes crx_id = 1; }
        var signedHeaderData = Proto.Bytes(1, crxId);

        // Signature input: prefix + uint32 LE len(signedHeaderData) + signedHeaderData + zip
        var toSign = new MemoryStream();
        toSign.Write(SignaturePrefix);
        toSign.Write(BitConverter.GetBytes((uint)signedHeaderData.Length));
        toSign.Write(signedHeaderData);
        toSign.Write(zip);
        var signature = rsa.SignData(toSign.ToArray(), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        // AsymmetricKeyProof { bytes public_key = 1; bytes signature = 2; }
        var proof = Proto.Concat(Proto.Bytes(1, spki), Proto.Bytes(2, signature));
        // CrxFileHeader { repeated AsymmetricKeyProof sha256_with_rsa = 2; bytes signed_header_data = 10000; }
        var header = Proto.Concat(Proto.Bytes(2, proof), Proto.Bytes(10000, signedHeaderData));

        var outMs = new MemoryStream();
        outMs.Write(Encoding.ASCII.GetBytes("Cr24"));
        outMs.Write(BitConverter.GetBytes(3u));
        outMs.Write(BitConverter.GetBytes((uint)header.Length));
        outMs.Write(header);
        outMs.Write(zip);
        return outMs.ToArray();
    }

    /// <summary>Extension ID: first 16 bytes of SHA-256(SPKI), each nibble mapped to 'a'..'p'.</summary>
    public static string IdFromPublicKey(byte[] spki)
    {
        var hash = SHA256.HashData(spki);
        var sb = new StringBuilder(32);
        foreach (var b in hash.AsSpan(0, 16)) { sb.Append((char)('a' + (b >> 4))); sb.Append((char)('a' + (b & 0xF))); }
        return sb.ToString();
    }

    public static string ReadManifestVersion(string manifestPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (!doc.RootElement.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("manifest.json has no \"version\" string");
        return v.GetString()!;
    }

    /// <summary>Byte range the CRX3 signature covers, for verification: prefix + len + signed_header_data + zip.</summary>
    public static byte[] SignedBytes(byte[] signedHeaderData, byte[] zip)
    {
        var ms = new MemoryStream();
        ms.Write(SignaturePrefix);
        ms.Write(BitConverter.GetBytes((uint)signedHeaderData.Length));
        ms.Write(signedHeaderData);
        ms.Write(zip);
        return ms.ToArray();
    }
}

/// <summary>Minimal protobuf wire encoding: only length-delimited fields (wire type 2) are needed for CRX3.</summary>
public static class Proto
{
    public static byte[] Varint(ulong v)
    {
        var buf = new List<byte>(10);
        while (v >= 0x80) { buf.Add((byte)(v | 0x80)); v >>= 7; }
        buf.Add((byte)v);
        return buf.ToArray();
    }

    public static byte[] Bytes(int field, byte[] value)
        => Concat(Varint(((ulong)field << 3) | 2), Varint((ulong)value.Length), value);

    public static byte[] Concat(params byte[][] parts)
    {
        var ms = new MemoryStream();
        foreach (var p in parts) ms.Write(p);
        return ms.ToArray();
    }

    /// <summary>Reads one length-delimited field (wire type 2). Returns false at end of buffer.</summary>
    public static bool ReadField(ReadOnlySpan<byte> s, ref int i, out int field, out byte[] value)
    {
        field = 0; value = Array.Empty<byte>();
        if (i >= s.Length) return false;
        var tag = ReadVarint(s, ref i);
        if ((tag & 7) != 2) throw new InvalidDataException($"unexpected wire type {tag & 7}");
        field = (int)(tag >> 3);
        var len = (int)ReadVarint(s, ref i);
        value = s.Slice(i, len).ToArray();
        i += len;
        return true;
    }

    public static ulong ReadVarint(ReadOnlySpan<byte> s, ref int i)
    {
        ulong v = 0; var shift = 0;
        while (i < s.Length) { var b = s[i++]; v |= (ulong)(b & 0x7F) << shift; if ((b & 0x80) == 0) break; shift += 7; }
        return v;
    }
}
