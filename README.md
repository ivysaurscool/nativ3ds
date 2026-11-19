# nativ3ds

nativ3ds is an early clean-room Nintendo 3DS-to-PC static recompilation toolkit. The goal is to
translate a title's ARM11 code ahead of time into native PC code, then provide portable replacements
for the 3DS operating-system, graphics, audio, input, and timing interfaces.

The committed product interface is a local `.3ds`/NCCH-to-Windows build pipeline. Container recognition
exists now; ExeFS loading and native linking are still under construction. See
[`docs/BUILD_PIPELINE.md`](docs/BUILD_PIPELINE.md) for the exact input/output contract.

This repository is **milestone 0.3**, not a game runner yet. It proves the core pipeline:

```text
authorized decrypted ARM code -> ARM decoder -> portable IR -> generated C++ -> PC runtime ABI
                                                    |
                                     1080p layout + frame timing
```

## What works now

- Decoding for an initial ARM-state subset: `MOV`, `ADD`, `SUB`, `CMP`, immediate word `LDR`/`STR`,
  `B`, `BL`, `BX`, and `SVC`.
- Strict decoding for an initial Thumb-1 subset, including shifts, arithmetic, literal/word loads,
  stores, stack lists, conditional branches, `BL`, `BX`, and `SVC`.
- Translation into a small, testable intermediate representation.
- A reference IR interpreter with ARM condition flags, bounded guest memory, calls, branches, and SVC exits.
- Reachable basic-block and direct-call discovery from declared entry points.
- A portable JSON project manifest with segment bounds and optional SHA-256 verification.
- Generated C++ blocks targeting a stable runtime interface.
- Correct ARM conditional-execution wrapping for translated instructions.
- 3DS top-only, stacked, and side-by-side screen layouts at arbitrary output sizes.
- Automatic 1080p top-screen scaling: 400×240 becomes 1800×1080 with 60-pixel side bars.
- Separate guest simulation and presentation rates, ready for interpolation and title patches.
- A dependency-free CLI and test runner; all milestone tests pass.
- GitHub Actions continuous integration on Windows.

## Important reality checks

“1080p” currently means scaling the final framebuffer while preserving its aspect ratio. True higher
internal resolution requires translating the PICA200 GPU command stream and resizing render targets.
Likewise, displaying at 120/144 Hz is safe, but making a 30/60 FPS game's simulation run faster often
requires a per-title timing patch so physics and animation do not speed up.

Static recompilation removes repeated CPU instruction interpretation/JIT overhead. It does not, by
itself, guarantee a speedup over mature emulators: GPU translation, synchronization, service shims,
and the quality of generated native code will determine real performance.

## Build and try it

Requirements: Windows and the .NET 9 SDK.

```powershell
./build.ps1
./src/PocketRecomp.Cli/bin/Release/net9.0/nativ3ds.exe demo
./src/PocketRecomp.Cli/bin/Release/net9.0/nativ3ds.exe analyze-project ./demo-output/project.json --strict
./src/PocketRecomp.Cli/bin/Release/net9.0/nativ3ds.exe layout --mode stacked
```

To inspect code that you are authorized to use:

```powershell
./src/PocketRecomp.Cli/bin/Release/net9.0/nativ3ds.exe inspect ./code.bin --base 00100000
./src/PocketRecomp.Cli/bin/Release/net9.0/nativ3ds.exe inspect-thumb ./thumb-code.bin --base 00100000
./src/PocketRecomp.Cli/bin/Release/net9.0/nativ3ds.exe inspect-container ./game.3ds
./src/PocketRecomp.Cli/bin/Release/net9.0/nativ3ds.exe inspect-elf ./homebrew.elf
./src/PocketRecomp.Cli/bin/Release/net9.0/nativ3ds.exe recompile ./code.bin --base 00100000 --out block.cpp
./src/PocketRecomp.Cli/bin/Release/net9.0/nativ3ds.exe analyze-project ./project.json
```

Inputs are raw little-endian ARM code. `--base` and `--offset` are hexadecimal. Copy
`config/project.example.json` beside an authorized `code.bin` to use reachable-code analysis.
The prototype deliberately does not parse or decrypt encrypted game containers.

## Legal and project boundary

PocketRecomp intentionally contains no Nintendo code, keys, firmware, games, SDK material, or
decryption/circumvention features. It does not download or extract any of them. Only use code and
assets that you created or are legally authorized to use. A finished port should be distributed as
a patch/tool that requires each user to provide their own lawful input, never as Nintendo game data.

## Next engineering milestones

1. Complete ARMv6K ARM-state and Thumb-1 lowering, then add VFPv2.
2. Extend the ELF loader from program segments/entry points to symbols and relocations.
3. Expand control-flow discovery with indirect-call tables and differential checks against the interpreter.
4. Implement guest virtual memory, thread scheduling, TLS, atomics, exceptions, and SVC/service shims.
5. Translate PICA200 commands to Vulkan/D3D12, including render-target scaling to 1080p and shader generation.
6. Add DSP audio, controller/touch/motion mapping, files/save-data redirection, and deterministic timing.
7. Add per-title patch manifests for addresses that cannot be found safely by generic analysis.
8. Profile generated x64/ARM64 code, then add optimization passes and persistent native build caching.

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for component boundaries and the boot path.
