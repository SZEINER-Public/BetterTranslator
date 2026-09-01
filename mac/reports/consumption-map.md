# Consumption map

Tiers: L1 ProjectReference, L2 mirror project or linked compile item compiling the original source in place, L3 shim or adapter written inside `mac/`, L4 blocked. Nothing outside `mac/` was created, edited, moved, renamed or deleted. No engine source file and no ViewModel source file exists twice on disk.

## Why L1 fails for every engine project

The root `Directory.Build.props:11` sets `<PlatformTarget>x64</PlatformTarget>` for every project beneath the repository root. Measured on this host: a `ProjectReference` from a project with `RuntimeIdentifier osx-arm64` to `src/BetterTranslator.Core/BetterTranslator.Core.csproj` builds with warning CS8012 and stamps `BetterTranslator.Core.dll` with PE machine `0x8664` (AMD64). The file lands in the output folder, and the arm64 runtime then refuses it with `System.IO.FileNotFoundException: Could not load file or assembly 'BetterTranslator.Core'`. The same source compiled by a mirror project under `mac/proj/`, which takes `mac/Directory.Build.props` instead, stamps `0xAA64` (ARM64) and loads. Overriding `PlatformTarget` would mean editing a file outside `mac/`, which the freeze rule forbids, so every engine project is consumed at L2.

## Mirror projects (L2)

Each mirror sets `EnableDefaultCompileItems=false` and includes `$(WindowsSourceRoot)<Project>\**\*.cs` with a `Link` path. It holds no copied source.

| Mirror project | Original | Original TFM | Mirror TFM and RID | Source files linked | Tier |
|---|---|---|---|---|---|
| `mac/proj/BetterTranslator.Core.Mirror` | `src/BetterTranslator.Core` | net10.0 | net10.0, osx-arm64 | 24 | L2 |
| `mac/proj/BetterTranslator.Engine.Mirror` | `src/BetterTranslator.Engine` | net10.0 | net10.0, osx-arm64 | 75, plus the 9 embedded `Data/` resources re-declared with their original `LogicalName` | L2 |
| `mac/proj/BetterTranslator.Indexing.Mirror` | `src/BetterTranslator.Indexing` | net10.0 | net10.0, osx-arm64 | 20 | L2 |
| `mac/proj/BetterTranslator.Runtime.Mirror` | `src/BetterTranslator.Runtime` | net10.0 | net10.0, osx-arm64 | 38 | L2 |
| `mac/proj/BetterTranslator.Map.Mirror` | `src/BetterTranslator.Map` | net10.0 | net10.0, osx-arm64 | 4, plus `SkiaSharp.NativeAssets.macOS` for the osx-arm64 native asset | L2 |
| `mac/proj/BetterTranslator.Updates.Mirror` | `src/BetterTranslator.Updates` | net10.0-windows | net10.0, osx-arm64 | 25. Compiles clean without the `-windows` moniker: the Win32 entry points are `DllImport` metadata, which binds at call time, and nothing in the project needs a Windows-only framework type | L2 |
| `mac/proj/BetterTranslator.App.Capture.Mirror` | `src/BetterTranslator.App` | net10.0-windows10.0.19041.0 | net10.0-windows10.0.19041.0, WPF | all `.cs` except `App.xaml.cs`, all `.xaml` as `Page`, `Strings.resx`, the app icon | L2, Windows host only. Excluded from `BetterTranslator.Mac.sln` because WPF does not build on macOS. See `blocked.md` |

## Linked compile items in `BetterTranslator.Mac.Shared` (L2)

Every file below is compiled from `src/BetterTranslator.App/` in place through a `Compile Include` with a `Link` path.

| Source | Files | Why it is consumed | Tier |
|---|---|---|---|
| `src/BetterTranslator.App/ViewModels/**/*.cs` | 30 | every ViewModel the wrapper binds | L2 |
| `src/BetterTranslator.App/Controls/SegmentItem.cs` | 1 | `MainWindowViewModel` builds `SegmentItem` instances for the workspace mode control | L2 |
| `src/BetterTranslator.App/Services/Tokens.cs` | 1 | ViewModels read timings and metrics through it | L2 |
| `src/BetterTranslator.App/Services/ClockService.cs` | 1 | the one app-wide tick every relative timestamp reads | L2 |
| `src/BetterTranslator.App/Services/UpdaterGateway.cs` | 1 | declares `IUpdaterHost` and `ElevationOutcome`, which `UpdatesViewModel` takes. The concrete `UpdaterGateway` is a Windows service gateway and is never instantiated by the wrapper | L2 |
| `src/BetterTranslator.App/Services/RestartResources.cs` | 1 | `RestartLog`, `ShellActiveWork`, `NamedRestartResource` and `RestartLauncher`, all used by `MainWindowViewModel` | L2 |
| `src/BetterTranslator.App/Mcp/*.cs` | 2 | `McpServerHost` and `AppGuiBridge`. The MCP surface and the tool contract are consumed unchanged | L2 |
| `src/BetterTranslator.App/Resources/Strings.cs` | 1 | localization accessor | L2 |
| `src/BetterTranslator.App/Resources/Strings.resx` | 1 | embedded with `LogicalName="BetterTranslator.App.Resources.Strings.resources"` so `ResourceManager` finds it under the wrapper's assembly name | L2 |

## Shims and adapters written inside `mac/` (L3)

| File | Type it supplies | Why L1 and L2 do not reach it |
|---|---|---|
| `mac/src/BetterTranslator.Mac.Shared/Shims/WindowsMediaBrush.cs` | `System.Windows.Media.Brush`, `System.Windows.Media.SolidColorBrush` | `MemoryViewModel`, `ActivityRowViewModel`, `MainWindowViewModel` and `SegmentItem` type a colour as a WPF brush. Avalonia's `IBrush` carries a member the framework marks not implementable by user code, and `Avalonia.Media.SolidColorBrush` is sealed, so the shim is a wrapper holding the Avalonia `IBrush` and its `Color`. The view layer unwraps it with `BrushShimConverter` |
| `mac/src/BetterTranslator.Mac.Shared/Shims/WindowsThreading.cs` | `System.Windows.Threading.Dispatcher`, `DispatcherOperation`, `DispatcherOperation<T>`, `DispatcherTimer` | `EntryViewModel`, `MainWindowViewModel`, `SkeletonGate`, `RuntimeStatusViewModel` and `ClockService` schedule on the WPF dispatcher. Each member forwards to `Avalonia.Threading.Dispatcher.UIThread` |
| `mac/src/BetterTranslator.Mac.Shared/Shims/WindowsApplication.cs` | `System.Windows.Application`, `System.Windows.IMainWindowSurface`, `System.Windows.WindowState` | `Tokens.Get<T>` resolves through `Application.Current.FindResource`; `AppGuiBridge` reads and sets the main window state. `FindResource` reads the Avalonia application resources and adapts an `ISolidColorBrush` into the brush shim so a token lookup typed as the WPF brush still returns one |
| `mac/src/BetterTranslator.Mac.Shared/Shims/WindowsClipboard.cs` | `System.Windows.Clipboard` | `EntryViewModel:1307` and `SettingsViewModel:468` copy text. Routed to `IClipboardSeam` |
| `mac/src/BetterTranslator.Mac.Shared/Shims/CollectionView.cs` | `System.ComponentModel.ICollectionView`, `ListCollectionView` | `ChatWorkspaceViewModel` holds four filtered views and calls `Filter` and `Refresh`. Avalonia has no collection view; the shim is a filtered projection that raises `CollectionChanged` so an `ItemsControl` can bind it |
| `mac/src/BetterTranslator.Mac.Shared/Shims/CollectionViewSource.cs` | `System.Windows.Data.CollectionViewSource` | the four views above are built from one |
| `mac/src/BetterTranslator.Mac.Shared/Shims/FileDialogs.cs` | `Microsoft.Win32.OpenFileDialog`, `SaveFileDialog`, `OpenFolderDialog` | `ChatWorkspaceViewModel`, `AddSourcesViewModel`, `FirstRunViewModel`, `MainWindowViewModel` and `SettingsViewModel` call `ShowDialog()` synchronously. The shim runs the async storage-provider seam and pumps a `DispatcherFrame` until it completes, which is the same mechanism WPF uses |
| `mac/src/BetterTranslator.Mac.Shared/Shims/IconDefinition.cs` | `BetterTranslator.App.Controls.IconDefinition`, `IconPart`, `IconPartCollection` | the original types are built on `System.Windows.Media.Geometry` and `System.Windows.Markup.ContentProperty`. The shim keeps the same names, namespace, property names and defaults on `Avalonia.Media.Geometry`, `PenLineCap`, `PenLineJoin` and `Avalonia.Metadata.Content` |
| `mac/src/BetterTranslator.Mac.Shared/Shims/UpdateNotificationsShim.cs` | `BetterTranslator.App.Notifications.UpdateNotifications.RemoveRegistration` | the original withdraws a Windows toast channel, a shell registration and a Start menu shortcut through `ole32`, `shell32` and the registry, none of which exist on macOS. The shim routes to `INotificationSeam.WithdrawRegistration` |

## Not consumed, replaced by a platform seam

These Windows implementations are neither linked nor mirrored. The wrapper supplies a macOS implementation of the same seam instead.

| Original | Replaced by |
|---|---|
| `src/BetterTranslator.App/Services/WindowFrame.cs` (`user32` window frame P/Invoke) | Avalonia window chrome in the head |
| `src/BetterTranslator.App/Services/AppInstance.cs` (Windows mutex and IPC) | `MacSingleInstance` |
| `src/BetterTranslator.App/Notifications/*.cs` (toast COM, shell registration, Start menu shortcut, registry) | `MacNotifications` |
| `src/BetterTranslator.App/App.xaml.cs` (WPF startup, toast verbs, cold activation) | `BetterTranslator.Mac.App.App` and `Program` |
| `src/BetterTranslator.App/MainWindow.xaml.cs` (WindowChrome hit testing, caption buttons, DWM) | the wrapper's window |
| `src/BetterTranslator.App/Views/*.xaml.cs` (WPF code-behind) | the wrapper's Avalonia views |
| `src/BetterTranslator.App/Themes/*.xaml`, `Verification/VerificationTokens.xaml` | transcribed token dictionaries under `mac/src/BetterTranslator.Mac.App/Themes/` and `Verification/`, values unchanged |
| `src/BetterTranslator.App/Controls/*.cs`, `Converters/*.cs`, `Verification/*.cs` | Avalonia ports under `mac/src/BetterTranslator.Mac.App/` |

## Blocked (L4)

Four items, listed with proposals in `blocked.md`: the native library resolver conflict in `BetterRuntime.cs`, the `kernel32` load path in `RuntimeSupport.cs`, the `.exe` host path in `HostedSession.cs`, and offscreen WPF capture on a non-Windows host.
