# Architecture

## Compilation path

The offline compiler is deliberately separated from copyrighted content and from the runtime:

1. **Input manifest** describes user-authorized, already-decrypted segments, addresses, hashes, and entry points.
2. **Front end** decodes ARMv6K ARM/Thumb instructions and rejects ambiguous or unsupported encodings.
3. **Discovery** walks direct control flow and uses per-title annotations for jump tables and indirect calls.
4. **IR** expresses 32-bit guest operations, explicit flags, memory access, calls, and service boundaries.
5. **Validation** executes blocks in both a reference interpreter and translated form, comparing state changes.
6. **Optimization** performs constant propagation, dead-code elimination, block linking, and safe flag elision.
7. **Back end** emits portable C++ first; direct x64 and ARM64 machine-code back ends can follow after correctness.
8. **Native build** links generated blocks with PocketRuntime and a user-local asset package.

## Runtime boundaries

`Cpu` is the generated-code ABI. It owns guest registers and flags while delegating address translation,
service calls, and dispatch. The runtime maps 32-bit guest addresses onto host allocations and never exposes
raw host pointers to generated code.

The service layer replaces 3DS kernel and user services with narrow host implementations. Services should be
implemented from public behavior and black-box tests, not copied proprietary code. Unsupported services fail
with structured diagnostics including the service ID, guest PC, thread, and recent calls.

The graphics layer consumes emulated PICA200 register/command writes and produces a backend-neutral render
graph. Vulkan and D3D12 backends can then allocate scaled render targets. Presentation layout is separate so
top/bottom placement can change without affecting guest rendering.

## 1080p policy

Default output is 1920×1080. The 400×240 top screen scales uniformly to 1800×1080, centered at x=60.
That avoids stretching 5:3 content to 16:9. Dual-screen layouts fit both source screens into the output while
preserving each aspect ratio. Nearest, linear, bicubic, and FSR-like filters are configuration choices.

Internal-resolution scaling comes later in the GPU translator. UI or effects that depend on exact pixel sizes
may need per-title rules; the safe fallback is native-resolution rendering followed by presentation upscaling.

## FPS policy

Three clocks must remain distinct:

- **Guest clock:** what time the 3DS program observes.
- **Simulation rate:** how often game logic advances.
- **Presentation rate:** how often the PC displays a frame.

The initial safe performance mode leaves guest/simulation timing unchanged and presents at the monitor rate,
optionally interpolating. True 60/120 FPS patches must change title-specific frame caps and delta-time logic.
The runtime should never silently double simulation speed.

## First bootable target strategy

Start with a small homebrew program built from source, because expected behavior and symbols are known and no
proprietary content is required. The first target should draw to both screens, read buttons/touch, play a tone,
allocate memory, and use two threads. It becomes the regression fixture for CPU, GPU, audio, input, and services.
Only after that path is deterministic should work move to legally supplied commercial-title inputs.
