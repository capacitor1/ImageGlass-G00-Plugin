//! RealLive G00 image decoder, ported from GARbro (morkt) to pure System APIs.
//! Original: Copyright (C) 2016 by morkt (MIT License).
//!
//! G00 is a headerless little-endian container. The first byte selects the variant:
//!   0 - 24bpp BGR, LZ77-compressed (single stream, 3 bytes per unit)
//!   1 - 8bpp paletted, LZ77-compressed (palette + index plane)
//!   2 - 32bpp BGRA, tiled; a first LZ stream holds the tile directory, the raw
//!       tile payloads follow uncompressed.
//!
//! The decoder always emits a tightly packed BGRA buffer (stride = Width * 4),
//! which is what the ImageGlass codec contract expects.
using System;
using System.IO;

namespace G00Codec;

internal sealed class G00MetaData
{
    public int Width;
    public int Height;
    public int BPP;   // 8 for the paletted variant, 24 otherwise
    public int Type;  // 0, 1 or 2
}

internal sealed class G00Decoded
{
    public int Width;
    public int Height;
    public int BPP;
    public bool HasAlpha;
    public byte[] Pixels = Array.Empty<byte>(); // BGRA, stride = Width * 4
}

/// <summary>G00 decoder. All methods are static and side-effect free.</summary>
internal static class G00Decoder
{
    // Safety cap: reject absurd dimensions before allocating (1G pixels ≈ 4 GiB BGRA).
    private const long MaxPixels = 0x10000000;
    // Hard ceiling for a single LZ output buffer (1 GiB) to survive corrupt headers.
    private const int MaxOutputSize = 0x40000000;

    public static G00MetaData? ReadMetaData(byte[] data)
    {
        if (data is null || data.Length < 9) return null;

        int type = data[0];
        if (type > 2) return null;

        int width = data[1] | (data[2] << 8);
        int height = data[3] | (data[4] << 8);
        if (0 == width || width > 0x8000 || 0 == height || height > 0x8000) return null;

        if (2 == type)
        {
            int count = ReadInt32(data, 5);
            if (count <= 0 || count > 0x1000) return null;
        }
        else
        {
            uint length = ReadUInt32(data, 5);
            // The stored length covers everything after the 5-byte fixed header.
            if ((long)length + 5 != data.Length) return null;
        }

        return new G00MetaData
        {
            Width = width,
            Height = height,
            BPP = 1 == type ? 8 : 24,
            Type = type,
        };
    }

    /// <summary>Decode a whole G00 file to BGRA. Returns null when the file is
    /// not valid G00 or uses an unsupported sub-variant.</summary>
    public static G00Decoded? Decode(byte[] data)
    {
        var meta = ReadMetaData(data);
        if (meta is null) return null;
        if ((long)meta.Width * meta.Height > MaxPixels) return null;

        try
        {
            return meta.Type switch
            {
                0 => DecodeV0(meta, data),
                1 => DecodeV1(meta, data),
                _ => DecodeV2(meta, data),
            };
        }
        catch
        {
            // Malformed input: every helper below bounds-checks and throws.
            return null;
        }
    }

    // ============================== type 0: BGR24 ==============================

    private static G00Decoded DecodeV0(G00MetaData meta, byte[] data)
    {
        var reader = new ByteReader(data) { Position = 5 };
        int pixelCount = meta.Width * meta.Height;
        int need = pixelCount * 3;

        byte[] raw = LzDecompress(reader, 1, 3, need);
        if (raw.Length < need) throw new InvalidDataException("truncated BGR stream");

        var pixels = new byte[pixelCount * 4];
        for (int i = 0, s = 0, d = 0; i < pixelCount; i++)
        {
            pixels[d++] = raw[s++]; // B
            pixels[d++] = raw[s++]; // G
            pixels[d++] = raw[s++]; // R
            pixels[d++] = 0xFF;     // opaque
        }

        return new G00Decoded
        {
            Width = meta.Width,
            Height = meta.Height,
            BPP = 24,
            HasAlpha = false,
            Pixels = pixels,
        };
    }

    // ============================== type 1: paletted ==============================

    private static G00Decoded DecodeV1(G00MetaData meta, byte[] data)
    {
        var reader = new ByteReader(data) { Position = 5 };
        int pixelCount = meta.Width * meta.Height;

        // The decompressed stream is: uint16 color count, count * BGRA palette entries,
        // then one byte per pixel of palette indices.
        byte[] raw = LzDecompress(reader, 2, 1, 2 + 256 * 4 + pixelCount);
        if (raw.Length < 2) throw new InvalidDataException("truncated palette stream");

        int colors = raw[0] | (raw[1] << 8);
        if (colors <= 0 || colors > 256) throw new InvalidDataException("bad palette size");

        int paletteStart = 2;
        int indexStart = paletteStart + colors * 4;
        if (raw.Length < indexStart + pixelCount) throw new InvalidDataException("truncated index plane");

        var pixels = new byte[pixelCount * 4];
        int paletteBytes = colors * 4;
        bool hasAlpha = false;
        for (int i = 0; i < colors; i++)
        {
            if (raw[paletteStart + i * 4 + 3] != 0xFF) hasAlpha = true;
        }

        for (int i = 0; i < pixelCount; i++)
        {
            int idx = raw[indexStart + i];
            int d = i * 4;
            if (idx < colors)
            {
                int s = paletteStart + idx * 4;
                pixels[d + 0] = raw[s + 0]; // B
                pixels[d + 1] = raw[s + 1]; // G
                pixels[d + 2] = raw[s + 2]; // R
                pixels[d + 3] = raw[s + 3]; // A
            }
            // Out-of-range indices are left as transparent black.
        }

        return new G00Decoded
        {
            Width = meta.Width,
            Height = meta.Height,
            BPP = 8,
            HasAlpha = hasAlpha,
            Pixels = pixels,
        };
    }

    // ============================== type 2: tiled BGRA ==============================

    private static G00Decoded DecodeV2(G00MetaData meta, byte[] data)
    {
        var reader = new ByteReader(data) { Position = 5 };
        int tileCount = reader.ReadInt32();
        if (tileCount <= 0 || tileCount > 0x1000) throw new InvalidDataException("bad tile count");

        var tileX = new int[tileCount];
        var tileY = new int[tileCount];
        for (int i = 0; i < tileCount; i++)
        {
            tileX[i] = reader.ReadInt32();
            tileY[i] = reader.ReadInt32();
            reader.Seek(0x10, SeekOrigin.Current);
        }

        // The tile directory is itself LZ-compressed.
        byte[] archive = LzDecompress(reader, 2, 1, 0);
        var ar = new ByteReader(archive);
        if (ar.ReadInt32() != tileCount) throw new InvalidDataException("tile count mismatch");

        var offsets = new uint[tileCount];
        var lengths = new int[tileCount];
        for (int i = 0; i < tileCount; i++)
        {
            offsets[i] = ar.ReadUInt32();
            lengths[i] = ar.ReadInt32();
        }

        int tile = -1;
        for (int i = 0; i < tileCount; i++)
        {
            if (lengths[i] != 0) { tile = i; break; }
        }
        if (tile < 0) throw new InvalidDataException("no tile payload");

        ar.Position = checked((int)offsets[tile]);
        int tileType = ar.ReadUInt16();
        int blockCount = ar.ReadUInt16();
        if (tileType != 1) throw new InvalidDataException("unsupported tile encoding");
        ar.Seek(0x70, SeekOrigin.Current);

        int width = meta.Width;
        int height = meta.Height;
        int dstStride = width * 4;
        var pixels = new byte[height * dstStride];

        for (int i = 0; i < blockCount; i++)
        {
            int x = ar.ReadUInt16();
            int y = ar.ReadUInt16();
            ar.ReadInt16(); // transparent color, unused
            int tw = ar.ReadUInt16();
            int th = ar.ReadUInt16();
            ar.Seek(0x52, SeekOrigin.Current);

            x += tileX[tile];
            y += tileY[tile];
            if (x < 0 || y < 0 || tw <= 0 || th <= 0 || x + tw > width || y + th > height)
                throw new InvalidDataException("tile out of bounds");

            int dst = y * dstStride + x * 4;
            int rowStride = tw * 4;
            for (int row = 0; row < th; row++)
            {
                ar.Read(pixels, dst, rowStride);
                dst += dstStride;
            }
        }

        return new G00Decoded
        {
            Width = width,
            Height = height,
            BPP = 32,
            HasAlpha = true,
            Pixels = pixels,
        };
    }

    // ============================== LZ77 ==============================

    /// <summary>
    /// RealLive's LZ77 variant. The stream starts with the packed size (including
    /// an 8-byte bias) and the uncompressed size; the payload is a bit mask byte
    /// followed by eight operations, 1 = literal copy of <paramref name="bytesPerPixel"/>
    /// bytes, 0 = back-reference encoded as a uint16 (low nibble = repeat count
    /// minus <paramref name="minCount"/>, high bits = distance in units).
    /// </summary>
    /// <param name="minOutputSize">Lower bound for the destination buffer; the file's
    /// own uncompressed-size field is sometimes rounded up (e.g. BGR24 stored as if
    /// it were BGRA), so the caller passes the true minimum it needs.</param>
    private static byte[] LzDecompress(ByteReader src, int minCount, int bytesPerPixel, int minOutputSize)
    {
        int packedSize = src.ReadInt32() - 8;
        int outputSize = src.ReadInt32();
        if (outputSize < 0 || outputSize > MaxOutputSize) outputSize = 0;
        if (minOutputSize > 0 && minOutputSize > outputSize) outputSize = minOutputSize;

        var output = new byte[Math.Max(outputSize, 1)];
        int dst = 0;
        int bits = 2;

        while (dst < output.Length && packedSize > 0)
        {
            bits >>= 1;
            if (1 == bits)
            {
                if (src.Remaining < 1) break;
                bits = src.ReadUInt8() | 0x100;
                --packedSize;
            }

            if (0 != (bits & 1))
            {
                if (bytesPerPixel <= 0 || src.Remaining < bytesPerPixel) break;
                if (dst + bytesPerPixel > output.Length) break;
                src.Read(output, dst, bytesPerPixel);
                dst += bytesPerPixel;
                packedSize -= bytesPerPixel;
            }
            else
            {
                if (packedSize < 2 || src.Remaining < 2) break;
                int offset = src.ReadUInt16();
                packedSize -= 2;

                int count = (offset & 0xF) + minCount;
                offset >>= 4;
                offset *= bytesPerPixel;
                count *= bytesPerPixel;

                if (offset <= 0 || offset > dst || count <= 0) break;
                if (dst + count > output.Length) count = output.Length - dst;

                for (int i = 0; i < count; i++)
                    output[dst + i] = output[dst - offset + i];
                dst += count;
            }
        }

        if (dst == output.Length) return output;
        // Shrink to what was actually produced (never larger than the requested min).
        var trimmed = new byte[dst];
        Buffer.BlockCopy(output, 0, trimmed, 0, dst);
        return trimmed;
    }

    // ============================== ByteReader ==============================

    private sealed class ByteReader
    {
        private readonly byte[] _d;
        private int _p;

        public ByteReader(byte[] data) { _d = data; }

        public int Position
        {
            get => _p;
            set
            {
                if (value < 0 || value > _d.Length) throw new EndOfStreamException();
                _p = value;
            }
        }

        public int Remaining => _d.Length - _p;

        public byte ReadUInt8()
        {
            if (_p + 1 > _d.Length) throw new EndOfStreamException();
            return _d[_p++];
        }

        public int ReadInt16()
        {
            if (_p + 2 > _d.Length) throw new EndOfStreamException();
            return _d[_p++] | (_d[_p++] << 8);
        }

        public int ReadUInt16()
        {
            if (_p + 2 > _d.Length) throw new EndOfStreamException();
            return _d[_p++] | (_d[_p++] << 8);
        }

        public int ReadInt32()
        {
            if (_p + 4 > _d.Length) throw new EndOfStreamException();
            int v = _d[_p] | (_d[_p + 1] << 8) | (_d[_p + 2] << 16) | (_d[_p + 3] << 24);
            _p += 4;
            return v;
        }

        public uint ReadUInt32() => (uint)ReadInt32();

        public void Read(byte[] buffer, int offset, int count)
        {
            if (count < 0 || _p + count > _d.Length) throw new EndOfStreamException();
            Buffer.BlockCopy(_d, _p, buffer, offset, count);
            _p += count;
        }

        public void Seek(int offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _p + offset,
                _ => _d.Length + offset,
            };
        }
    }

    // ============================== byte helpers ==============================

    private static int ReadInt32(byte[] b, int i)
        => b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24);

    private static uint ReadUInt32(byte[] b, int i) => (uint)ReadInt32(b, i);
}
