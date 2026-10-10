// Map rendering - the session-free half of Map.
// Everything here is a function of the map bytes alone, so it unit tests headlessly.
//
// The BMP writer, the PNG writer and the resize are hand-rolled on purpose.
// The obvious alternative, Magick.NET, is 321 MB on disk with per-RID native payloads, too much to ship inside a plugin folder.

using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace DMCBK.OfficialPlugins.Map;

/// <summary>A straight 8-bit-per-channel colour.</summary>
/// <param name="R">The red channel.</param>
/// <param name="G">The green channel.</param>
/// <param name="B">The blue channel.</param>
public readonly record struct MapColor(byte R, byte G, byte B);

/// <summary>
/// A snapshot of one in-game map item, for rendering.
/// The copy is taken on the session loop so the command thread can render without reading session state off-loop; the lock exists for that hand-off.
/// </summary>
public sealed class CachedMap
{
    private readonly object _gate = new();

    /// <summary>The map item id.</summary>
    public int MapId { get; init; }

    /// <summary>The map scale (0 = 1 block per pixel, up to 4).</summary>
    public byte Scale { get; set; }

    /// <summary>Whether the map is locked (cartography table).</summary>
    public bool Locked { get; set; }

    /// <summary>The decoration count last reported by the server.</summary>
    public int IconCount { get; set; }

    /// <summary>The pixel buffer width.</summary>
    public byte Width { get; private set; }

    /// <summary>The pixel buffer height.</summary>
    public byte Height { get; private set; }

    /// <summary>The map-color bytes, row-major, <see cref="Width"/> * <see cref="Height"/> long.</summary>
    public byte[] Colors { get; private set; } = [];

    /// <summary>When the map was last updated, in local time.</summary>
    public DateTime LastUpdated { get; set; }

    /// <summary>
    /// Replaces the snapshot with the core's merged grid.
    /// This takes the whole buffer, not a patch: the session's map state already holds every update ever applied to that map.
    /// </summary>
    /// <param name="colors">The merged, row-major colour grid.</param>
    /// <param name="width">The grid width (128 for a Minecraft map).</param>
    /// <param name="height">The grid height (128 for a Minecraft map).</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A dimension is outside 1-255, or does not match the length of <paramref name="colors"/>.
    /// </exception>
    public void CopyFrom(ReadOnlySpan<byte> colors, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, byte.MaxValue);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(height, byte.MaxValue);
        if (colors.Length != width * height)
            throw new ArgumentOutOfRangeException(nameof(colors), "Colour span length must equal width * height.");

        lock (_gate)
        {
            if (Colors.Length != colors.Length)
                Colors = new byte[colors.Length];

            colors.CopyTo(Colors);
            Width = (byte)width;
            Height = (byte)height;
        }
    }

    /// <summary>Takes a stable copy of the pixel buffer for rendering off the packet thread.</summary>
    public byte[] SnapshotColors()
    {
        lock (_gate)
            return (byte[])Colors.Clone();
    }
}

/// <summary>
/// The map-item colour palette, the base colours of vanilla MapColor.java. The table is copied out here because a plugin cannot reach the client's own embedded resources.
/// </summary>
public static class MapColors
{
    private static readonly MapColor[] BasePalette =
    [
        new(0x00, 0x00, 0x00), new(0x7F, 0xB2, 0x38), new(0xF7, 0xE9, 0xA3), new(0xC7, 0xC7, 0xC7),
        new(0xFF, 0x00, 0x00), new(0xA0, 0xA0, 0xFF), new(0xA7, 0xA7, 0xA7), new(0x00, 0x7C, 0x00),
        new(0xFF, 0xFF, 0xFF), new(0xA4, 0xA8, 0xB8), new(0x97, 0x6D, 0x4D), new(0x70, 0x70, 0x70),
        new(0x40, 0x40, 0xFF), new(0x8F, 0x77, 0x48), new(0xFF, 0xFC, 0xF5), new(0xD8, 0x7F, 0x33),
        new(0xB2, 0x4C, 0xD8), new(0x66, 0x99, 0xD8), new(0xE5, 0xE5, 0x33), new(0x7F, 0xCC, 0x19),
        new(0xF2, 0x7F, 0xA5), new(0x4C, 0x4C, 0x4C), new(0x99, 0x99, 0x99), new(0x4C, 0x7F, 0x99),
        new(0x7F, 0x3F, 0xB2), new(0x33, 0x4C, 0xB2), new(0x66, 0x4C, 0x33), new(0x66, 0x7F, 0x33),
        new(0x99, 0x33, 0x33), new(0x19, 0x19, 0x19), new(0xFA, 0xEE, 0x4D), new(0x5C, 0xDB, 0xD5),
        new(0x4A, 0x80, 0xFF), new(0x00, 0xD9, 0x3A), new(0x81, 0x56, 0x31), new(0x70, 0x02, 0x00),
        new(0xD1, 0xB1, 0xA1), new(0x9F, 0x52, 0x24), new(0x95, 0x57, 0x6C), new(0x70, 0x6C, 0x8A),
        new(0xBA, 0x85, 0x24), new(0x67, 0x75, 0x35), new(0xA0, 0x4D, 0x4E), new(0x39, 0x29, 0x23),
        new(0x87, 0x6B, 0x62), new(0x57, 0x5C, 0x5C), new(0x7A, 0x49, 0x58), new(0x4C, 0x3E, 0x5C),
        new(0x4C, 0x32, 0x23), new(0x4C, 0x52, 0x2A), new(0x8E, 0x3C, 0x2E), new(0x25, 0x16, 0x10),
        new(0xBD, 0x30, 0x31), new(0x94, 0x3F, 0x61), new(0x5C, 0x19, 0x1D), new(0x16, 0x7E, 0x86),
        new(0x3A, 0x8E, 0x8C), new(0x56, 0x2C, 0x3E), new(0x14, 0xB4, 0x85), new(0x64, 0x64, 0x64),
        new(0xD8, 0xAF, 0x93), new(0x7F, 0xA7, 0x96),
    ];

    /// <summary>The vanilla shade multipliers, indexed by the low two bits of the map-color byte.</summary>
    private static readonly byte[] ShadeMultipliers = [180, 220, 255, 135];

    /// <summary>The colour shown for a palette id this build does not know.</summary>
    private static readonly MapColor Unknown = new(248, 0, 248);

    /// <summary>The number of known base colours.</summary>
    public static int BaseColorCount => BasePalette.Length;

    /// <summary>Expands one map-color byte into RGB: base id in the high 6 bits, shade in the low 2.</summary>
    public static MapColor ColorByteToRgb(byte receivedColorId)
    {
        int baseColorId = receivedColorId >> 2;
        if (baseColorId >= BasePalette.Length)
            return Unknown;

        MapColor rgb = BasePalette[baseColorId];
        byte multiplier = ShadeMultipliers[receivedColorId & 3];
        return new MapColor(
            (byte)(rgb.R * multiplier / 255),
            (byte)(rgb.G * multiplier / 255),
            (byte)(rgb.B * multiplier / 255));
    }
}

/// <summary>A raw 24-bit RGB image buffer, the intermediate every writer here consumes.</summary>
/// <param name="Width">The image width in pixels.</param>
/// <param name="Height">The image height in pixels.</param>
/// <param name="Pixels">Row-major RGB triples, 3 bytes per pixel, top row first.</param>
public sealed record RgbImage(int Width, int Height, byte[] Pixels)
{
    /// <summary>Builds an image from a map-color buffer through the map palette.</summary>
    public static RgbImage FromMapColors(byte[] colors, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(colors);
        var pixels = new byte[width * height * 3];
        for (int index = 0; index < width * height; index++)
        {
            MapColor color = MapColors.ColorByteToRgb(index < colors.Length ? colors[index] : (byte)0);
            pixels[index * 3] = color.R;
            pixels[(index * 3) + 1] = color.G;
            pixels[(index * 3) + 2] = color.B;
        }

        return new RgbImage(width, height, pixels);
    }

    /// <summary>Reads one pixel.</summary>
    public MapColor GetPixel(int x, int y)
    {
        int offset = ((y * Width) + x) * 3;
        return new MapColor(Pixels[offset], Pixels[offset + 1], Pixels[offset + 2]);
    }

    /// <summary>
    /// Nearest-neighbour resize to an exact size, aspect ratio ignored.
    /// Nearest-neighbour is the right filter here: a smoothing one blurs the 1-block-per-pixel grid the map is made of.
    /// </summary>
    public RgbImage ResizeNearest(int targetWidth, int targetHeight)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(targetWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(targetHeight, 1);
        var pixels = new byte[targetWidth * targetHeight * 3];
        for (int y = 0; y < targetHeight; y++)
        {
            int sourceY = Math.Min(Height - 1, (int)((long)y * Height / targetHeight));
            for (int x = 0; x < targetWidth; x++)
            {
                int sourceX = Math.Min(Width - 1, (int)((long)x * Width / targetWidth));
                int source = ((sourceY * Width) + sourceX) * 3;
                int target = ((y * targetWidth) + x) * 3;
                pixels[target] = Pixels[source];
                pixels[target + 1] = Pixels[source + 1];
                pixels[target + 2] = Pixels[source + 2];
            }
        }

        return new RgbImage(targetWidth, targetHeight, pixels);
    }
}

/// <summary>A 24-bit uncompressed BMP writer.</summary>
public static class BmpEncoder
{
    /// <summary>The fixed size of the BITMAPFILEHEADER plus BITMAPINFOHEADER.</summary>
    public const int HeaderSize = 54;

    /// <summary>Encodes the image as a 24-bit BI_RGB bitmap.</summary>
    public static byte[] Encode(RgbImage image)
    {
        ArgumentNullException.ThrowIfNull(image);

        // BMP rows are padded to a multiple of 4 bytes.
        // Skip the padding and only widths that already land on 4 produce a readable file.
        int rowBytes = image.Width * 3;
        int padding = (4 - (rowBytes % 4)) % 4;
        int pixelBytes = (rowBytes + padding) * image.Height;

        var output = new byte[HeaderSize + pixelBytes];
        Span<byte> span = output;

        BinaryPrimitives.WriteUInt16LittleEndian(span, 0x4D42);                    // bfType "BM"
        BinaryPrimitives.WriteUInt32LittleEndian(span[2..], (uint)output.Length);  // bfSize
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..], 0);                    // bfReserved1
        BinaryPrimitives.WriteUInt16LittleEndian(span[8..], 0);                    // bfReserved2
        BinaryPrimitives.WriteUInt32LittleEndian(span[10..], HeaderSize);          // bfOffBits
        BinaryPrimitives.WriteUInt32LittleEndian(span[14..], 40);                  // biSize
        BinaryPrimitives.WriteUInt32LittleEndian(span[18..], (uint)image.Width);   // biWidth
        BinaryPrimitives.WriteUInt32LittleEndian(span[22..], (uint)image.Height);  // biHeight
        BinaryPrimitives.WriteUInt16LittleEndian(span[26..], 1);                   // biPlanes
        BinaryPrimitives.WriteUInt16LittleEndian(span[28..], 24);                  // biBitCount
        BinaryPrimitives.WriteUInt32LittleEndian(span[30..], 0);                   // biCompression BI_RGB

        int offset = HeaderSize;
        for (int y = image.Height - 1; y >= 0; y--)
        {
            for (int x = 0; x < image.Width; x++)
            {
                MapColor color = image.GetPixel(x, y);
                output[offset++] = color.B;
                output[offset++] = color.G;
                output[offset++] = color.R;
            }

            offset += padding;
        }

        return output;
    }
}

/// <summary>
/// A minimal PNG writer: 8-bit truecolour, no interlacing, filter type 0.
/// It exists because the chat bridges reject BMP attachments.
/// </summary>
public static class PngEncoder
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Encodes the image as an RGB PNG.</summary>
    public static byte[] Encode(RgbImage image)
    {
        ArgumentNullException.ThrowIfNull(image);

        var raw = new byte[(image.Width * 3 + 1) * image.Height];
        int offset = 0;
        for (int y = 0; y < image.Height; y++)
        {
            raw[offset++] = 0; // filter type 0 (None)
            Array.Copy(image.Pixels, y * image.Width * 3, raw, offset, image.Width * 3);
            offset += image.Width * 3;
        }

        using var compressed = new MemoryStream();
        using (var deflate = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(raw, 0, raw.Length);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, image.Width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), image.Height);
        header[8] = 8;  // bit depth
        header[9] = 2;  // colour type: truecolour
        header[10] = 0; // deflate
        header[11] = 0; // adaptive filtering
        header[12] = 0; // no interlace

        using var output = new MemoryStream();
        output.Write(Signature);
        WriteChunk(output, "IHDR"u8, header);
        WriteChunk(output, "IDAT"u8, compressed.ToArray());
        WriteChunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        stream.Write(type);
        stream.Write(data);

        uint crc = Crc32.Compute(type, data);
        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, crc);
        stream.Write(checksum);
    }

    /// <summary>The PNG chunk CRC (IEEE 802.3, reflected, as specified by the PNG spec).</summary>
    private static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        internal static uint Compute(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
        {
            uint crc = 0xFFFFFFFFu;
            foreach (byte value in first)
                crc = Table[(crc ^ value) & 0xFF] ^ (crc >> 8);

            foreach (byte value in second)
                crc = Table[(crc ^ value) & 0xFF] ^ (crc >> 8);

            return crc ^ 0xFFFFFFFFu;
        }

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;

                table[n] = c;
            }

            return table;
        }
    }
}
