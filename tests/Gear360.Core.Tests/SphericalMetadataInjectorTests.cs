using System.Buffers.Binary;
using System.Text;
using Gear360.Core.Stitching;

namespace Gear360.Core.Tests;

public class SphericalMetadataInjectorTests
{
    // ---- Synthetic files (no ffmpeg needed) ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Faststart_layout_shifts_chunk_offsets_by_the_growth(bool largeMoovHeader)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "a.mp4");
        var (bytes, offsets) = BuildFile(moovFirst: true, largeMoovHeader, trailingFree: false, offsetBase: 0);
        File.WriteAllBytes(path, bytes);

        Assert.True(SphericalMetadataInjector.Inject(path));

        var after = File.ReadAllBytes(path);
        var growth = after.Length - bytes.Length;
        Assert.True(growth > 0);
        var newOffsets = ReadChunkOffsets(after, out var is64);
        Assert.False(is64);
        Assert.Equal(offsets.Select(o => o + growth), newOffsets);

        // Each offset still points at the same sample bytes.
        for (var i = 0; i < offsets.Length; i++)
        {
            Assert.Equal(bytes.AsSpan((int)offsets[i], 4).ToArray(), after.AsSpan((int)newOffsets[i], 4).ToArray());
        }

        Assert.True(SphericalMetadataInjector.HasSphericalMetadata(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Moov_after_mdat_keeps_chunk_offsets(bool trailingFree)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "a.mp4");
        var (bytes, offsets) = BuildFile(moovFirst: false, largeMoovHeader: false, trailingFree, offsetBase: 0);
        File.WriteAllBytes(path, bytes);

        Assert.True(SphericalMetadataInjector.Inject(path));

        var after = File.ReadAllBytes(path);
        Assert.Equal(offsets, ReadChunkOffsets(after, out _));
        Assert.Equal(bytes.AsSpan(0, (int)offsets[^1] + 4).ToArray(), after.AsSpan(0, (int)offsets[^1] + 4).ToArray());
        if (trailingFree)
        {
            Assert.Equal("free"u8.ToArray(), after.AsSpan(after.Length - 8 + 4, 4).ToArray());
        }

        Assert.True(SphericalMetadataInjector.HasSphericalMetadata(path));
    }

    [Fact]
    public void Offsets_that_would_overflow_32_bits_are_promoted_to_co64()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "a.mp4");

        // Offsets are not checked against the file length, so a small file can carry offsets near 4 GiB.
        var (bytes, offsets) = BuildFile(moovFirst: true, largeMoovHeader: false, trailingFree: false, offsetBase: uint.MaxValue - 200L);
        File.WriteAllBytes(path, bytes);

        Assert.True(SphericalMetadataInjector.Inject(path));

        var after = File.ReadAllBytes(path);
        var growth = after.Length - bytes.Length;
        var newOffsets = ReadChunkOffsets(after, out var is64);
        Assert.True(is64);
        Assert.Equal(offsets.Select(o => o + growth), newOffsets);
        Assert.Contains(newOffsets, o => o > uint.MaxValue);
    }

    [Fact]
    public void Injecting_twice_is_a_no_op()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "a.mp4");
        File.WriteAllBytes(path, BuildFile(moovFirst: true, largeMoovHeader: false, trailingFree: false, offsetBase: 0).Bytes);

        Assert.False(SphericalMetadataInjector.HasSphericalMetadata(path));
        Assert.True(SphericalMetadataInjector.Inject(path));
        var once = File.ReadAllBytes(path);
        Assert.False(SphericalMetadataInjector.Inject(path));
        Assert.Equal(once, File.ReadAllBytes(path));
    }

    [Fact]
    public void Writes_the_spherical_v1_xml()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "a.mp4");
        File.WriteAllBytes(path, BuildFile(moovFirst: true, largeMoovHeader: false, trailingFree: false, offsetBase: 0).Bytes);

        SphericalMetadataInjector.Inject(path);

        var xml = SphericalMetadataInjector.ReadSphericalXml(path);
        Assert.NotNull(xml);
        Assert.Contains("<GSpherical:Spherical>true</GSpherical:Spherical>", xml);
        Assert.Contains("<GSpherical:Stitched>true</GSpherical:Stitched>", xml);
        Assert.Contains("<GSpherical:StitchingSoftware>Gear360Extractor</GSpherical:StitchingSoftware>", xml);
        Assert.Contains("<GSpherical:ProjectionType>equirectangular</GSpherical:ProjectionType>", xml);

        // The uuid box sits right after tkhd.
        var bytes = File.ReadAllBytes(path);
        var tkhd = IndexOf(bytes, "tkhd"u8) - 4;
        var tkhdSize = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(tkhd));
        var next = tkhd + (int)tkhdSize;
        Assert.Equal("uuid"u8.ToArray(), bytes.AsSpan(next + 4, 4).ToArray());
        Assert.Equal(Convert.FromHexString("ffcc8263f8554a938814587a02521fdd"), bytes.AsSpan(next + 8, 16).ToArray());
    }

    [Fact]
    public void Rejects_files_that_are_not_mp4()
    {
        using var temp = new TempDirectory();
        var path = temp.CreateFile("not.mp4", 1000);
        Assert.Throws<InvalidDataException>(() => SphericalMetadataInjector.Inject(path));
    }

    [Fact]
    public void Rejects_files_without_a_video_track()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "a.mp4");
        var bytes = BuildFile(moovFirst: true, largeMoovHeader: false, trailingFree: false, offsetBase: 0).Bytes;
        var handler = IndexOf(bytes, "vide"u8);
        "soun"u8.CopyTo(bytes.AsSpan(handler));
        File.WriteAllBytes(path, bytes);

        Assert.Throws<InvalidDataException>(() => SphericalMetadataInjector.Inject(path));
    }

    // ---- Real files made by ffmpeg ----

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Real_mp4_still_plays_identically_after_injection(bool faststart)
    {
        Ffmpeg.Require();
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "clip.mp4");
        Ffmpeg.CreateClip(path, 320, 160, 2, faststart);
        var before = Ffmpeg.Probe(path);
        var checksums = Ffmpeg.DecodeChecksums(path);
        Assert.DoesNotContain("Spherical", Ffmpeg.ProbeVideoStream(path), StringComparison.OrdinalIgnoreCase);

        Assert.True(SphericalMetadataInjector.Inject(path));

        Assert.True(SphericalMetadataInjector.HasSphericalMetadata(path));
        var after = Ffmpeg.Probe(path);
        Assert.Equal(before, after);

        // Every audio and video frame decodes without errors to the same data, so the chunk offsets are right.
        Assert.Equal(checksums, Ffmpeg.DecodeChecksums(path));

        // ffmpeg itself recognises the metadata.
        var stream = Ffmpeg.ProbeVideoStream(path);
        Assert.Contains("Spherical Mapping", stream);
        Assert.Contains("equirectangular", stream);

        Assert.False(SphericalMetadataInjector.Inject(path));
    }

    // ---- Helpers ----

    /// <summary>
    /// Builds ftyp + moov(trak(tkhd, mdia(hdlr 'vide', minf(stbl(stco))))) + mdat, with moov before or after mdat.
    /// The stco entries point at 4-byte markers inside mdat (or at <paramref name="offsetBase"/> onwards when non-zero).
    /// </summary>
    private static (byte[] Bytes, long[] Offsets) BuildFile(bool moovFirst, bool largeMoovHeader, bool trailingFree, long offsetBase)
    {
        var ftyp = Box("ftyp", [.. "isom"u8, 0, 0, 2, 0, .. "isomiso2avc1mp41"u8]);
        var mdatPayload = new byte[64];
        for (var i = 0; i < mdatPayload.Length; i++)
        {
            mdatPayload[i] = (byte)(i * 7);
        }

        var mdat = Box("mdat", mdatPayload);
        int[] markers = [0, 16, 40, 60];

        // The moov size does not depend on the offset values, so build it once to learn the layout.
        var moovSize = BuildMoov(new long[markers.Length], largeMoovHeader).Length;
        var mdatPayloadStart = moovFirst ? ftyp.Length + moovSize + 8 : ftyp.Length + 8;
        var offsets = markers.Select(m => offsetBase != 0 ? offsetBase + m : mdatPayloadStart + m).ToArray();
        var moov = BuildMoov(offsets, largeMoovHeader);

        IEnumerable<byte[]> parts = moovFirst ? [ftyp, moov, mdat] : [ftyp, mdat, moov];
        if (trailingFree)
        {
            parts = parts.Append(Box("free", []));
        }

        return (parts.SelectMany(p => p).ToArray(), offsets);
    }

    private static byte[] BuildMoov(long[] offsets, bool largeHeader)
    {
        var stcoPayload = new byte[8 + (offsets.Length * 4)];
        BinaryPrimitives.WriteUInt32BigEndian(stcoPayload.AsSpan(4), (uint)offsets.Length);
        for (var i = 0; i < offsets.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(stcoPayload.AsSpan(8 + (i * 4)), (uint)offsets[i]);
        }

        byte[] hdlrPayload = [0, 0, 0, 0, 0, 0, 0, 0, .. "vide"u8, .. new byte[12], .. "Video\0"u8];
        var stbl = Box("stbl", Box("stco", stcoPayload));
        var minf = Box("minf", stbl);
        var mdia = Box("mdia", [.. Box("hdlr", hdlrPayload), .. minf]);
        var trak = Box("trak", [.. Box("tkhd", new byte[84]), .. mdia]);
        byte[] mvhd = Box("mvhd", new byte[100]);
        byte[] content = [.. mvhd, .. trak];
        if (!largeHeader)
        {
            return Box("moov", content);
        }

        var box = new byte[16 + content.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, 1);
        "moov"u8.CopyTo(box.AsSpan(4));
        BinaryPrimitives.WriteUInt64BigEndian(box.AsSpan(8), (ulong)box.Length);
        content.CopyTo(box, 16);
        return box;
    }

    private static byte[] Box(string type, byte[] payload)
    {
        var box = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        payload.CopyTo(box, 8);
        return box;
    }

    private static long[] ReadChunkOffsets(byte[] file, out bool is64)
    {
        var co64 = IndexOf(file, "co64"u8);
        is64 = co64 >= 0;
        var position = is64 ? co64 : IndexOf(file, "stco"u8);
        Assert.True(position >= 0, "no chunk offset table");
        var count = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(position + 8));
        var offsets = new long[count];
        for (var i = 0; i < count; i++)
        {
            offsets[i] = is64
                ? (long)BinaryPrimitives.ReadUInt64BigEndian(file.AsSpan(position + 12 + (i * 8)))
                : BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(position + 12 + (i * 4)));
        }

        return offsets;
    }

    private static int IndexOf(byte[] haystack, ReadOnlySpan<byte> needle) => haystack.AsSpan().IndexOf(needle);
}
