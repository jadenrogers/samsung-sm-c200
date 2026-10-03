using System.Buffers.Binary;
using System.Text;

namespace Gear360.Core.Stitching;

/// <summary>
/// Marks an MP4 as a 360° equirectangular video by adding Google's Spherical Video V1 metadata: a <c>uuid</c> box
/// holding an RDF/XMP document, placed in the video track right after <c>tkhd</c>. This is what Google's
/// spatial-media tool writes, and what YouTube, VLC and Facebook look for.
/// </summary>
/// <remarks>
/// See https://github.com/google/spatial-media/blob/master/docs/spherical-video-rfc.md. Inserting the box makes
/// <c>moov</c> larger, so when <c>moov</c> sits before the media data (a "faststart" file) every chunk offset in
/// <c>stco</c>/<c>co64</c> is shifted by the growth; 32-bit tables that would overflow are promoted to <c>co64</c>.
/// </remarks>
public static class SphericalMetadataInjector
{
    /// <summary>The value written to <c>GSpherical:StitchingSoftware</c> by default.</summary>
    public const string DefaultStitchingSoftware = "Gear360Extractor";

    // Largest moov this class will load into memory. Real moov boxes are a few MB even for hour-long clips.
    private const long MaxMoovSize = 512L * 1024 * 1024;

    private const int CopyBufferSize = 1024 * 1024;

    /// <summary>The 16-byte user type of the spherical <c>uuid</c> box: ffcc8263-f855-4a93-8814-587a02521fdd.</summary>
    internal static ReadOnlySpan<byte> SphericalUuid =>
        [0xff, 0xcc, 0x82, 0x63, 0xf8, 0x55, 0x4a, 0x93, 0x88, 0x14, 0x58, 0x7a, 0x02, 0x52, 0x1f, 0xdd];

    /// <summary>Adds equirectangular spherical metadata to the first video track of an MP4/MOV file, in place.</summary>
    /// <returns>True if the metadata was added; false if the file already had it.</returns>
    /// <exception cref="InvalidDataException">The file is not a valid MP4 or has no video track.</exception>
    public static bool Inject(string path, string stitchingSoftware = DefaultStitchingSoftware)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(stitchingSoftware);

        Mp4Box moov;
        TopLevelBox moovLocation;
        bool moovIsLast;
        long fileLength;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            fileLength = stream.Length;
            var topLevel = ReadTopLevel(stream);
            var index = FindMoov(topLevel);
            moovLocation = topLevel[index];
            moovIsLast = index == topLevel.Count - 1;
            moov = LoadMoov(stream, moovLocation);
        }

        var track = FindVideoTrack(moov) ?? throw new InvalidDataException("The file has no video track.");
        if (track.Children!.Any(IsSphericalBox))
        {
            return false;
        }

        var tkhdIndex = track.Children!.FindIndex(b => b.Type == "tkhd");
        track.Children!.Insert(tkhdIndex + 1, CreateSphericalBox(stitchingSoftware));

        var oldMoovEnd = moovLocation.Offset + moovLocation.Size;
        var tables = new List<Mp4Box>();
        CollectChunkOffsetTables(moov, tables);

        // Data stored after moov moves by however much moov grows. Promoting a 32-bit table to 64 bits grows moov
        // again, so repeat until no shifted offset overflows.
        long delta;
        while (true)
        {
            delta = moov.TotalSize - moovLocation.Size;
            var overflowing = tables.FirstOrDefault(t => !t.Is64BitOffsets && t.ChunkOffsets!.Any(o => o >= oldMoovEnd && o + delta > uint.MaxValue));
            if (overflowing is null)
            {
                break;
            }

            overflowing.Is64BitOffsets = true;
        }

        foreach (var table in tables)
        {
            var offsets = table.ChunkOffsets!;
            for (var i = 0; i < offsets.Length; i++)
            {
                if (offsets[i] >= oldMoovEnd)
                {
                    offsets[i] += delta;
                }
            }
        }

        var newMoov = Serialize(moov);
        if (moovIsLast)
        {
            // Nothing follows moov, so it can simply be rewritten where it is.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            stream.Position = moovLocation.Offset;
            stream.Write(newMoov);
            stream.SetLength(moovLocation.Offset + newMoov.Length);
            stream.Flush(flushToDisk: true);
        }
        else
        {
            RewriteWithNewMoov(path, moovLocation, newMoov, fileLength);
        }

        return true;
    }

    /// <summary>True when any track of the file carries a spherical <c>uuid</c> box.</summary>
    /// <exception cref="InvalidDataException">The file is not a valid MP4.</exception>
    public static bool HasSphericalMetadata(string path) => ReadSphericalXml(path) is not null;

    /// <summary>Returns the spherical XMP document of the first track that has one, or null.</summary>
    /// <exception cref="InvalidDataException">The file is not a valid MP4.</exception>
    public static string? ReadSphericalXml(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var topLevel = ReadTopLevel(stream);
        var moov = LoadMoov(stream, topLevel[FindMoov(topLevel)]);
        foreach (var track in moov.Children!.Where(b => b.Type == "trak"))
        {
            var box = track.Children!.FirstOrDefault(IsSphericalBox);
            if (box is not null)
            {
                return Encoding.UTF8.GetString(box.Payload!, SphericalUuid.Length, box.Payload!.Length - SphericalUuid.Length);
            }
        }

        return null;
    }

    /// <summary>The XMP document written into the <c>uuid</c> box.</summary>
    internal static string BuildXml(string stitchingSoftware) =>
        "<?xml version=\"1.0\"?>" +
        "<rdf:SphericalVideo xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\" " +
        "xmlns:GSpherical=\"http://ns.google.com/videos/1.0/spherical/\">" +
        "<GSpherical:Spherical>true</GSpherical:Spherical>" +
        "<GSpherical:Stitched>true</GSpherical:Stitched>" +
        $"<GSpherical:StitchingSoftware>{System.Security.SecurityElement.Escape(stitchingSoftware)}</GSpherical:StitchingSoftware>" +
        "<GSpherical:ProjectionType>equirectangular</GSpherical:ProjectionType>" +
        "</rdf:SphericalVideo>";

    private static Mp4Box CreateSphericalBox(string stitchingSoftware)
    {
        var xml = Encoding.UTF8.GetBytes(BuildXml(stitchingSoftware));
        var payload = new byte[SphericalUuid.Length + xml.Length];
        SphericalUuid.CopyTo(payload);
        xml.CopyTo(payload, SphericalUuid.Length);
        return new Mp4Box("uuid") { Payload = payload };
    }

    private static bool IsSphericalBox(Mp4Box box) =>
        box.Type == "uuid" && box.Payload is { Length: >= 16 } payload && payload.AsSpan(0, 16).SequenceEqual(SphericalUuid);

    private static Mp4Box? FindVideoTrack(Mp4Box moov)
    {
        foreach (var track in moov.Children!.Where(b => b.Type == "trak"))
        {
            var handler = track.Child("mdia")?.Child("hdlr")?.Payload;

            // hdlr: version/flags (4), pre_defined (4), handler_type (4).
            if (handler is { Length: >= 12 } && handler.AsSpan(8, 4).SequenceEqual("vide"u8))
            {
                return track;
            }
        }

        return null;
    }

    private static void CollectChunkOffsetTables(Mp4Box box, List<Mp4Box> tables)
    {
        if (box.ChunkOffsets is not null)
        {
            tables.Add(box);
        }

        if (box.Children is { } children)
        {
            foreach (var child in children)
            {
                CollectChunkOffsetTables(child, tables);
            }
        }
    }

    private static void RewriteWithNewMoov(string path, TopLevelBox moov, byte[] newMoov, long fileLength)
    {
        var temp = path + ".spherical.tmp";
        try
        {
            using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize))
            using (var target = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize))
            {
                CopyRange(source, target, 0, moov.Offset);
                target.Write(newMoov);
                CopyRange(source, target, moov.Offset + moov.Size, fileLength - (moov.Offset + moov.Size));
                target.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
            }

            throw;
        }
    }

    private static void CopyRange(Stream source, Stream target, long offset, long count)
    {
        source.Position = offset;
        var buffer = new byte[(int)Math.Min(CopyBufferSize, Math.Max(count, 1))];
        while (count > 0)
        {
            var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (read == 0)
            {
                throw new InvalidDataException("The file ended unexpectedly.");
            }

            target.Write(buffer, 0, read);
            count -= read;
        }
    }

    // ---- Parsing ----

    private readonly record struct TopLevelBox(string Type, long Offset, long Size);

    private static List<TopLevelBox> ReadTopLevel(Stream stream)
    {
        var boxes = new List<TopLevelBox>();
        var header = new byte[16];
        long position = 0;
        var length = stream.Length;
        while (position < length)
        {
            if (length - position < 8)
            {
                // Some writers leave a few padding bytes at the end; ignore them.
                break;
            }

            stream.Position = position;
            stream.ReadExactly(header, 0, 8);
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = Encoding.Latin1.GetString(header, 4, 4);
            if (size == 1)
            {
                if (length - position < 16)
                {
                    throw new InvalidDataException($"Truncated '{type}' box header at offset {position}.");
                }

                stream.ReadExactly(header, 8, 8);
                size = checked((long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8)));
            }
            else if (size == 0)
            {
                size = length - position;
            }

            if (size < 8 || size > length - position)
            {
                throw new InvalidDataException($"Invalid size {size} for '{type}' box at offset {position}.");
            }

            boxes.Add(new TopLevelBox(type, position, size));
            position += size;
        }

        if (boxes.Count == 0 || boxes[0].Type is not ("ftyp" or "moov" or "mdat" or "free" or "skip" or "wide" or "uuid"))
        {
            throw new InvalidDataException("Not an MP4/MOV file.");
        }

        return boxes;
    }

    private static int FindMoov(List<TopLevelBox> boxes)
    {
        var index = boxes.FindIndex(b => b.Type == "moov");
        if (index < 0)
        {
            throw new InvalidDataException("The file has no 'moov' box (is it complete?).");
        }

        if (boxes.FindLastIndex(b => b.Type == "moov") != index)
        {
            throw new InvalidDataException("The file has more than one 'moov' box.");
        }

        return index;
    }

    private static Mp4Box LoadMoov(Stream stream, TopLevelBox location)
    {
        if (location.Size > MaxMoovSize)
        {
            throw new InvalidDataException($"The 'moov' box is unexpectedly large ({location.Size} bytes).");
        }

        var bytes = new byte[location.Size];
        stream.Position = location.Offset;
        stream.ReadExactly(bytes);
        var boxes = ParseBoxes(bytes);
        if (boxes.Count != 1 || boxes[0].Type != "moov")
        {
            throw new InvalidDataException("Could not parse the 'moov' box.");
        }

        return boxes[0];
    }

    // Boxes whose payload is nothing but child boxes, on the path to the tracks and their chunk offset tables.
    private static bool IsContainer(string type) => type is "moov" or "trak" or "mdia" or "minf" or "stbl" or "edts" or "dinf";

    private static List<Mp4Box> ParseBoxes(ReadOnlySpan<byte> data)
    {
        var boxes = new List<Mp4Box>();
        var position = 0;
        while (position < data.Length)
        {
            var remaining = data.Length - position;
            if (remaining < 8)
            {
                throw new InvalidDataException("Truncated box header.");
            }

            long size = BinaryPrimitives.ReadUInt32BigEndian(data[position..]);
            var type = Encoding.Latin1.GetString(data.Slice(position + 4, 4));
            var headerSize = 8;
            if (size == 1)
            {
                if (remaining < 16)
                {
                    throw new InvalidDataException($"Truncated '{type}' box header.");
                }

                size = checked((long)BinaryPrimitives.ReadUInt64BigEndian(data[(position + 8)..]));
                headerSize = 16;
            }
            else if (size == 0)
            {
                size = remaining;
            }

            if (size < headerSize || size > remaining)
            {
                throw new InvalidDataException($"Invalid size {size} for '{type}' box.");
            }

            var payload = data.Slice(position + headerSize, (int)size - headerSize);
            boxes.Add(ParseBox(type, payload));
            position += (int)size;
        }

        return boxes;
    }

    private static Mp4Box ParseBox(string type, ReadOnlySpan<byte> payload)
    {
        if (IsContainer(type))
        {
            return new Mp4Box(type) { Children = ParseBoxes(payload) };
        }

        if (type is "stco" or "co64")
        {
            var is64 = type == "co64";
            if (payload.Length < 8)
            {
                throw new InvalidDataException($"Truncated '{type}' box.");
            }

            var count = BinaryPrimitives.ReadUInt32BigEndian(payload[4..]);
            var entrySize = is64 ? 8 : 4;
            if (count > (uint)((payload.Length - 8) / entrySize))
            {
                throw new InvalidDataException($"The '{type}' box lists more entries than it holds.");
            }

            var offsets = new long[count];
            for (var i = 0; i < offsets.Length; i++)
            {
                var entry = payload[(8 + (i * entrySize))..];
                offsets[i] = is64 ? checked((long)BinaryPrimitives.ReadUInt64BigEndian(entry)) : BinaryPrimitives.ReadUInt32BigEndian(entry);
            }

            return new Mp4Box(type) { VersionAndFlags = BinaryPrimitives.ReadUInt32BigEndian(payload), ChunkOffsets = offsets, Is64BitOffsets = is64 };
        }

        return new Mp4Box(type) { Payload = payload.ToArray() };
    }

    // ---- Writing ----

    private static byte[] Serialize(Mp4Box box)
    {
        var buffer = new byte[box.TotalSize];
        var written = Write(box, buffer);
        if (written != buffer.Length)
        {
            throw new InvalidOperationException("MP4 box size mismatch.");
        }

        return buffer;
    }

    private static int Write(Mp4Box box, Span<byte> destination)
    {
        var total = box.TotalSize;
        var headerSize = box.HeaderSize;
        var type = box.Type;
        if (headerSize == 16)
        {
            BinaryPrimitives.WriteUInt32BigEndian(destination, 1);
            BinaryPrimitives.WriteUInt64BigEndian(destination[8..], (ulong)total);
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(destination, (uint)total);
        }

        Encoding.Latin1.GetBytes(type, destination.Slice(4, 4));
        var position = headerSize;

        if (box.Children is { } children)
        {
            foreach (var child in children)
            {
                position += Write(child, destination[position..]);
            }
        }
        else if (box.ChunkOffsets is { } offsets)
        {
            BinaryPrimitives.WriteUInt32BigEndian(destination[position..], box.VersionAndFlags);
            BinaryPrimitives.WriteUInt32BigEndian(destination[(position + 4)..], (uint)offsets.Length);
            position += 8;
            foreach (var offset in offsets)
            {
                if (box.Is64BitOffsets)
                {
                    BinaryPrimitives.WriteUInt64BigEndian(destination[position..], (ulong)offset);
                    position += 8;
                }
                else
                {
                    BinaryPrimitives.WriteUInt32BigEndian(destination[position..], checked((uint)offset));
                    position += 4;
                }
            }
        }
        else
        {
            box.Payload!.CopyTo(destination[position..]);
            position += box.Payload!.Length;
        }

        return position;
    }

    /// <summary>An MP4 box held in memory: a container with children, a chunk offset table, or raw payload.</summary>
    private sealed class Mp4Box(string type)
    {
        private readonly string _type = type;

        /// <summary>The four-character type; chunk offset tables report stco or co64 from their current width.</summary>
        public string Type => ChunkOffsets is null ? _type : Is64BitOffsets ? "co64" : "stco";

        public List<Mp4Box>? Children { get; init; }

        public byte[]? Payload { get; init; }

        public long[]? ChunkOffsets { get; init; }

        public bool Is64BitOffsets { get; set; }

        public uint VersionAndFlags { get; init; }

        public long PayloadSize =>
            Children is { } children ? children.Sum(c => c.TotalSize)
            : ChunkOffsets is { } offsets ? 8 + ((long)offsets.Length * (Is64BitOffsets ? 8 : 4))
            : Payload!.Length;

        public int HeaderSize => PayloadSize + 8 > uint.MaxValue ? 16 : 8;

        public long TotalSize => PayloadSize + HeaderSize;

        public Mp4Box? Child(string childType) => Children?.FirstOrDefault(c => c.Type == childType);
    }
}
