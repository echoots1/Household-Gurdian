using System.Security.Cryptography;
using System.Text;
using Guardian.Contracts;
using Guardian.Service.Infrastructure;

namespace Guardian.Service.Web;

/// <summary>
/// Serves the packed extension and its update manifest from %ProgramData%\Guardian\ext\ (guardian.crx + guardian.id + guardian.version).
/// The forcelist entry points the browser here, so there is no store and no external host.
/// </summary>
public sealed class ExtensionHost
{
    private readonly Paths _paths;
    public ExtensionHost(Paths paths) => _paths = paths;

    public string CrxPath => Path.Combine(_paths.ExtDir, "guardian.crx");
    public bool Available => File.Exists(CrxPath);
    public string Id => ReadOr("guardian.id", "");
    public string Version => ReadOr("guardian.version", "1.0.0");

    private string ReadOr(string file, string fallback)
    {
        var p = Path.Combine(_paths.ExtDir, file);
        return File.Exists(p) ? File.ReadAllText(p).Trim() : fallback;
    }

    public string UpdateXml() =>
        $"""
        <?xml version='1.0' encoding='UTF-8'?>
        <gupdate xmlns='http://www.google.com/update2/response' protocol='2.0'>
          <app appid='{Id}'>
            <updatecheck codebase='http://127.0.0.1:{Ports.Local}/ext/guardian.crx' version='{Version}' />
          </app>
        </gupdate>
        """;

    /// <summary>Extension ID from a CRX3 file's public key (first 128 bits of SHA-256, hex mapped onto a–p).</summary>
    public static string IdFromCrx(byte[] crx)
    {
        // CRX3: "Cr24" | version=3 | headerLen | protobuf CrxFileHeader { repeated AsymmetricKeyProof sha256_with_rsa = 2 { bytes public_key = 1; ... } ... }
        if (crx.Length < 16 || Encoding.ASCII.GetString(crx, 0, 4) != "Cr24") return "";
        var headerLen = BitConverter.ToInt32(crx, 8);
        var header = crx.AsSpan(12, headerLen);
        var pub = FirstPublicKey(header);
        return pub is null ? "" : IdFromPublicKey(pub);
    }

    public static string IdFromPublicKey(byte[] spki)
    {
        var hash = SHA256.HashData(spki);
        var sb = new StringBuilder(32);
        foreach (var b in hash.AsSpan(0, 16)) { sb.Append((char)('a' + (b >> 4))); sb.Append((char)('a' + (b & 0xF))); }
        return sb.ToString();
    }

    private static byte[]? FirstPublicKey(ReadOnlySpan<byte> header)
    {
        var i = 0;
        while (i < header.Length)
        {
            var tag = ReadVarint(header, ref i);
            var field = tag >> 3; var wire = tag & 7;
            if (wire != 2) return null;
            var len = (int)ReadVarint(header, ref i);
            if (field == 2)
            {
                var proof = header.Slice(i, len); var j = 0;
                while (j < proof.Length)
                {
                    var t2 = ReadVarint(proof, ref j); var l2 = (int)ReadVarint(proof, ref j);
                    if ((t2 >> 3) == 1) return proof.Slice(j, l2).ToArray();
                    j += l2;
                }
            }
            i += len;
        }
        return null;
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> s, ref int i)
    {
        ulong v = 0; var shift = 0;
        while (i < s.Length) { var b = s[i++]; v |= (ulong)(b & 0x7F) << shift; if ((b & 0x80) == 0) break; shift += 7; }
        return v;
    }
}
