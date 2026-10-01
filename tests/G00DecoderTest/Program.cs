//! Standalone test harness for G00Decoder. It compiles the very file that ships
//! inside the ImageGlass plugin, so a green run here means the decoding logic is
//! sound before the Native AOT publish is attempted.
//!
//! Usage:
//!   dotnet run --project tests/G00DecoderTest                 (default samples)
//!   dotnet run --project tests/G00DecoderTest -- path/to.g00  (explicit files)
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using G00Codec;

namespace G00DecoderTest;

internal static class Program
{
    private static int _failures;

    private static string ThisFile([CallerFilePath] string path = "") => path;

    /// <summary>Plugin root, derived from this source file's location so the
    /// default sample paths work no matter what the process working directory is.</summary>
    private static string PluginRoot()
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ThisFile())!, "..", ".."));

    private static string[] DefaultSamples()
    {
        var root = PluginRoot();
        return
        [
            Path.Combine(root, "tests", "data", "synthetic_v0.g00"),
            Path.Combine(root, "tests", "data", "synthetic_v1.g00"),
            Path.Combine(root, "tests", "data", "bg01.g00"),
        ];
    }

    private static int Main(string[] args)
    {
        var files = args.Length > 0 ? args : DefaultSamples();

        foreach (var file in files)
        {
            if (!File.Exists(file))
            {
                Console.WriteLine($"SKIP  {file} (not found)");
                continue;
            }

            try
            {
                Check(file);
            }
            catch (Exception ex)
            {
                _failures++;
                Console.WriteLine($"FAIL  {file}: {ex.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "All checks passed." : $"{_failures} check(s) failed.");
        return _failures == 0 ? 0 : 1;
    }

    private static void Check(string file)
    {
        var bytes = File.ReadAllBytes(file);
        var meta = G00Decoder.ReadMetaData(bytes) ?? throw new Exception("ReadMetaData returned null");
        var decoded = G00Decoder.Decode(bytes) ?? throw new Exception("Decode returned null");

        Console.WriteLine($"OK    {file}: type={meta.Type} {meta.Width}x{meta.Height} " +
                          $"bpp={decoded.BPP} hasAlpha={decoded.HasAlpha} bytes={decoded.Pixels.Length}");

        if (decoded.Width != meta.Width || decoded.Height != meta.Height)
            throw new Exception("decoded dimensions do not match metadata");

        if (decoded.Pixels.Length != decoded.Width * decoded.Height * 4)
            throw new Exception("pixel buffer is not tightly packed BGRA");

        switch (Path.GetFileName(file))
        {
            case "synthetic_v0.g00":
                CheckSyntheticV0(decoded);
                break;
            case "synthetic_v1.g00":
                CheckSyntheticV1(decoded);
                break;
            case "bg01.g00":
                CheckBg01(decoded);
                break;
        }
    }

    private static void CheckSyntheticV0(G00Decoded d)
    {
        const int w = 32, h = 24;
        if (d.Width != w || d.Height != h) throw new Exception("bad dimensions");
        if (d.BPP != 24 || d.HasAlpha) throw new Exception("bad format flags");

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                byte b = (byte)(((x + y) * 4) & 0xFF);
                byte g = (byte)((y * 8) & 0xFF);
                byte r = (byte)((x * 8) & 0xFF);
                if (d.Pixels[i] != b || d.Pixels[i + 1] != g || d.Pixels[i + 2] != r || d.Pixels[i + 3] != 0xFF)
                    throw new Exception($"pixel ({x},{y}) mismatch");
            }
        }
    }

    private static void CheckSyntheticV1(G00Decoded d)
    {
        const int w = 8, h = 6;
        if (d.Width != w || d.Height != h) throw new Exception("bad dimensions");
        if (d.BPP != 8 || !d.HasAlpha) throw new Exception("bad format flags");

        byte[][] palette =
        [
            [0, 0, 0, 255],
            [0, 0, 255, 255],
            [0, 255, 0, 255],
            [255, 0, 0, 128],
        ];

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                var want = palette[(x + y) % 4];
                if (d.Pixels[i] != want[0] || d.Pixels[i + 1] != want[1] ||
                    d.Pixels[i + 2] != want[2] || d.Pixels[i + 3] != want[3])
                    throw new Exception($"palette pixel ({x},{y}) mismatch");
            }
        }
    }

    private static void CheckBg01(G00Decoded d)
    {
        if (d.Width != 1152 || d.Height != 720) throw new Exception("bad dimensions");
        if (d.BPP != 24 || d.HasAlpha) throw new Exception("bad format flags");

        const string expectedMd5 = "0533569d24530daf3da351b46be80b16";
        var md5 = Convert.ToHexString(MD5.HashData(d.Pixels)).ToLowerInvariant();
        if (md5 != expectedMd5)
            throw new Exception($"pixel hash mismatch: {md5} != {expectedMd5}");

        // Spot checks against the reference decode.
        var p0 = new[] { d.Pixels[0], d.Pixels[1], d.Pixels[2], d.Pixels[3] };
        if (p0[0] != 232 || p0[1] != 251 || p0[2] != 255 || p0[3] != 255)
            throw new Exception("top-left pixel mismatch");

        int mid = ((720 / 2) * 1152 + (1152 / 2)) * 4;
        if (d.Pixels[mid] != 50 || d.Pixels[mid + 1] != 44 || d.Pixels[mid + 2] != 45 || d.Pixels[mid + 3] != 255)
            throw new Exception("center pixel mismatch");
    }
}
