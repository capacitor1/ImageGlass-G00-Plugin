# ImageGlass RealLive G00 Codec Plugin

Native **decode-only** codec plugin for ImageGlass v10 that adds `.g00` support,
ported from the GARbro / GameRes C# G00 decoder (morkt) to pure `System` APIs.

| Extension | Read | Write |
| --------- | ---- | ----- |
| `.g00`    | ✅ type 0, 1 & 2 | ❌ |

Supports the three RealLive G00 storage variants:

| Type | Content | Notes |
| ---- | ------- | ----- |
| `0` | 24bpp BGR | LZ77-compressed single plane, exported as opaque BGRA |
| `1` | 8bpp paletted | BGRA palette + index plane, expanded to BGRA |
| `2` | 32bpp BGRA | tiled; a first LZ stream holds the tile directory |

No SkiaSharp or any third-party dependency — the decoder is pure C#, so the package
stays tiny and fully cross-platform.

## Install

1. Download `G00Codec-<platform>.igplugin.zip` from the latest release (or from the
   `publish` workflow artifacts).
2. ImageGlass → **Settings → Plugins → Add**, pick the `.igplugin.zip`.
3. Click **Trust** and enable the plugin.

The zip contains the Native AOT shared library (`G00Codec.dll` / `libG00Codec.so` /
`G00Codec.dylib`) and a manifest whose `executable` field is patched to match that
platform's filename at packaging time.

## Build

Requires the .NET 10 SDK. Native AOT publishing needs a C++ toolchain
(Visual Studio "Desktop development with C++" on Windows, `clang` on Linux/macOS).

```powershell
# publish for one platform
dotnet publish source/ImageGlassG00Codec.csproj -c Release -r win-x64 -p:Platform=x64 -o out/win-x64
```

Pushing to `main` (or running the `publish` workflow manually) builds and packs
`win-x64`, `linux-x64` and `osx-arm64`.

## Testing

`source/G00Decoder.cs` is the single decoder used by the plugin. The
`tests/G00DecoderTest` console harness compiles that exact file and checks it
against committed synthetic samples for type 0 and type 1:

```powershell
dotnet run --project tests/G00DecoderTest -c Release
```

The real-world sample `bg01.g00` is **not** committed (it is a copyrighted game
asset). Drop it into `tests/data/bg01.g00` and the harness will additionally verify
dimensions (1152×720), the top-left/center pixels, and the MD5 of the decoded BGRA
buffer. The same checks are what the `test` workflow runs in CI for the synthetic
samples.

## Layout

```
source/G00Decoder.cs        # pure decode logic (no ImageGlass dependency)
source/G00CodecPlugin.cs    # ImageGlass v10 native ABI glue
source/ImageGlassG00Codec.csproj
source/igplugin.json        # manifest (executable patched per platform when packing)
tests/G00DecoderTest/       # standalone decoder test harness
tests/data/                 # synthetic v0/v1 samples (bg01.g00 gitignored)
```

## Credits

Decoder ported from [GARbro](https://github.com/morkt/GARbro)
(`ArcFormats/RealLive/ImageG00.cs`, Copyright © 2016 morkt, MIT License).
Plugin ABI follows the [ImageGlass SDK](https://github.com/ImageGlass/SDK).

## License

MIT. See [LICENSE](LICENSE).
