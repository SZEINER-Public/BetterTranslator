# Parity

Host: macOS 26.5.2 on Arm64. The Windows head needs WPF and a Windows host, so windows.png and windows.tree.json are produced by mac/tools/parity-windows and are absent here.

Rendered at 1440x900 and 1280x800 logical size, light theme, fixed DPI, fixed seeded content. The wrapper renders through the Avalonia headless platform with the Skia backend, which has no windowing backend: no operating system window is created, shown or focused, and no frame is taken from the interactive desktop.

## Gates

| Screen | Size | Structural | Token | Text | String | Pixel |
|---|---|---|---|---|---|---|
| sidebar-no-chats | 1440x900 | deferred | deferred | deferred | pass | deferred |
| sidebar-no-chats | 1280x800 | deferred | deferred | deferred | pass | deferred |
| sidebar-has-chats | 1440x900 | deferred | deferred | deferred | pass | deferred |
| sidebar-has-chats | 1280x800 | deferred | deferred | deferred | pass | deferred |
| sidebar-search-open | 1440x900 | deferred | deferred | deferred | pass | deferred |
| sidebar-search-open | 1280x800 | deferred | deferred | deferred | pass | deferred |
| chat-empty | 1440x900 | deferred | deferred | deferred | pass | deferred |
| chat-empty | 1280x800 | deferred | deferred | deferred | pass | deferred |
| chat-populated | 1440x900 | deferred | deferred | deferred | pass | deferred |
| chat-populated | 1280x800 | deferred | deferred | deferred | pass | deferred |
| chat-skeleton | 1440x900 | deferred | deferred | deferred | pass | deferred |
| chat-skeleton | 1280x800 | deferred | deferred | deferred | pass | deferred |
| settings-memory | 1440x900 | deferred | deferred | deferred | pass | deferred |
| settings-memory | 1280x800 | deferred | deferred | deferred | pass | deferred |
| settings-your-data | 1440x900 | deferred | deferred | deferred | pass | deferred |
| settings-your-data | 1280x800 | deferred | deferred | deferred | pass | deferred |
| settings-runtime | 1440x900 | deferred | deferred | deferred | pass | deferred |
| settings-runtime | 1280x800 | deferred | deferred | deferred | pass | deferred |
| settings-downloads | 1440x900 | deferred | deferred | deferred | pass | deferred |
| settings-downloads | 1280x800 | deferred | deferred | deferred | pass | deferred |
| settings-agent | 1440x900 | deferred | deferred | deferred | pass | deferred |
| settings-agent | 1280x800 | deferred | deferred | deferred | pass | deferred |
| settings-config | 1440x900 | deferred | deferred | deferred | pass | deferred |
| settings-config | 1280x800 | deferred | deferred | deferred | pass | deferred |
| settings-updates | 1440x900 | deferred | deferred | deferred | pass | deferred |
| settings-updates | 1280x800 | deferred | deferred | deferred | pass | deferred |
| first-run-choosing | 1440x900 | deferred | deferred | deferred | pass | deferred |
| first-run-choosing | 1280x800 | deferred | deferred | deferred | pass | deferred |
| add-sources-empty | 1440x900 | deferred | deferred | deferred | pass | deferred |
| add-sources-empty | 1280x800 | deferred | deferred | deferred | pass | deferred |
| add-sources-files-chosen | 1440x900 | deferred | deferred | deferred | pass | deferred |
| add-sources-files-chosen | 1280x800 | deferred | deferred | deferred | pass | deferred |
| file-preview-source | 1440x900 | deferred | deferred | deferred | pass | deferred |
| file-preview-source | 1280x800 | deferred | deferred | deferred | pass | deferred |
| retrieval-map-empty | 1440x900 | deferred | deferred | deferred | pass | deferred |
| retrieval-map-empty | 1280x800 | deferred | deferred | deferred | pass | deferred |
| resize-dividers | 1440x900 | deferred | deferred | deferred | pass | deferred |
| resize-dividers | 1280x800 | deferred | deferred | deferred | pass | deferred |

## Contact sheet

| Screen | Size | windows.png | mac.png | diff.png | trees |
|---|---|---|---|---|---|
| sidebar-no-chats | 1440x900 | absent | [mac.png](../parity-out/sidebar-no-chats/1440x900/mac.png) | absent | [mac.tree.json](../parity-out/sidebar-no-chats/1440x900/mac.tree.json) absent |
| sidebar-no-chats | 1280x800 | absent | [mac.png](../parity-out/sidebar-no-chats/1280x800/mac.png) | absent | [mac.tree.json](../parity-out/sidebar-no-chats/1280x800/mac.tree.json) absent |
| sidebar-has-chats | 1440x900 | absent | [mac.png](../parity-out/sidebar-has-chats/1440x900/mac.png) | absent | [mac.tree.json](../parity-out/sidebar-has-chats/1440x900/mac.tree.json) absent |
| sidebar-has-chats | 1280x800 | absent | [mac.png](../parity-out/sidebar-has-chats/1280x800/mac.png) | absent | [mac.tree.json](../parity-out/sidebar-has-chats/1280x800/mac.tree.json) absent |
| sidebar-search-open | 1440x900 | absent | [mac.png](../parity-out/sidebar-search-open/1440x900/mac.png) | absent | [mac.tree.json](../parity-out/sidebar-search-open/1440x900/mac.tree.json) absent |
| sidebar-search-open | 1280x800 | absent | [mac.png](../parity-out/sidebar-search-open/1280x800/mac.png) | absent | [mac.tree.json](../parity-out/sidebar-search-open/1280x800/mac.tree.json) absent |
| chat-empty | 1440x900 | absent | [mac.png](../parity-out/chat-empty/1440x900/mac.png) | absent | [mac.tree.json](../parity-out/chat-empty/1440x900/mac.tree.json) absent |
| chat-empty | 1280x800 | absent | [mac.png](../parity-out/chat-empty/1280x800/mac.png) | absent | [mac.tree.json](../parity-out/chat-empty/1280x800/mac.tree.json) absent |
| chat-populated | 1440x900 | absent | [mac.png](../parity-out/chat-populated/1440x900/mac.png) | absent | [mac.tree.json](../parity-out/chat-populated/1440x900/mac.tree.json) absent |
| chat-populated | 1280x800 | absent | [mac.png](../parity-out/chat-populated/1280x800/mac.png) | absent | [mac.tree.json](../parity-out/chat-populated/1280x800/mac.tree.json) absent |
| chat-skeleton | 1440x900 | absent | [mac.png](../parity-out/chat-skeleton/1440x900/mac.png) | absent | [mac.tree.json](../parity-out/chat-skeleton/1440x900/mac.tree.json) absent |
| chat-skeleton | 1280x800 | absent | [mac.png](../parity-out/chat-skeleton/1280x800/mac.png) | absent | [mac.tree.json](../parity-out/chat-skeleton/1280x800/mac.tree.json) absent |
| settings-memory | 1440x900 | absent | [mac.png](../parity-out/settings-memory/1440x900/mac.png) | absent | [mac.tree.json](../parity-out/settings-memory/1440x900/mac.tree.json) absent |
| settings-memory | 1280x800 | absent | [mac.png](../parity-out/settings-memory/1280x800/mac.png) | absent | [mac.tree.json](../parity-out/settings-memory/1280x800/mac.tree.json) absent |
| settings-your-data | 1440x900 | absent | [mac.png](../parity-out/settings-your-data/1440x900/mac.png) | absent | [mac.tree.json](../parity-out/settings-your-data/1440x900/mac.tree.json) absent |
| settings-your-data | 1280x800 | absent | [mac.png](../parity-out/settings-your-data/1280x800/mac.png) | absent | [mac.tree.json](../parity-out/settings-your-data/1280x800/mac.tree.json) absent |
| settings-runtime | 1440x900 | absent | [mac.png](../parity-out/settings-runtime/1440x900/mac.png) | absent | [mac.tree.json](../parity-out/settings-runtime/1440x900/mac.tree.json) absent |
| settings-runtime | 1280x800 | absent | [mac.png](../parity-out/settings-runtime/1280x800/mac.png) | absent | [mac.tree.json](../parity-out/settings-runtime/1280x800/mac.tree.json) absent |
| settings-downloads | 1440x900 | absent | [mac.png](../parity-out/settings-downloads/1440x900/mac.png) | absent | [mac.tree.json](../parity-out/settings-downloads/1440x900/mac.tree.json) absent |
| settings-downloads | 1280x800 | absent | [mac.png](../parity-out/settings-downloads/1280x800/mac.png) | absent | [mac.tree.json](../parity-out/settings-downloads/1280x800/mac.tree.json) absent |
| settings-agent | 1440x900 | absent | [mac.png](../parity-out/settings-agent/1440x900/mac.png) | absent | [mac.tree.json](../parity-out/settings-agent/1440x900/mac.tree.json) absent |
| settings-agent | 1280x800 | absent | [mac.png](../parity-out/settings-agent/1280x800/mac.png) | absent | [mac.tree.json](../parity-out/settings-agent/1280x800/mac.tree.json) absent |
| settings-config | 1440x900 | absent | [mac.png](../parity-out/settings-config/1440x900/mac.png) | absent | [mac.tree.json](../parity-out/settings-config/1440x900/mac.tree.json) absent |
| settings-config | 1280x800 | absent | [mac.png](../parity-out/settings-config/1280x800/mac.png) | absent | [mac.tree.json](../parity-out/settings-config/1280x800/mac.tree.json) absent |
| settings-updates | 1440x900 | absent | [mac.png](../parity-out/settings-updates/1440x900/mac.png) | absent | [mac.tree.json](../parity-out/settings-updates/1440x900/mac.tree.json) absent |
| settings-updates | 1280x800 | absent | [mac.png](../parity-out/settings-updates/1280x800/mac.png) | absent | [mac.tree.json](../parity-out/settings-updates/1280x800/mac.tree.json) absent |
| first-run-choosing | 1440x900 | absent | [mac.png](../parity-out/first-run-choosing/1440x900/mac.png) | absent | [mac.tree.json](../parity-out/first-run-choosing/1440x900/mac.tree.json) absent |
| first-run-choosing | 1280x800 | absent | [mac.png](../parity-out/first-run-choosing/1280x800/mac.png) | absent | [mac.tree.json](../parity-out/first-run-choosing/1280x800/mac.tree.json) absent |
| add-sources-empty | 1440x900 | absent | [mac.png](../parity-out/add-sources-empty/1440x900/mac.png) | absent | [mac.tree.json](../parity-out/add-sources-empty/1440x900/mac.tree.json) absent |
| add-sources-empty | 1280x800 | absent | [mac.png](../parity-out/add-sources-empty/1280x800/mac.png) | absent | [mac.tree.json](../parity-out/add-sources-empty/1280x800/mac.tree.json) absent |
| add-sources-files-chosen | 1440x900 | absent | [mac.png](../parity-out/add-sources-files-chosen/1440x900/mac.png) | absent | [mac.tree.json](../parity-out/add-sources-files-chosen/1440x900/mac.tree.json) absent |
| add-sources-files-chosen | 1280x800 | absent | [mac.png](../parity-out/add-sources-files-chosen/1280x800/mac.png) | absent | [mac.tree.json](../parity-out/add-sources-files-chosen/1280x800/mac.tree.json) absent |
| file-preview-source | 1440x900 | absent | [mac.png](../parity-out/file-preview-source/1440x900/mac.png) | absent | [mac.tree.json](../parity-out/file-preview-source/1440x900/mac.tree.json) absent |
| file-preview-source | 1280x800 | absent | [mac.png](../parity-out/file-preview-source/1280x800/mac.png) | absent | [mac.tree.json](../parity-out/file-preview-source/1280x800/mac.tree.json) absent |
| retrieval-map-empty | 1440x900 | absent | [mac.png](../parity-out/retrieval-map-empty/1440x900/mac.png) | absent | [mac.tree.json](../parity-out/retrieval-map-empty/1440x900/mac.tree.json) absent |
| retrieval-map-empty | 1280x800 | absent | [mac.png](../parity-out/retrieval-map-empty/1280x800/mac.png) | absent | [mac.tree.json](../parity-out/retrieval-map-empty/1280x800/mac.tree.json) absent |
| resize-dividers | 1440x900 | absent | [mac.png](../parity-out/resize-dividers/1440x900/mac.png) | absent | [mac.tree.json](../parity-out/resize-dividers/1440x900/mac.tree.json) absent |
| resize-dividers | 1280x800 | absent | [mac.png](../parity-out/resize-dividers/1280x800/mac.png) | absent | [mac.tree.json](../parity-out/resize-dividers/1280x800/mac.tree.json) absent |

## Failures and deferrals

| Screen | Size | Gate | Verdict | Detail |
|---|---|---|---|---|
| every screen (38 captures) | both | pixel | DEFERRED | one side is missing on this host |
| every screen (38 captures) | both | structural | DEFERRED | no reference tree on this host |
| every screen (38 captures) | both | text | DEFERRED | no reference tree on this host |
| every screen (38 captures) | both | token | DEFERRED | no reference tree on this host |

## Declared deltas

Only a difference declared here may differ visually between the Windows head and the macOS wrapper. A gate is never widened, disabled or given a looser tolerance to make a screen pass. A visual difference not declared here is a defect.

| Delta | Region | Screens | Exercised | Reason |
|---|---|---|---|---|
| traffic-lights | titlebar caption controls | main-window-shell | deferred | macOS draws close, minimise and zoom as traffic lights at the leading edge, painted by the platform. The Windows head draws minimise, maximise and close as 46 wide caption buttons at the trailing edge. The platform owns the control, so neither its geometry nor its position can match. MainWindowViewModel is a composition root: its parameterless constructor opens the profile database, starts the agent server and constructs a LocalTranslator. The window shell is therefore not rendered offscreen in this run, so this delta is declared but not exercised. See deferred-macos.md. |
| menu-bar | application menu bar | main-window-shell | deferred | macOS carries the application menu in the system menu bar outside the window. The Windows head has no menu bar at all, so the wrapper gains a surface the Windows head never renders. MainWindowViewModel is a composition root: its parameterless constructor opens the profile database, starts the agent server and constructs a LocalTranslator. The window shell is therefore not rendered offscreen in this run, so this delta is declared but not exercised. See deferred-macos.md. |
| command-accelerators | rendered keyboard shortcut hints | chat-populated, settings-memory, settings-your-data, settings-runtime, settings-downloads, settings-agent, settings-config, settings-updates | yes | Command replaces Control in every accelerator, so a rendered shortcut hint differs by one glyph. |
| system-font | every text run | * | yes | The wrapper uses the macOS system font in place of Segoe UI Variable Text. The two do not share metrics, so advance widths, line boxes and the resulting text bounds differ. Font metric differences are excluded from the pixel gate for this reason. The token, text and string gates still apply in full, and the structural gate still applies to every bound that is not text derived. |
| overlay-scrollbars | scroll bars | * | yes | macOS draws overlay scrollbars that occupy no layout width and fade when idle. The Windows head reserves an 8 wide track through the ScrollThumbWidth token. |
| focus-ring | focused control outline | * | yes | The macOS focus ring is drawn by the platform with its own inset and corner radius. The Windows head draws a 2 wide accent rectangle at offset 2 through the FocusRingWidth and FocusRingOffset tokens. |
| hig-control-metrics | platform drawn push buttons, text fields, checkboxes and toggles | * | yes | Human Interface Guidelines control heights and corner radii differ from the Windows values where the platform draws the control rather than the application. Application drawn controls keep the Windows token values exactly. |
| native-file-panels | the button that opens a file or folder panel | add-sources-empty, add-sources-files-chosen, settings-your-data, settings-downloads, first-run-choosing | yes | The wrapper opens the native macOS panel through the storage provider seam instead of the Win32 common dialog. The panel is not part of the window and is not captured; only the wording on the button that opens it is platform specific. |
| cpu-only-backend-section | runtime backend section | settings-runtime, first-run-choosing | yes | The wrapper runs the CPU inference backend and nothing else. The Windows accelerator picker, its hardware line, its install and repair flow and its restart notice are absent from the wrapper's view tree rather than disabled, and are replaced by a single CPU-only statement driven by the backend seam. |
| updates-unsupported | update controls | settings-updates | yes | The Windows head installs updates through an elevated Windows service. There is no background installer on this platform yet, so the wrapper renders the seam's unsupported reason in place of the enable, check and install controls. |
