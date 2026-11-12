using System.Text;

// Reproducible multi-disk ZIP, not a single ZIP renamed or sliced into .zNN files.
// Record layout: https://pkware.cachefly.net/webdocs/casestudies/APPNOTE.TXT
// Sections 4.3.7, 4.3.12, 4.3.16 and 8.3. Each stored entry starts on its own disk.
internal static class TraditionalZipFixture
{
    internal static readonly Dictionary<string, string> Contents = new()
    {
        ["first.txt"] = "first disk payload",
        ["目录/第二.txt"] = "第二卷中的真实内容",
    };

    internal static (string First, string Middle, string Last) Create(string directory, string name)
    {
        Directory.CreateDirectory(directory);
        var stem = Path.Combine(directory, name);
        var entries = Contents.Select(p => (Name: Encoding.UTF8.GetBytes(p.Key),
            Data: Encoding.UTF8.GetBytes(p.Value))).ToArray();
        for (var disk = 0; disk < entries.Length; disk++)
        {
            var entry = entries[disk];
            using var writer = new BinaryWriter(File.Create(stem + $".z{disk + 1:00}"));
            if (disk == 0) writer.Write(0x08074b50u);
            writer.Write(0x04034b50u);
            writer.Write((ushort)20);       // version needed
            writer.Write((ushort)0x800);    // UTF-8 names
            writer.Write((ushort)0);        // stored
            writer.Write((ushort)0);        // DOS time
            writer.Write((ushort)0x21);     // 1980-01-01
            writer.Write(Crc32(entry.Data));
            writer.Write((uint)entry.Data.Length);
            writer.Write((uint)entry.Data.Length);
            writer.Write((ushort)entry.Name.Length);
            writer.Write((ushort)0);
            writer.Write(entry.Name);
            writer.Write(entry.Data);
        }

        using (var writer = new BinaryWriter(File.Create(stem + ".zip")))
        {
            for (var disk = 0; disk < entries.Length; disk++)
            {
                var entry = entries[disk];
                writer.Write(0x02014b50u);
                writer.Write((ushort)20);   // version made by
                writer.Write((ushort)20);
                writer.Write((ushort)0x800);
                writer.Write((ushort)0);
                writer.Write((ushort)0);
                writer.Write((ushort)0x21);
                writer.Write(Crc32(entry.Data));
                writer.Write((uint)entry.Data.Length);
                writer.Write((uint)entry.Data.Length);
                writer.Write((ushort)entry.Name.Length);
                writer.Write((ushort)0);    // extra length
                writer.Write((ushort)0);    // comment length
                writer.Write((ushort)disk); // local header disk
                writer.Write((ushort)0);    // internal attributes
                writer.Write(0u);          // external attributes
                writer.Write(disk == 0 ? 4u : 0u); // offset on that disk
                writer.Write(entry.Name);
            }
            var directorySize = (uint)writer.BaseStream.Position;
            writer.Write(0x06054b50u);
            writer.Write((ushort)entries.Length); // final disk index (zero based)
            writer.Write((ushort)entries.Length); // central directory disk
            writer.Write((ushort)entries.Length);
            writer.Write((ushort)entries.Length);
            writer.Write(directorySize);
            writer.Write(0u);                // central directory offset on final disk
            writer.Write((ushort)0);
        }
        return (stem + ".z01", stem + ".z02", stem + ".zip");
    }

    private static uint Crc32(byte[] data)
    {
        var crc = uint.MaxValue;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) == 1 ? 0xedb88320u : 0u);
        }
        return ~crc;
    }
}
