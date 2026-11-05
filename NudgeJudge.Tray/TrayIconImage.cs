using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace NudgeJudge.Tray;

/// <summary>
/// The app icon for the tray, read from the embedded <c>nudgejudge.ico</c> without the UI toolkit's image stack:
/// each size's PNG as stored (Windows takes those directly), or decoded to pixels (for the Linux tray protocol).
/// </summary>
internal static class TrayIconImage
{
    /// <summary>Each icon size (square, in pixels) with its PNG bytes, smallest first.</summary>
    public static IReadOnlyList<(int Size, byte[] Png)> Entries { get; } = ReadIco();

    /// <summary>The PNG for the smallest size at least <paramref name="size"/> (or the largest there is).</summary>
    public static (int Size, byte[] Png) Best(int size)
    {
        foreach (var entry in Entries)
            if (entry.Size >= size) return entry;
        return Entries[^1];
    }

    private static List<(int, byte[])> ReadIco()
    {
        using var stream = typeof(TrayIconImage).Assembly.GetManifestResourceStream("nudgejudge.ico")
                           ?? throw new InvalidOperationException("The icon isn't embedded.");
        var ico = new byte[stream.Length];
        stream.ReadExactly(ico);

        var count = BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(4));
        var entries = new List<(int Size, byte[] Png)>();
        for (var i = 0; i < count; i++)
        {
            var dir = ico.AsSpan(6 + 16 * i, 16);
            var size = dir[0] == 0 ? 256 : dir[0];
            var length = BinaryPrimitives.ReadInt32LittleEndian(dir[8..]);
            var offset = BinaryPrimitives.ReadInt32LittleEndian(dir[12..]);
            entries.Add((size, ico.AsSpan(offset, length).ToArray()));
        }
        entries.Sort((a, b) => a.Size.CompareTo(b.Size));
        return entries;
    }

    /// <summary>
    /// Decodes an 8-bit RGBA, non-interlaced PNG (what the icon is made of) to ARGB32 in network byte order,
    /// the layout StatusNotifierItem's <c>IconPixmap</c> wants.
    /// </summary>
    public static (int Width, int Height, byte[] Argb) DecodeToArgb(byte[] png)
    {
        int width = 0, height = 0;
        using var idat = new MemoryStream();
        var pos = 8; // signature
        while (pos + 8 <= png.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos));
            var type = png.AsSpan(pos + 4, 4);
            var data = png.AsSpan(pos + 8, length);
            if (type.SequenceEqual("IHDR"u8))
            {
                width = BinaryPrimitives.ReadInt32BigEndian(data);
                height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                // Bit depth 8, colour type 6 (RGBA), no interlacing.
                if (data[8] != 8 || data[9] != 6 || data[12] != 0)
                    throw new NotSupportedException("Only 8-bit RGBA, non-interlaced PNGs are supported.");
            }
            else if (type.SequenceEqual("IDAT"u8)) idat.Write(data);
            else if (type.SequenceEqual("IEND"u8)) break;
            pos += 12 + length;
        }

        const int bpp = 4;
        var stride = width * bpp;
        var raw = new byte[height * (stride + 1)];
        idat.Position = 0;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress)) z.ReadExactly(raw);

        var rgba = new byte[height * stride];
        for (var y = 0; y < height; y++)
        {
            var filter = raw[y * (stride + 1)];
            var line = raw.AsSpan(y * (stride + 1) + 1, stride);
            var row = rgba.AsSpan(y * stride, stride);
            var prior = y > 0 ? rgba.AsSpan((y - 1) * stride, stride) : default;
            for (var x = 0; x < stride; x++)
            {
                int a = x >= bpp ? row[x - bpp] : 0;
                int b = y > 0 ? prior[x] : 0;
                int c = x >= bpp && y > 0 ? prior[x - bpp] : 0;
                row[x] = (byte)(line[x] + filter switch
                {
                    1 => a,
                    2 => b,
                    3 => (a + b) / 2,
                    4 => Paeth(a, b, c),
                    _ => 0,
                });
            }
        }

        var argb = new byte[rgba.Length];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            argb[i] = rgba[i + 3];
            argb[i + 1] = rgba[i];
            argb[i + 2] = rgba[i + 1];
            argb[i + 3] = rgba[i + 2];
        }
        return (width, height, argb);
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
