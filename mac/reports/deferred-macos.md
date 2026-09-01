# Deferred: not verifiable in this run

The host for this run is macOS 26 on Apple silicon, with the .NET 10.0.400 SDK provisioned user-local under `~/.dotnet`. The run is headless by instruction: no window, console or browser was opened, raised or focused, and no screenshot was taken from the interactive desktop. Nothing below is claimed as verified.

| Item | Why it is deferred | What would verify it |
|---|---|---|
| Native menu bar placement | requires a running windowed session on the interactive desktop; opening one is forbidden for this run | launch the head on a macOS desktop session and read the menu bar |
| Traffic light window controls | same; `ExtendClientAreaToDecorationsHint` and the inset are declared but their painted result was not observed | same |
| Command key accelerators | declared as `KeyGesture` with `Cmd`; the platform mapping was not exercised | same |
| SF Pro metrics and the resulting layout shift | the font resolves only at render time on a machine with the system font; no frame was rendered on the interactive desktop | run the parity harness on a macOS session and read the emitted visual-tree JSON font metrics |
| Overlay scrollbars and the macOS focus ring | platform-drawn, not observed | same |
| `.app` bundle layout | packaging is out of scope for this run | build the bundle and inspect `Contents/MacOS`, `Contents/Resources` and `Info.plist` |
| Entitlements | out of scope | `codesign -d --entitlements` on the signed bundle |
| Code signing | out of scope | `codesign --verify --deep --strict` |
| Notarisation | out of scope | `xcrun notarytool submit` then `spctl -a -vvv -t install` |
| Execution of the CPU-only runtime on Apple silicon | `libBetterRuntimeCPU.dylib` is not built in this repository and is not present on this machine; the resolver conflict in `blocked.md` also stands in the way | build the arm64 CPU artifact with the flags in `runtime-cpu-compat.md` section 6, land the `blocked.md` proposal, then run one `br_model_load` and one `br_gen_next` |
| Thread count against performance and efficiency cores | no inference ran, so no throughput was measured | measure tokens per second at several `NThreads` values on the target Mac |
| 16 KB page and 128-byte cache line behaviour of the native build | no native artifact exists to inspect | `otool -l` on the built dylib, then a load test |
| Windows side of the parity harness | WPF does not run on this host; see `blocked.md` | run `mac/tools/parity` on a Windows host with the `net10.0-windows10.0.19041.0` targeting pack |
| Pixel gate | needs both heads rendered; only the wrapper side rendered here | same as the row above |
| The Windows product still building and behaving as today | the WPF solution cannot be built on this host, so this run neither verified it nor could have broken it | `git status` shows no path outside `mac/` changed, which is the property that matters; build `BetterTranslator.sln` on a Windows host to confirm |
