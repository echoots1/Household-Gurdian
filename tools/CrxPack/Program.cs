using CrxPack;

// Usage: CrxPack <extension-dir> <key.pem> <out.crx>
// Packs a directory into a CRX3 file signed with an RSA-2048 key (created if key.pem does not exist),
// prints ID=<extension id> and VERSION=<manifest version>, and writes <out.crx>.id / <out.crx>.version.
if (args.Length != 3)
{
    Console.Error.WriteLine("usage: CrxPack <extension-dir> <key.pem> <out.crx>");
    return 2;
}

try
{
    var result = Packer.Pack(args[0], args[1], args[2]);
    Console.WriteLine($"ID={result.Id}");
    Console.WriteLine($"VERSION={result.Version}");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"CrxPack: {ex.Message}");
    return 1;
}
