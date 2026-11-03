# Concentus (Opus codec, pure C#)

QuickMatch voice chat encodes and decodes Opus with Concentus, a C# port of the Opus reference
library by Logan Stromberg, under the Opus BSD license (`LICENSE`, which must stay with this code).

- Source: https://github.com/lostromb/concentus, commit `3885c4e46513ef0fc81fca100189e54f1714c6ca`
  (2025-09-27, package version 2.2.2), folder `CSharp/Concentus`.
- Download checked on 2026-10-29: `codeload.github.com/lostromb/concentus/tar.gz/<commit>`,
  SHA-256 `7c20f41c0da58ce16d24103006e7f5b48102762ae0c5f5bc7ac42782697b4a14`.
- The 132 files listed in `SOURCES.sha256` are byte-identical to that commit. Check with
  `shasum -a 256 -c SOURCES.sha256` in this folder.

Left out on purpose:
- `Native/`, `OpusCodecFactory.cs` and `ResamplerFactory.cs`: they load a native libopus through
  P/Invoke. The iPhone port's mod checker refuses P/Invoke, and the managed codec is what every
  platform uses.
- `AssemblyInfo.cs` and the project file.

Added: `NativeNamespace.cs` (an empty `Concentus.Native` namespace for one `using` line) and
`.editorconfig` (treats this folder as generated code, so the mod's nullable warnings skip it).

Why it is compiled into QuickMatch: the desktop game loads only `QuickMatch.dll` from the mod folder
and resolves no other DLLs, and the iPhone build takes exactly one audited DLL per mod. The source
is therefore part of the mod and goes through the same review and hash pinning.
