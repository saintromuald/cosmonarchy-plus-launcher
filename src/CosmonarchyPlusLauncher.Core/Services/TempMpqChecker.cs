using System.Text;

namespace CosmonarchyPlusLauncher.Core.Services;

/// <summary>Read-only Storm MPQ v0 verification for Cosmonarchy's temp.mpq.
///
/// The hotloader rebuilds temp.mpq (~950 MB) when the data tree changes; an
/// interrupted rebuild leaves it truncated and the game fails later with
/// "unable to open a required file ... Bad length". This detects exactly that:
/// header/tables parse, no hash or block entry points past EOF, and every file
/// of the extracted <build>/mpq/ tree resolves to a live block. Ported from
/// the script-based checker with the same semantics (header + tables only,
/// no file data read).</summary>
public static class TempMpqChecker
{
    private const uint FlagDeleted = 0x02000000;
    private const uint FlagExists = 0x80000000;
    private const uint EmptySlot = 0xFFFFFFFF;

    private static readonly uint[] Crypt = BuildCryptTable();

    public sealed record Probe(int HeaderOk, int HashEntries, int BlockEntries, int Missing, int TreeFiles, string Fail, string FirstMissing);

    public static uint HashTableKey => HashString("(hash table)", 3);

    public static string ProbeFirstEntry(string mpqPath)
    {
        using var f = File.OpenRead(mpqPath);
        var header = new byte[32];
        f.Read(header, 0, 32);
        var htOff = ReadU32(header, 16);
        f.Seek(htOff, SeekOrigin.Begin);
        var ht = new byte[16];
        f.Read(ht, 0, 16);
        var dec = new Cipher(HashString("(hash table)", 3));
        var hA = dec.Decrypt(ReadU32(ht, 0));
        var hB = dec.Decrypt(ReadU32(ht, 4));
        var loc = dec.Decrypt(ReadU32(ht, 8));
        var blk = dec.Decrypt(ReadU32(ht, 12));
        return $"htOff={htOff} hA={hA:X8} hB={hB:X8} loc={loc:X8} blk={blk}";
    }
    public static uint BlockTableKey => HashString("(block table)", 3);

    public static Probe ProbeArchive(string mpqPath, string treeDir)
    {
        var header = new byte[32];
        using (var f = File.OpenRead(mpqPath))
            f.Read(header, 0, 32);
        var archive = new Archive(mpqPath);
        return new Probe(
            header[0] == (byte)'M' ? 1 : 0,
            (int)ReadU32(header, 24),
            (int)ReadU32(header, 28),
            archive.Missing(treeDir),
            Directory.Exists(treeDir) ? Directory.EnumerateFiles(treeDir, "*", SearchOption.AllDirectories).Count() : 0,
            archive.Fail,
            archive.FirstMissing(treeDir));
    }

    private static int CountMissing(string mpqPath, string treeDir)
    {
        var parser = new Archive(mpqPath);
        return parser.Missing(treeDir);
    }

    private static string FirstMissing(string mpqPath, string treeDir)
    {
        var parser = new Archive(mpqPath);
        return parser.FirstMissing(treeDir);
    }

    public static bool IsComplete(string mpqPath, string treeDir)
    {
        try
        {
            if (!File.Exists(mpqPath))
                return false;
            return Check(mpqPath, treeDir);
        }
        catch
        {
            return false; // unreadable counts as broken; the caller heals it
        }
    }

    private static bool Check(string mpqPath, string treeDir) =>
        new Archive(mpqPath).IsComplete(treeDir);

    private sealed class Archive
    {
        private readonly long _total;
        private readonly Dictionary<(uint, uint), int> _files;
        private readonly (uint Off, uint Size, uint FileSize, uint Flags)[] _blocks;
        private readonly bool _valid;
        public string Fail = "";

        public Archive(string mpqPath)
        {
            try
            {
                using var f = File.OpenRead(mpqPath);
                _total = f.Length;
                var header = new byte[32];
                if (f.Read(header, 0, 32) != 32) { Fail = "short header"; _valid = false; return; }
                if (header[0] != (byte)'M' || header[1] != (byte)'P' || header[2] != (byte)'Q' || header[3] != 0x1A) { Fail = "bad magic"; _valid = false; return; }

                var fmt = ReadU16(header, 12);
                var htOff = ReadU32(header, 16);
                var btOff = ReadU32(header, 20);
                var htCount = (int)ReadU32(header, 24);
                var btCount = (int)ReadU32(header, 28);
                if (fmt != 0 || htOff + (long)htCount * 16 > _total || btOff + (long)btCount * 16 > _total) { Fail = $"tables beyond EOF (fmt={fmt} ht={htOff + (long)htCount * 16} bt={btOff + (long)btCount * 16} total={_total})"; _valid = false; return; }

                f.Seek(htOff, SeekOrigin.Begin);
                var ht = new byte[htCount * 16];
                if (f.Read(ht, 0, ht.Length) != ht.Length) { Fail = "short hash table read"; _valid = false; return; }
                f.Seek(btOff, SeekOrigin.Begin);
                var bt = new byte[btCount * 16];
                if (f.Read(bt, 0, bt.Length) != bt.Length) { Fail = "short block table read"; _valid = false; return; }

                var hashDec = new Cipher(HashString("(hash table)", 3));
                var files = new Dictionary<(uint, uint), int>();
                for (var i = 0; i < htCount; i++)
                {
                    var baseIdx = i * 16;
                    var hashA = hashDec.Decrypt(ReadU32(ht, baseIdx));
                    var hashB = hashDec.Decrypt(ReadU32(ht, baseIdx + 4));
                    _ = hashDec.Decrypt(ReadU32(ht, baseIdx + 8)); // locale/platform word advances the cipher
                    var block = (int)hashDec.Decrypt(ReadU32(ht, baseIdx + 12));
                    if (hashA == EmptySlot)
                        continue;
                    if (block >= btCount) { Fail = $"hash entry {i} block {block} >= {btCount}"; _valid = false; return; }
                    files[(hashA, hashB)] = block;
                }

                var blockDec = new Cipher(HashString("(block table)", 3));
                var blocks = new (uint Off, uint Size, uint FileSize, uint Flags)[btCount];
                for (var i = 0; i < btCount; i++)
                {
                    var baseIdx = i * 16;
                    var off = blockDec.Decrypt(ReadU32(bt, baseIdx));
                    var size = blockDec.Decrypt(ReadU32(bt, baseIdx + 4));
                    var fileSize = blockDec.Decrypt(ReadU32(bt, baseIdx + 8));
                    var flags = blockDec.Decrypt(ReadU32(bt, baseIdx + 12));
                    blocks[i] = (off, size, fileSize, flags);
                }

                foreach (var (off, size, _, flags) in blocks)
                {
                    if ((flags & FlagDeleted) != 0 || (flags & FlagExists) == 0)
                        continue;
                    if ((long)off + size > _total) { Fail = "a block runs past EOF"; _valid = false; return; }
                }

                _files = files;
                _blocks = blocks;
                _valid = true;
            }
            catch (Exception ex)
            {
                Fail = ex.ToString();
                _valid = false;
            }
        }

        public bool IsComplete(string treeDir)
        {
            if (!_valid)
                return false;
            if (!Directory.Exists(treeDir))
                return true;
            foreach (var file in Directory.EnumerateFiles(treeDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(treeDir, file).Replace(Path.DirectorySeparatorChar, '\\');
                var key = (HashString(rel, 1), HashString(rel, 2));
                if (!_files.TryGetValue(key, out var blockIdx))
                    return false;
                var (_, _, fileSize, flags) = _blocks[blockIdx];
                if ((flags & FlagExists) == 0 || fileSize <= 0)
                    return false;
            }
            return true;
        }

        public int Missing(string treeDir)
        {
            if (!_valid || !Directory.Exists(treeDir))
                return -1;
            var missing = 0;
            foreach (var file in Directory.EnumerateFiles(treeDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(treeDir, file).Replace(Path.DirectorySeparatorChar, '\\');
                var key = (HashString(rel, 1), HashString(rel, 2));
                if (!_files.TryGetValue(key, out var blockIdx))
                {
                    missing++;
                    continue;
                }
                var (_, _, fileSize, flags) = _blocks[blockIdx];
                if ((flags & FlagExists) == 0 || fileSize <= 0)
                    missing++;
            }
            return missing;
        }

        public string FirstMissing(string treeDir)
        {
            if (!_valid || !Directory.Exists(treeDir))
                return "archive invalid";
            foreach (var file in Directory.EnumerateFiles(treeDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(treeDir, file).Replace(Path.DirectorySeparatorChar, '\\');
                var key = (HashString(rel, 1), HashString(rel, 2));
                if (!_files.TryGetValue(key, out var blockIdx))
                    return rel;
                var (_, _, fileSize, flags) = _blocks[blockIdx];
                if ((flags & FlagExists) == 0 || fileSize <= 0)
                    return rel + " (bad record)";
            }
            return "(none)";
        }
    }

    private static uint[] BuildCryptTable()
    {
        var table = new uint[0x500];
        var seed = 0x00100001u;
        for (var i1 = 0; i1 < 0x100; i1++)
        {
            var i2 = i1;
            for (var j = 0; j < 5; j++)
            {
                seed = (uint)(((ulong)seed * 125 + 3) % 0x2AAAAB);
                var t1 = (seed & 0xFFFF) << 16;
                seed = (uint)(((ulong)seed * 125 + 3) % 0x2AAAAB);
                table[i2] = t1 | (seed & 0xFFFF);
                i2 += 0x100;
            }
        }
        return table;
    }

    private static uint HashString(string value, int type)
    {
        uint seed1 = 0x7FED7FED, seed2 = 0xEEEEEEEE;
        foreach (var ch in value.ToUpperInvariant())
        {
            var c = (byte)ch;
            seed1 = Crypt[type * 0x100 + c] ^ (seed1 + seed2);
            seed2 = (uint)(c + seed2 + (seed2 << 5) + seed1 + 3);
        }
        return seed1;
    }

    private struct Cipher
    {
        private uint _key;
        private uint _seed;

        public Cipher(uint key)
        {
            _key = key;
            _seed = 0xEEEEEEEE;
        }

        public uint Decrypt(uint cipher)
        {
            _seed = _seed + Crypt[0x400 + (_key & 0xFF)];
            var plain = cipher ^ (_key + _seed);
            var old = _key;
            _key = ((~old << 0x15) + 0x11111111) | (old >> 0x0B);
            _seed = plain + _seed + (_seed << 5) + 3;
            return plain;
        }
    }

    private static ushort ReadU16(byte[] buf, int offset) =>
        (ushort)(buf[offset] | (buf[offset + 1] << 8));

    private static uint ReadU32(byte[] buf, int offset) =>
        (uint)(buf[offset] | (buf[offset + 1] << 8) | (buf[offset + 2] << 16) | (buf[offset + 3] << 24));
}
