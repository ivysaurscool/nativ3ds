# `.3ds` to Windows executable pipeline

The product contract is intentionally direct:

```powershell
nativ3ds inspect-container game.3ds
nativ3ds build game.3ds --title-manifest titles/<title-id>.json --output build/<title-id>
```

The final build output will contain a Windows executable, the PocketRuntime libraries, a user-local
asset package derived from that user's input, and a build report. It will not contain Nintendo keys,
firmware, SDK files, or content copied from somebody else's game.

## Required local inputs

- A game/container the user is legally authorized to use.
- A title manifest supplied by the open-source project. It identifies code entry points, jump tables,
  service mappings, patches, and timing fixes by hashes/patterns rather than redistributing game data.
- If the user's input is encrypted, authorized already-decrypted executable content prepared outside
  nativ3ds. Do not submit keys, firmware, account data, or copyrighted game files to this repository.

## Build stages

1. **Identify:** recognize NCSD/NCCH and validate declared sizes and hashes.
2. **Load:** map authorized ExeFS code/data, symbols where available, and relocation metadata.
3. **Discover:** find reachable ARM and Thumb blocks from entry points plus title annotations.
4. **Verify:** decode strictly; unknown instructions stop the build with address/encoding diagnostics.
5. **Translate:** lower guest instructions into IR and compare test executions with the reference machine.
6. **Generate:** produce native C++/direct host code and a deterministic dispatch table.
7. **Link:** combine generated code with PocketRuntime service, memory, input, audio, and graphics layers.
8. **Package:** create the PC executable and a local asset package tied to the source-content hash.
9. **Report:** record coverage, unsupported services, patches, compiler version, and reproducible hashes.

## Current implementation status

- Stage 1: NCSD/NCCH recognition and size validation is implemented without extraction/decryption.
- Stages 3–5: initial ARM control flow, ARM IR, Thumb decoding, and a bounded reference interpreter exist.
- Project-manifest builds can analyze authorized raw ARM segments today.
- Bounds-checked ELF32 ARM loading now maps program segments, BSS, permissions, and ARM/Thumb entry mode.
- ExeFS loading, ELF relocations, complete instruction lowering, native linking, and the PC runtime are next.

The `.3ds` path is a native-recompilation pipeline, not a fallback that launches an emulator.
