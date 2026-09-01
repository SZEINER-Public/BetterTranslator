# WPF surface inventory

## 1. WPF App inventory
### 1.1 Windows and UserControls
| File | Root type | x:Class | Backing ViewModel | Code-behind responsibility |
|---|---|---|---|---|
| `src/BetterTranslator.App/App.xaml` | `Application` | `BetterTranslator.App.App` | none, reaches `MainWindowViewModel` at `App.xaml.cs:68`, `:86` | startup verb dispatch, single instance, toast activation, IPC (208 lines) |
| `src/BetterTranslator.App/MainWindow.xaml` | `Window` | `BetterTranslator.App.MainWindow` | `MainWindowViewModel`, instantiated inline at `MainWindow.xaml:39-41` | chrome sync, panel width animation, backdrop and Escape routing (359 lines) |
| `src/BetterTranslator.App/Views/AddSourcesView.xaml` | `UserControl` | `BetterTranslator.App.Views.AddSourcesView` | `AddSourcesViewModel`, host binds at `MainWindow.xaml:1049` | `InitializeComponent` only (8 lines) |
| `src/BetterTranslator.App/Views/ChatView.xaml` | `UserControl` | `BetterTranslator.App.Views.ChatView` | `ChatWorkspaceViewModel`, bound at `MainWindow.xaml:523` | file drag and drop, drop-mark animation, composer and rename focus (149 lines) |
| `src/BetterTranslator.App/Views/FilePreviewView.xaml` | `UserControl` | `BetterTranslator.App.Views.FilePreviewView` | `FilePreviewViewModel`, bound at `MainWindow.xaml:759` | `InitializeComponent` only (8 lines) |
| `src/BetterTranslator.App/Views/FirstRunView.xaml` | `UserControl` | `BetterTranslator.App.Views.FirstRunView` | `FirstRunViewModel`, bound at `MainWindow.xaml:962` | `InitializeComponent` only (8 lines) |
| `src/BetterTranslator.App/Views/RetrievalMapView.xaml` | `UserControl` | `BetterTranslator.App.Views.RetrievalMapView` | `RetrievalMapViewModel`, bound at `MainWindow.xaml:608` | Skia paint pass, camera pan, zoom, fit, hit test, keyboard (238 lines) |
| `src/BetterTranslator.App/Views/SettingsView.xaml` | `UserControl` | `BetterTranslator.App.Views.SettingsView` | `SettingsViewModel`, bound at `MainWindow.xaml:970` | animates the dialog surface wider on the Config tab (85 lines) |
| `src/BetterTranslator.App/Views/SidebarView.xaml` | `UserControl` | `BetterTranslator.App.Views.SidebarView` | `ChatWorkspaceViewModel`, bound at `MainWindow.xaml:510`, same instance as ChatView | search sweep animation, toggle icon swap, Escape (92 lines) |

### 1.2 Custom controls (`src/BetterTranslator.App/Controls/`)
| Type | Base class | Kind | Role |
|---|---|---|---|
| `BilingualRow` | `Control` | templated control | source and target two-column transcript row |
| `ChromeButton` | `Button` | templated control | square icon button used across chrome and toolbar |
| `Disclosure` | static class | attached-property host | rotates a chevron 180 degrees when a disclosure opens |
| `ExpandArea` | `ContentControl` | height-animated container, no custom template | collapse and expand of an entry body |
| `ExpandPlan` | `readonly record struct` | pure value type | expand animation decision, zero WPF dependency |
| `IconDefinition`, `IconPart`, `IconPartCollection` | POCO | XAML-authored data model | square design box plus stroked or filled parts |
| `IconView` | `FrameworkElement` | custom-drawn (`OnRender`) | draws an `IconDefinition` in the inherited foreground |
| `MarkdownFlow` | static class | attached-property host | builds a `FlowDocument` onto a `RichTextBox` |
| `MarkdownInlines` | internal static class | helper | the single `MdInline` to WPF `Inline` mapper |
| `MarkdownTable` | static class | attached-property host | populates a `Grid` from an `MdTable` |
| `MarkdownText` | static class | attached-property host | fills `TextBlock.Inlines` from `MdInline` |
| `Menus` | static class | attached-property host plus behavior | row menu opener and destructive menu-item flag |
| `ResizeSeparator` | `Thumb` | templated control | draggable pane divider with keyboard resizing |
| `Reveal` | static class | behavior | fades an element in on first show, one live announcement |
| `SegmentedControl` | `ListBox` | templated control | fixed-width segments with a sliding thumb |
| `SegmentItem` | `ObservableObject` | view model | one segment of `SegmentedControl` |
| `SelectableCard` | static class | behavior | whole card toggles the `CheckBox` inside it |
| `SkeletonLines` | `Control` | templated control, bars built in code | streaming placeholder with a travelling opacity mask |
| `Sticky` | static class | behavior | pins an element while its scroller moves. Dead code, no usage anywhere |
| `ToggleSwitch` | `ButtonBase` | templated control | 30 x 17 track with a translating knob |
| `WheelChaining` | static class | behavior | hands the wheel to the outer scroller when travel runs out |
| `Wordmark` | `FrameworkElement` | custom-drawn (`OnRender`) | six-bar product mark on an 822 x 385 design grid |

### 1.3 Converters
| Type | Input to output | Resource key and where used |
|---|---|---|
| `BooleanToVisibilityConverter` (framework) | `bool` to `Visibility` | `BoolToVisibility` (`Themes/Controls.xaml:33`), every view |
| `InverseBooleanToVisibilityConverter` | `bool` to inverted `Visibility`, two-way | `InverseBoolToVisibility` (`:34`), MainWindow, ChatView, FirstRunView, RetrievalMapView, SettingsView, SidebarView |
| `StringEmptyToVisibilityConverter` | `string` to `Visibility`, visible while empty | `StringEmptyToVisibility` (`:35`), placeholder text in MainWindow, ChatView, SidebarView, FirstRunView |
| `ResourceKeyToBrushConverter` | resource-key `string` to `Brush` | `FlagBrush` (`:36`), `Controls.xaml:1487` `LanguageTile` against `Themes/Flags.xaml` |
| `ResourceKeyToIconConverter` | resource-key `string` to `IconDefinition` | `KeyToIcon` (`:37`), `MainWindow.xaml:1088`, `:1099` |
| `StringNotEmptyToVisibilityConverter` | `string` to `Visibility`, visible while non-empty | `NotEmptyToVisibility` (`:39`), MainWindow, ChatView, FirstRunView, SettingsView |
| `ZeroToVisibilityConverter` | `int` to `Visibility`, visible when zero | `ZeroToVisibility` (`:40`), `SettingsView.xaml:341` on `InstalledModels.Count` |
| `EqualToVisibilityConverter` (`IMultiValueConverter`) | two values to `Visibility` when equal | `EqualToVisibility` (`:41`), `ChatView.xaml:1321-1325` MultiBinding |

### 1.4 Resource dictionaries
| File | Lines | Holds | x:Keys |
|---|---|---|---|
| `src/BetterTranslator.App/Themes/Colors.xaml` | 77 | 48 `SolidColorBrush` | 48 |
| `src/BetterTranslator.App/Themes/Typography.xaml` | 337 | 32 `Style`, 16 `sys:Double`, 15 `FontWeight`, 2 `FontFamily` | 65 |
| `src/BetterTranslator.App/Themes/Metrics.xaml` | 309 | 110 `sys:Double`, 24 `Thickness`, 17 `CornerRadius`, 15 `Duration`, 4 `DropShadowEffect`, 2 `KeySpline`, 1 `Style` | 173 |
| `src/BetterTranslator.App/Themes/Icons.xaml` | 508 | 60 `IconDefinition`, 1 `FontFamily` (`FontFamilyCaptionGlyph`) | 61 |
| `src/BetterTranslator.App/Themes/Flags.xaml` | 1500 | 51 `DrawingBrush` | 51 |
| `src/BetterTranslator.App/Themes/Controls.xaml` | 1678 | 51 `Style`, 62 `ControlTemplate`, 3 `DataTemplate`, 1 `ItemsPanelTemplate`, 8 converter instances | 43 |
| `src/BetterTranslator.App/Themes/Markdown.xaml` | 139 | 8 implicit `DataTemplate`s keyed by `MdBlock` subtype | 0 |
| `src/BetterTranslator.App/Themes/Json.xaml` | 59 | implicit `JsonScalar` `DataTemplate` plus the `JsonRows` style | 1 |
| `src/BetterTranslator.App/Verification/VerificationTokens.xaml` | 14 | 2 `SolidColorBrush`, 2 `sys:Double`, 2 `DashStyle` | 6 |

Merge order is load bearing (`App.xaml:7-15`): Colors, Typography, Metrics, Icons, Flags, Controls, Markdown, Json, VerificationTokens.

### 1.5 Inline DataTemplates by view
No view file sets a `DataType` on any inline template; every one is an anonymous `ItemTemplate`. The only `DataType`-keyed templates in the project live in `Themes/Markdown.xaml` and `Themes/Json.xaml`.

| View file | DataType | Item type in practice | Renders |
|---|---|---|---|
| `MainWindow.xaml:412-447` | not set | `ProjectScopeViewModel` | project scope menu row with lock icon and detail |
| `MainWindow.xaml:629-651` | not set | `MemoryViewModel.RailRow` | colour swatch, trimmed label, count |
| `MainWindow.xaml:809-856` | not set | `EffortOptionViewModel` | advanced-panel effort radio card with a Get it link |
| `Views/ChatView.xaml:145-656` | not set | `EntryViewModel` | whole transcript row: `BilingualRow`, five source renderers, eight target renderers, metrics and actions footer |
| `Views/ChatView.xaml:386-412` | not set | `MemorySegmentViewModel` | plain run, or a memory word button with solid or dashed underline |
| `Views/ChatView.xaml:742-751` | not set | `string` | alternative word button |
| `Views/ChatView.xaml:898-948` | not set | `AttachmentViewModel` | attachment chip with preview, label, size, remove |
| `Views/ChatView.xaml:1164-1198` | not set | source language row | language menu row with tile, name, native name |
| `Views/ChatView.xaml:1270-1330` | not set | target language row | as above plus a reason line and a MultiBinding check mark |
| `Views/ChatView.xaml:1399-1436` | not set | `EffortOptionViewModel` | effort menu row with note and install hint |
| `Views/FirstRunView.xaml:84-232` | not set | `InstallItemViewModel` | install card, a retemplated `CheckBox` wrapping the whole card |
| `Views/FirstRunView.xaml:324-371` | not set | `InstallItemViewModel` | install progress row, status dot and bar |
| `Views/RetrievalMapView.xaml:92-117` | not set | `ReindexScope` | reindex scope menu row |
| `Views/RetrievalMapView.xaml:141-160` | not set | `PickableSourceViewModel` | pickable source with checkbox and chunk count |
| `Views/RetrievalMapView.xaml:250-275` | not set | `ActivityRowViewModel` | activity log line with a coloured dot |
| `Views/SettingsView.xaml:261-308` | not set | `CacheEntry` | cache row, wipes-memory badge, Delete |
| `Views/SettingsView.xaml:345-376` | not set | `InstalledModelRow` | installed model row with a gated Delete |
| `Views/SettingsView.xaml:457-513` | not set | `BackendOptionViewModel` | backend `ChoiceCard` plus a sibling fetch button |
| `Views/SettingsView.xaml:571-601` | not set | `CatalogueRow` | catalogue row with state and size |
| `Views/SettingsView.xaml:616-641` | not set | `ModelOptionViewModel` | model `ChoiceCard` |
| `Views/SettingsView.xaml:824-831` | not set | `ConfigFileViewModel` | config file tab button |

### 1.6 Inline Styles and Triggers by view
| View file | Key | TargetType | Trigger keyed on | Sets |
|---|---|---|---|---|
| `Views/ChatView.xaml:845-856` | implicit | `Border` | `DataTrigger` `IsDropTarget` == `True` | `Background=BrushAccentTint`, `BorderBrush=BrushAccent` |
| `Views/ChatView.xaml:1413-1419` | implicit, `BasedOn TextDefault` | `TextBlock` | `DataTrigger` `IsBlocked` == `True` | `Foreground=BrushTextMuted` |
| `Views/FilePreviewView.xaml:194-209` | implicit, `BasedOn SelectableMarkdownSyntax` | `TextBox` | `DataTrigger` `HasRenderingSwitch` == `False` | `FontFamilyUi`, `TextBodySize`, `BrushText` |
| `Views/FilePreviewView.xaml:194-209` | same style | `TextBox` | `DataTrigger` `IsMonospace` == `True` | `FontFamilyMono`, `TextMonoPathSize`. Declared second, so it wins |
| `Views/FirstRunView.xaml:97-101` | implicit `ControlTemplate` | `CheckBox` | none | template is a bare `ContentPresenter`, so the card is the control |
| `Views/FirstRunView.xaml:106-124` | implicit, `BasedOn SourceCard` | `Border` | `DataTrigger` `IsSelected` == `True` | `BrushAccentTintSoft`, `BrushAccentBorder` |
| `Views/FirstRunView.xaml:106-124` | same style | `Border` | `DataTrigger` `IsInstalledHere` == `True` | `BrushSurfaceSunken`, `BrushBorderFaint`, `TextElement.Foreground=BrushTextMuted`. Declared second, so it wins |
| `Views/FirstRunView.xaml:333-341` | implicit | `Ellipse` | `DataTrigger` `IsInstalled` == `True` | `Fill=BrushSuccess` |
| `Views/FirstRunView.xaml:354-363` | implicit, `BasedOn TextSecondary` | `TextBlock` | `DataTrigger` `IsInstalled` == `True` | `Foreground=BrushSuccessDeep` |
| `Views/SettingsView.xaml:891-900` | implicit, `BasedOn TextMeta` | `TextBlock` | `DataTrigger` `IsValid` == `False` | `Foreground=BrushDangerText` |

`MainWindow.xaml`, `AddSourcesView.xaml`, `RetrievalMapView.xaml` and `SidebarView.xaml` declare no inline style at all. No `Trigger` (property trigger), `MultiDataTrigger` or `EventTrigger` appears in any view file.

### 1.7 Attached properties declared in the App project
| Owner type | file:line | Property | Type |
|---|---|---|---|
| `Disclosure` | `Controls/Disclosure.cs:10` | `TurnsWhenOpen` | `bool` |
| `MarkdownFlow` | `Controls/MarkdownFlow.cs:26` | `Blocks` | `IEnumerable<MdBlock>` |
| `MarkdownTable` | `Controls/MarkdownTable.cs:11` | `Source` | `MdTable` |
| `MarkdownText` | `Controls/MarkdownText.cs:14` | `Inlines` | `IEnumerable<MdInline>` |
| `Menus` | `Controls/Menus.cs:20` | `OpensRowMenu` | `bool` |
| `Menus` | `Controls/Menus.cs:31` | `IsDanger` | `bool` |
| `Reveal` | `Controls/Reveal.cs:18` | `Target` | `bool` |
| `SelectableCard` | `Controls/SelectableCard.cs:20` | `TogglesSelection` | `bool` |
| `Sticky` | `Controls/Sticky.cs:9` | `WithinRegion` | `bool`, dead code |
| `Sticky` | `Controls/Sticky.cs:51` | `Scroll` | `ScrollViewer`, private, used as a per-element side table |
| `WheelChaining` | `Controls/WheelChaining.cs:23` | `Chains` | `bool` |
| `VerificationText` | `Verification/VerificationText.cs:9` | `Text` | `string` |
| `VerificationText` | `Verification/VerificationText.cs:15` | `Result` | `VerificationResult` |

### 1.8 WPF constructs needing deliberate translation, by view
| View | RelativeSource forms | DynamicResource | Virtualization | Other constructs |
|---|---|---|---|---|
| `App.xaml` | none | none | none | none |
| `MainWindow.xaml` | `FindAncestor` `Window` 508, 524, 609, 757, 800; `ItemsControl` 425, 834, 843 | none | none, both `ItemsControl`s unvirtualized | `WindowChrome` 31-37 with `UseAeroCaptionButtons=False`, `IsHitTestVisibleInChrome` 357; two `Popup`s, 133 `StaysOpen=False` and 395 `StaysOpen=True`; no Adorner, VisualStateManager, Storyboard, MultiBinding or `x:Static`; no text selection, only the Advanced `TextBox` 900 |
| `Views/AddSourcesView.xaml` | none | none | none | none of WindowChrome, Popup, Adorner, VisualStateManager, Storyboard, MultiBinding, `x:Static`, text selection |
| `Views/ChatView.xaml` | `FindAncestor` `ItemsControl` 155, 211, 324, 396, 448, 564, 574, 584, 597, 617, 916, 944, 1170, 1280, 1324; `ContextMenu` 495-524; `UserControl` 749-791; `Window` 831, 837, 1365, 1372, 1389, 1397, 1405; one `Self` at 491 | 490, 493, 497, 501, 505, 507, 513, 518, 520, 594, because menus live outside the visual tree | deliberately none. The entries `ItemsControl` 141 is unvirtualized, rationale in the comment at 26-38 | four `Popup`s 1003, 1116, 1206, 1383; one `ContextMenu` 489-526; one `MultiBinding` 1321-1325; `x:Static` at 88, 151, 154, 203, 213, 286, 315, 326, 623, 642, 651, 1057-1098, 1532; selection via read-only `TextBox` and `RichTextBox`, never `TextBlock`; no Adorner, VisualStateManager or Storyboard |
| `Views/FilePreviewView.xaml` | none | none | none | one `Popup` 65-90; selection via read-only `TextBox` 191 and `RichTextBox` 178; no WindowChrome, Adorner, VisualStateManager, Storyboard, MultiBinding, `x:Static` |
| `Views/FirstRunView.xaml` | `FindAncestor` `ItemsControl` 227 | none | none on either `ItemsControl` (82, 322) | one inline `ControlTemplate` 98-100, the only one in any view; no Popup, Adorner, VisualStateManager, Storyboard, MultiBinding, `x:Static`, text selection |
| `Views/RetrievalMapView.xaml` | `FindAncestor` `ItemsControl` 98; `Window` 302 | none | none on any of the three item lists | one `Popup` 72-187; `skia:SKElement` 205 with no `Background`, the paint pass clears the surface each frame; no WindowChrome, Adorner, VisualStateManager, Storyboard, MultiBinding, `x:Static`, text selection |
| `Views/SettingsView.xaml` | `FindAncestor` `ItemsControl` 304, 372, 460, 505, 618, 828; `UserControl` 954 | none | none on any of the six `ItemsControl`s | no Popup, WindowChrome, Adorner, VisualStateManager, Storyboard, MultiBinding, `x:Static`; selection via read-only `TextBox` 749, 783, 861; the surface resize is code-behind through `MotionService` |
| `Views/SidebarView.xaml` | none | none in this file | the one virtualizing surface in the app, inherited from `ChatRowList` at `Themes/Controls.xaml:763-768`: `IsVirtualizing=True`, `VirtualizationMode=Recycling`, `ScrollUnit=Pixel`, `CanContentScroll=True` | `ElementName` binding at 47; no Popup, WindowChrome, Adorner, VisualStateManager, Storyboard, MultiBinding, `x:Static`, text selection |

## 2. ViewModel coupling
One row per file in `src/BetterTranslator.App/ViewModels/` (30 files).

| File | Public types | Verdict | WPF types referenced, with lines | Platform seams needed |
|---|---|---|---|---|
| `ActivityRowViewModel.cs` | `record ActivityRowViewModel` | HEAVY | `System.Windows.Media` :1; `Brush` :11; `Tokens.Get<Brush>` :18, :19, :20, :25, :29 | theme token and brush lookup |
| `AddSourcesViewModel.cs` | `class AddSourcesViewModel` | LIGHT | `Microsoft.Win32` :5; `OpenFolderDialog` :177, :191; `OpenFileDialog` :205 | folder picker, file picker |
| `AttachmentViewModel.cs` | `class AttachmentViewModel` | FREE | none | filesystem read, `FileInfo` :56, :61 |
| `ConfirmDialogViewModel.cs` | `class ConfirmDialogViewModel` | FREE | none, icons carried as string keys :16-17, :35, :37 | none |
| `EntryViewModel.cs` | `class EntryViewModel` | HEAVY | `System.Windows` :3; `System.Windows.Threading` :4; `DispatcherTimer` :23, :923, :1230, :1237, :1320, :1327; `Application.Current?.Dispatcher` :1053; `CheckAccess` :1055; `BeginInvoke` :1061; `Clipboard.SetText` :1307 | clipboard, UI thread marshal, UI timers, theme tokens :1105, :1194, :1327 |
| `FilePreviewViewModel.cs` | `class FilePreviewViewModel` | FREE | none | none |
| `FileSlotState.cs` | `enum FileSlotState` | FREE | none | none |
| `FirstRunViewModel.cs` | `enum FirstRunStage`, `enum StartChoice`, `class FirstRunViewModel` | LIGHT | `Microsoft.Win32` :9; `OpenFolderDialog` :240 | folder picker, app-data paths :51, :162, :165, :227, :247, HTTP download, hardware probe :67 |
| `ChatNameAsk.cs` | `record ChatNameAsk` | FREE | none | none |
| `ChatRowViewModel.cs` | `class ChatRowViewModel` | FREE direct | none named, transitively WPF via `SkeletonGate` :21, :54 | UI timer |
| `ChatWorkspaceViewModel.cs` | `class ChatWorkspaceViewModel` | HEAVY | `System.ComponentModel` :2; `System.Windows.Data` :4; `Microsoft.Win32` :15; `CollectionViewSource` :45, :48, :1859; `ICollectionView` :93, :96, :297, :300, :1857; `OpenFileDialog` :446; `SaveFileDialog` :1517; `Refresh()` :103, :111, :145, :146, :615, :621, :1872, :1873 | file picker, save picker already seamed at :1483, drag and drop :439, :466, filesystem, filtered collection views |
| `InstallBatch.cs` | `record InstallBatch` | FREE | none | none |
| `InstallItemViewModel.cs` | `class InstallItemViewModel` | FREE | none | none |
| `MainWindowViewModel.cs` | `class MainWindowViewModel` | HEAVY | `System.Windows` :4; `System.Windows.Media` :5; `System.Windows.Threading` :6; `Microsoft.Win32` :23; `DispatcherTimer` :29, :30, :249, :256; `Tokens.Get<Brush>` :275; `Application.Current?.MainWindow?.DataContext` :395; `Dispatcher.BeginInvoke` and `Application.Current.Shutdown` :740; `OpenFolderDialog` :1057 | folder picker, app-data paths, single instance :732, process relaunch and exit :646, :739-740, :770-795, UI timers, theme tokens :249, :273-275, :606-626, MCP listener :394-411, backend resolve :443-446 |
| `MemorySegmentViewModel.cs` | `class MemorySegmentViewModel` | FREE | none | none |
| `MemoryViewModel.cs` | `record RailRow`, `class MemoryViewModel` | HEAVY | `System.Windows.Media` :2; `Brush` :13; `Tokens.Get<Brush>` :203, :204, :205, :206 | theme token and brush lookup |
| `PickableSourceViewModel.cs` | `class PickableSourceViewModel` | FREE | none | none |
| `ProjectScopeViewModel.cs` | `enum ProjectScopeKind`, `class ProjectScopeViewModel` | FREE | none | none |
| `RestartNoticeViewModel.cs` | `enum RestartNoticeState`, `class RestartNoticeViewModel` | FREE | none | restart trigger, already delegate-injected :21, :111, :114, :140 |
| `RetrievalMapViewModel.cs` | `enum MapFocus`, `class RetrievalMapViewModel` | FREE direct | none named, transitively WPF via `App.Controls.SegmentItem` :2, :32, :38, :44, :55, :169 | canvas repaint and fit events :221, :224 |
| `RuntimeStatusViewModel.cs` | `class RuntimeStatusViewModel` | LIGHT | `System.Windows` :2; `Application.Current?.Dispatcher` :50; `CheckAccess` :52; `BeginInvoke` :58 | UI thread marshal that must stay `BeginInvoke`, filesystem :103, :147, running-backend read :207-219 |
| `SamplerAdvisory.cs` | `static class SamplerAdvisory` | FREE | none | none |
| `SettingsViewModel.cs` | `enum SettingsTab`, `class ConfigFileViewModel`, `record CatalogueRow`, `record InstalledModelRow`, `class BackendOptionViewModel`, `class ModelOptionViewModel`, `class SettingsViewModel` | LIGHT | `System.Windows.Clipboard.SetText` :468; `System.Windows.Application.Current?.Shutdown()` :976; `Microsoft.Win32.OpenFolderDialog` :1011 | clipboard, folder picker, shell open :152, :159, :975, :1148, app-data paths, backend probe :703, :916 |
| `SkeletonGate.cs` | `class SkeletonGate` | LIGHT | `System.Windows.Threading` :1; `DispatcherTimer` :20, :130, :137; `.Tick` :138 | UI timer, theme tokens :38, :81 |
| `TargetLanguage.cs` | `record TargetLanguage` | FREE | none | none |
| `TranslationAsk.cs` | `record TranslationAsk` | FREE | none | none |
| `TranslationPhase.cs` | `enum TranslationPhase` | FREE | none | none |
| `TranslationSettingsViewModel.cs` | `class EffortOptionViewModel`, `class TranslationSettingsViewModel` | FREE | none | app-data paths :74, :258, download-manager route :74, :308, :321 |
| `UpdatesViewModel.cs` | `class UpdatesViewModel` | LIGHT | `System.Windows.Application.Current?.Shutdown()` :353 | update trigger, app-data paths, process shutdown, Windows service state :170, :382, :405, :425-436 |
| `WorkspaceMode.cs` | `enum WorkspaceMode` | FREE | none | none |

### Distinct WPF types across all ViewModels
| WPF type | Files that use it |
|---|---|
| `System.Windows.Media.Brush` | `ActivityRowViewModel.cs` :1, :11, :18-20, :25, :29; `MemoryViewModel.cs` :2, :13, :201, :203-206; `MainWindowViewModel.cs` :5, :275 |
| `System.Windows.Threading.DispatcherTimer` | `EntryViewModel.cs` :4, :23, :923, :1230, :1237, :1320, :1327; `MainWindowViewModel.cs` :6, :29, :30, :249, :256; `SkeletonGate.cs` :1, :20, :130, :137 |
| `Application.Current.Dispatcher` with `CheckAccess` and `BeginInvoke` | `EntryViewModel.cs` :1053, :1055, :1061; `RuntimeStatusViewModel.cs` :50, :52, :58; `MainWindowViewModel.cs` :740 |
| `Application.Current.Shutdown(int)` | `MainWindowViewModel.cs` :740; `SettingsViewModel.cs` :976; `UpdatesViewModel.cs` :353 |
| `Application.Current.MainWindow.DataContext` | `MainWindowViewModel.cs` :395 |
| `System.Windows.Clipboard.SetText` | `EntryViewModel.cs` :1307; `SettingsViewModel.cs` :468 |
| `Microsoft.Win32.OpenFileDialog` | `AddSourcesViewModel.cs` :5, :205; `ChatWorkspaceViewModel.cs` :15, :446 |
| `Microsoft.Win32.OpenFolderDialog` | `AddSourcesViewModel.cs` :177, :191; `FirstRunViewModel.cs` :9, :240; `MainWindowViewModel.cs` :23, :1057; `SettingsViewModel.cs` :1011 |
| `Microsoft.Win32.SaveFileDialog` | `ChatWorkspaceViewModel.cs` :1517 |
| `System.Windows.Data.CollectionViewSource` | `ChatWorkspaceViewModel.cs` :4, :45, :48, :1859 |
| `System.ComponentModel.ICollectionView` | `ChatWorkspaceViewModel.cs` :2, :93, :96, :297, :300, :1857 |
| indirect, `Services/Tokens.cs` over `Application.Current.FindResource` | `ActivityRowViewModel.cs`, `MemoryViewModel.cs`, `MainWindowViewModel.cs`, `EntryViewModel.cs`, `SkeletonGate.cs` |
| indirect, `Controls.SegmentItem` and `Controls.IconDefinition`, which carry `Brush` and `Geometry` | `MainWindowViewModel.cs` :7, :245, :260, :267, :269, :274, :284, :290, :936, :1104; `RetrievalMapViewModel.cs` :2, :32, :38, :44, :55, :169 |

## 3. Engine project portability
### 3.1 Per project
| Project | TargetFramework | RID plumbing | PackageReferences | ProjectReferences |
|---|---|---|---|---|
| `BetterTranslator.Core` | `net10.0` (`.csproj:4`) | none | `Microsoft.Data.Sqlite` 10.0.10 (:8), `Microsoft.Extensions.DependencyInjection.Abstractions` 10.0.10 (:9), `SQLitePCLRaw.lib.e_sqlite3` 2.1.12 (:14) | none |
| `BetterTranslator.Engine` | `net10.0` (:4) | none | `Markdig` 1.3.2 (:33), `WeCantSpell.Hunspell` 7.0.1 (:34) | Core (:38) |
| `BetterTranslator.Indexing` | `net10.0` (:4) | none | `DocumentFormat.OpenXml` 3.5.1 (:8), `Markdig` 1.3.2 (:9), `PdfPig` 0.1.15 (:10) | Core (:14), Engine (:15) |
| `BetterTranslator.Runtime` | `net10.0` (:4) | none. Instead a `$(BetterRuntimeDir)`-gated `None` copy of `BetterRuntimeCPU.dll` (:11, :16) | `ModelContextProtocol` 2.1.0 (:26) | Core (:30), Indexing (:35), Engine (:36) |
| `BetterTranslator.Map` | `net10.0` (:4) | none | `SkiaSharp` 4.151.0 (:8), with no `SkiaSharp.NativeAssets.*` of any kind | Core (:12) |
| all five, shared | `Directory.Build.props:11` sets `PlatformTarget=x64`, `:12` sets `InvariantGlobalization=false` | the only RID anywhere in the repo is `win-x64` (`build.ps1:116, 128`; `build/SingleFile.targets:7, 75, 77, 83`) | | |

### 3.2 Windows-only findings
| Project | file:line | Category | What it does | Reachability |
|---|---|---|---|---|
| all five | `Directory.Build.props:11` | build target | `PlatformTarget=x64` on every assembly | (b) reachable and would break on an arm64 host |
| all five | `build.ps1:116, 128`, `build/SingleFile.targets:7` | RID | only `win-x64` is plumbed, no macOS publish path exists | (b) reachable and would break |
| all five | `Directory.Build.props:19` | backslash path | `TrimEnd('\')` on `MSBuildThisFileDirectory` | (c) no-op on macOS, consumers accept the trailing separator |
| Core | `Services/Restart/RelaunchTarget.cs:152, 155, 158` | DllImport | `GetCommandLineW`, `CommandLineToArgvW`, `LocalFree` | (a) inert, guarded by `!OperatingSystem.IsWindows()` at :92 |
| Core | `Services/Restart/RelaunchTarget.cs:23, 61` | case-insensitive path | `OrdinalIgnoreCase` on an absolute path prefix and on equality | (c) matches the APFS default, wrong on a case-sensitive volume |
| Core | `Services/Restart/RelaunchTarget.cs:35, 66` | extension assumption | rejects a managed entry point by its `.dll` suffix | (c) under `dotnet App.dll` the muxer has no suffix, so `OuterArguments()` at :94 drops the assembly path and the relaunch fails as `RestartOutcome.RelaunchFailed` |
| Core | `Services/AppPaths.cs:17` | SpecialFolder | `LocalApplicationData` roots the db, models, caches and dictionaries (:71-89) | (c) resolves to `~/.local/share/BetterTranslator`, not the macOS convention |
| Core | `Services/InstallLocationStore.cs:156`; `Services/Restart/RestartService.cs:206` | file locking, string | a `FileShare.None` write probe, and the message `"Windows did not start {target.Executable}."` | (c) the probe becomes an advisory `flock` on Unix and is harmless; the message names the wrong OS |
| Engine | `.csproj:13-25` | backslash path | nine `EmbeddedResource Include="Data\..."` | (c) MSBuild normalizes, and the manifest names are dotted |
| Engine | `Corpus/CorpusSelector.cs:97` | case-insensitive path | every corpus include and exclude glob matched `IgnoreCase` by design | (c) over-matches on a case-sensitive volume |
| Engine | `Models/ModelStores.cs:100`, `Verification/DictionaryStore.cs:59` | case-insensitive path | `OrdinalIgnoreCase` sets dedupe absolute store and dictionary folders | (c) two case-distinct directories collapse to one |
| Engine | `Models/ModelStores.cs:31, 42, 46` | SpecialFolder, drive letter | `UserProfile` inside a `PlatformNotSupportedException` catch, then `HOMEDRIVE` plus `HOMEPATH`, then `USERPROFILE` | (a) `:31` returns `$HOME` and the function returns at :35, so the two drive-letter fallbacks are unreachable and unset anyway |
| Engine | `Verification/Signals/LexicalSignals.cs:69-83` | process launch flag | `ProcessStartInfo` with `UseShellExecute=false`, `CreateNoWindow=true`, redirected streams | (a) inert by default, built only when both configured paths exist; a macOS-native `majka` would work |
| Engine | `Slop/TranslationCandidate.cs:26`, `Slop/Glossary.cs:293`, `Markup/MarkupGuard.cs:64-65`, `Markup/SourceResidue.cs:110` | backslash path | `[A-Za-z]:\\\S+` regexes | (a) content heuristics over translated text, never path construction |
| Indexing | whole project, and `Readers/*.cs:9-20` | none, then case-insensitive path | no registry, WPF, `System.Drawing`, WinForms, `DllImport`, backslash path, SpecialFolder, process launch, pipe, mutex or file lock. Only the `.json`, `.pdf`, `.txt`, `.docx` and markdown extension tests use `OrdinalIgnoreCase` | (a) the project is otherwise clean; (c) the extension tests are benign |
| Runtime | `Inference/BetterRuntime.cs:198-258` | DllImport | 18 `[DllImport("BetterRuntime")]` entry points resolved through `SetDllImportResolver` at :82-83 | (b) reachable and would break, the central blocker |
| Runtime | `Inference/BetterRuntime.cs:136` | extension assumption | `Path.Combine(dir, flavor + ".dll")`, the only extension ever probed | (b) reachable and would break; no `.dylib` is tried, so every call ends at `DllNotFoundException` (:165) |
| Runtime | `Inference/RuntimeSupport.cs:141-142, 144-145, 170` | DllImport | `LoadLibraryExW`, `AddDllDirectory`, `GetHRForLastWin32Error`, with no OS guard and no try or catch | (a) inert on the normal path, (b) if a caller reaches the public `RuntimeSupport.Open` directly |
| Runtime | `Inference/RuntimeBackend.cs:80-82` | extension assumption | `BetterRuntimeCUDA.dll`, `BetterRuntimeVulkan.dll`, `BetterRuntimeCPU.dll` | (b) reachable and would break, no `.dylib` mapping |
| Runtime | `Inference/RuntimeBackend.cs:111, 112, 119, 120` | DllImport probe | `CanLoad("nvapi64.dll", "nvcuda.dll", "atiadlxx.dll", "amdhip64.dll", "vulkan-1.dll")` | (a) inert, `NativeLibrary.TryLoad` returns false and `HardwareReport` becomes all false. No Metal probe exists |
| Runtime | `Inference/RuntimeSupport.cs:31`; `Inference/HostedSession.cs:70`; `Inference/InferenceHost.cs:44` | extension assumption | the MSVC redistributable list, `"BetterTranslator.Host.exe"`, `"betterruntime.exe"` | (a) the redistributable list is inert, used only from the unreachable `Open`; (b) both hosted-inference routes are reachable and silently unavailable |
| Runtime | `Inference/HostedSession.cs:100-110`, `Inference/InferenceHost.cs:69-85` | process launch flag | `ProcessStartInfo` with `UseShellExecute=false`, `CreateNoWindow=true` | (a) inert, both gated on a `.exe` that cannot exist |
| Runtime | `Inference/HostedSession.cs:89-94` | named pipe | `NamedPipeServerStream`, cross-platform overload, no `PipeSecurity` | (a) inert; it would work over a Unix domain socket if a mac host binary existed |
| Runtime | `Downloads/ArtifactLock.cs:63-69, 90` | file locking | `FileShare.None` plus `FileOptions.DeleteOnClose`, and a case-folded lock identity | (c) advisory `flock` on Unix, `DeleteOnClose` is emulated, and case-distinct artifact names share one lock |
| Runtime | `Inference/ModelLibrary.cs:48, 59, 91`, `Inference/LocalTranslator.cs:181`, `Inference/ModelSelection.cs:76`, `Inference/ChatNamer.cs:52`, `Downloads/ModelResolver.cs:164`, `Agents/TranslationGateway.cs:178, 184, 195, 217`, `Inference/RuntimeBackend.cs:69` | case-insensitive path | dictionaries and comparisons keyed by a full model or folder path with `OrdinalIgnoreCase` | (c) case-distinct real files collapse on a case-sensitive volume |
| Runtime | `Downloads/ArtifactManifest.cs:60, 61, 67, 80, 87` | extension assumption | the download catalogue offers Windows PE binaries only | (b) reachable and would break |
| Runtime | `Downloads/ArtifactManifest.cs:149-152, 176-183` | extension assumption | `IsWindowsLibrary` rejects any `.dll` whose first two bytes are not `4D 5A` | (c) a Mach-O artifact named `.dll` would be rejected |
| Runtime | `Downloads/ModelResolver.cs:121` | extension assumption | `Path.Combine(AppContext.BaseDirectory, component.FileName)` where `FileName` is a `.dll` | (c) always false on macOS, so runtimes report as never installed |
| Runtime | `.csproj:16` | backslash path | `$(BetterRuntimeDir)\BetterRuntimeCPU.dll` | (c) MSBuild normalizes and the whole item group is gated |
| Map | `Painting/MapPainter.cs:51, 65, 66, 99, 119-122, 146, 157-159, 179-182, 219, 224, 232, 234` | native asset | `SKCanvas`, `SKPaint`, `SKFont`, `SKPathEffect`, `SKPathBuilder` all need `libSkiaSharp` | (b) reachable and would break, no `SkiaSharp.NativeAssets.macOS` is referenced and the only RID plumbed is `win-x64` |
| Map | `Painting/MapPainter.cs:65, 219, 232` | font metrics | `SKTypeface.Default` resolves through CoreText on macOS | (c) glyph metrics differ from the Windows layout the constants at :40-45 were tuned against |
| Map | `Camera/MapCamera.cs:8`, `Painting/MapPainter.cs:33-35` | System.Windows | comments name `SKElement` and WPF elements | (c) comments only, the assembly has no WPF reference |

None of the five projects contains registry access, a `System.Windows` or `System.Drawing` or WinForms type, `WindowsIdentity`, `ServiceBase`, `EventLog`, WMI, a window handle, a named `Mutex`, a `Global\` prefix, or a `MemoryMappedFile`.

## 4. Accelerator-aware surfaces
### 4.1 Backend selection and hardware probing
| Type | Member | file:line | Produces |
|---|---|---|---|
| `RuntimeBackend` (enum) | `Cpu`, `Vulkan`, `Cuda` | `Core/Models/RuntimeBackend.cs:11-21` | the persisted flavour choice |
| `HardwareReport` | the record and `VendorLabel` | `Runtime/Inference/RuntimeBackend.cs:12`, `:15-21` | capability probe result and the vendor sentence |
| `BackendOption` | the record, `CanSelect`, `BlockedReason` | `Runtime/Inference/RuntimeBackend.cs:25-31`, `:39`, `:41-46` | one backend as settings and the installer see it |
| `BackendCatalog` | `Probe()`, `CanLoad(string)` | `Runtime/Inference/RuntimeBackend.cs:109-121`, `:187-196` | a `HardwareReport` by `NativeLibrary.TryLoad` over five vendor libraries |
| `BackendCatalog` | `FileNameFor`, `DependenciesFor`, `Options`, `Recommend`, `IsInstalled`, `Resolve`, `IsSupported`, `SearchPaths`, `SearchAlso` | `:78-83`, `:98-102`, `:123-134`, `:141-149`, `:160-163`, `:170-175`, `:177-182`, `:65`, `:67-73` | the three flavour file names, the two cuBLAS companions for CUDA, always three options, the folder list probed, and the flavour that will actually be used |
| `RuntimeSupport` | `BackendOf`, `RequiredBeside`, `MissingBeside`, `CarryInto`, `Open`, `LoadScoped`, `Describe` | `Runtime/Inference/RuntimeSupport.cs:41-44`, `:35-39`, `:46-47`, `:49-81`, `:95-111`, `:153-174`, `:113-136` | flavour classification, the VC++ modules carried beside it, a `FlavorFinding`, and the user-facing failure sentence |
| `Native` | `ResolveFlavor()`, `PreferredFlavor`, `LoadedFlavor`, `Substitution`, `Explain` | `Runtime/Inference/BetterRuntime.cs:114-166`, `:90`, `:96`, `:108`, `:173-196` | probe order `[chosen,] CUDA, Vulkan, CPU` at :122-124, and the substitution sentence with the cuBLAS clause at :191-192 |
| `ModelParams` | `NGpuLayers` | `Runtime/Inference/BetterRuntime.cs:47` | the offload knob |
| `LocalTranslator` | `Backend`, `Prefer`, `PreferFlavor`, `Flavor`, `LoadedFlavor`, `FlavorNote`, `ResolvedFlavor`, `GpuLayers` | `Runtime/Inference/LocalTranslator.cs:95`, `:97-102`, `:109-113`, `:116`, `:127`, `:130`, `:143`, `:160` | current and loaded flavour, the note, and `_backend == Cpu ? 0 : -1` |
| `ComponentCatalog` | `RuntimesFor(HardwareReport)`, `CudaLibraries`, `NoCuda`, `NoVulkan` | `Runtime/Models/ComponentCatalog.cs:121-156`, `:176-194`, `:202`, `:204` | three runtime rows (CPU :125, Vulkan :136, CUDA :148), the companions, the unsupported reasons |
| `ModelResolver`, `HostRequest`, `HostResponse` | `Resolve`, `WithDependencies`, `Presence.Explain`; `GpuLayers`, `Flavor`, `SearchPaths`, `FlavorNote` | `Runtime/Downloads/ModelResolver.cs:100-172`, `:181-199`, `:58-73`; `Runtime/Inference/HostProtocol.cs:25`, `:28`, `:35`, `:78`, `:81` | `MissingDependencies` presence when the companions are absent, and the child-host load request and reply |
| `TranslationGateway`, `AppSettings`, `SettingsStore` | `ApplyBackend`, `RuntimeBackend`, key `runtime_backend` | `Runtime/Agents/TranslationGateway.cs:131-140`; `Core/Models/AppSettings.cs:31`; `Core/Services/SettingsStore.cs:19`, `:61-64`, `:107` | applies the resolved backend at :138, and persists the choice, default `Cpu` |

### 4.2 ViewModel members exposing a backend, hardware, GPU state, or an install or repair flow
| Member | file:line |
|---|---|
| `SettingsViewModel.Backends`, `.HardwareLabel`, `.SelectedBackend`, `.RunningBackend`, `.NeedsRestart` | `App/ViewModels/SettingsViewModel.cs:504`, `:513-514`, `:516-517`, `:526-527`, `:534` |
| `SettingsViewModel.LoadRuntimeTab(AppSettings)` | `:701-754`, with `Probe()` at :703 and `HardwareLabel` at :704 |
| `SettingsViewModel.GetFlavourCommand`, `.ChooseBackendCommand`, `.RestartCommand`, `.ShowRuntimeCommand`, `.IsRuntimeTab` | `:763-773`, `:775-784`, `:963-977`, `:691-692`, `:365` |
| `SettingsViewModel.RemoveBlockedReason(ModelComponent)`, `.DataMessage`, `.HasDataMessage` | `:849-870`, `:834-839` |
| `SettingsViewModel.AllComponents()`, `.InstalledPath(ModelComponent)`, `.Catalogue`, `CatalogueRow.StateLabel` | `:915-916`, `:928-941`, `:510`, `:212-218` |
| `SettingsViewModel.OnSelectedBackendChanged`, `.OnRunningBackendChanged`, the write at `settings.RuntimeBackend` | `:1165-1170`, `:1172`, `:1235` |
| `BackendOptionViewModel` whole type: `Option`, `Backend`, `Title`, `Summary`, `CanSelect`, `IsRecommended`, `BlockedReason`, `HasBlockedReason`, `IsChosen`, `CanFetch`, `FetchLabel`, `Refresh` | `App/ViewModels/SettingsViewModel.cs:234-276` |
| `RuntimeStatusViewModel.BackendLabel`, `.BackendNote`, `.HasBackendNote` | `App/ViewModels/RuntimeStatusViewModel.cs:207-221`, `:227`, `:229` |
| `RuntimeStatusViewModel._backend` constructor parameter, `RaiseState()` raising the three, and the `StateDetail` failure branch that carries `RuntimeSupport.Describe` text | `:27`, `:32`, `:36`, `:315-317`, `:125` |
| `FirstRunViewModel` constructor `BackendCatalog.Probe()`, `HardwareLabel` assignment, `RuntimesFor(hardware)` into `Items` | `App/ViewModels/FirstRunViewModel.cs:66`, `:67`, `:69`, `:88` |
| `FirstRunViewModel.HardwareLabel` get-only property, declared and bound nowhere in XAML | `:174` |
| `FirstRunViewModel.UseDefaultFolderCommand`, `.PickFolderCommand`, both calling `BackendCatalog.SearchAlso` | `:225-231` (`:228`), `:237-258` (`:254`) |
| `FirstRunViewModel.SelectedBytes`, `.InstallLabel`, `.DownloadSizeLabel`, which price `InstallBytes` including cuBLAS | `:193-194`, `:203`, `:206` |
| `InstallItemViewModel.IsBlocked`, `.BlockedReason`, `.HasBlockedReason`, `.CanDeselect`, `.SelectByDefault()`, the blocked guard, and `.IsIncomplete`, `.PresenceNote`, `.HasPresenceNote` for the cuBLAS-missing state | `App/ViewModels/InstallItemViewModel.cs:113`, `:115`, `:117`, `:125`, `:99-100`, `:184-195`, `:68`, `:70-73` |
| `MainWindowViewModel.RunningBackend`, pushed into Settings, plus the resolve chain `SearchAlso`, `Resolve`, `Prefer` | `App/ViewModels/MainWindowViewModel.cs:320`, `:339`, `:443`, `:445`, `:446` |
| `MainWindowViewModel.OpenDownloadsCommand`, `.OpenDownloadManager()` | `:861-880`, `:917` |
| `UpdatesViewModel` | no backend, hardware, GPU or driver member exists in the file |

### 4.3 XAML elements rendering them
| file:line | Element |
|---|---|
| `Views/SettingsView.xaml:52-55`, `:442-445` | the Runtime rail `Button` bound to `ShowRuntimeCommand`, and the Runtime pane `ScrollViewer` gated on `IsRuntimeTab` |
| `Views/SettingsView.xaml:452`, `:455` | two `Run`s carrying `HardwareLabel` plus the trailing sentence, then `ItemsControl ItemsSource="{Binding Backends}"` |
| `Views/SettingsView.xaml:459-463` | `Button Style=ChoiceCard` bound to `ChooseBackendCommand`, `CanSelect`, `Title` |
| `Views/SettingsView.xaml:470`, `:471-475`, `:476-480`, `:482-488` | the `Title`, `Summary`, `BlockedReason` (gated on `HasBlockedReason`) and `Recommended` (gated on `IsRecommended`) `TextBlock`s |
| `Views/SettingsView.xaml:503-511` | the fetch `Button`, `FetchLabel` and `GetFlavourCommand`, gated on `CanFetch` |
| `Views/SettingsView.xaml:517-538` | the `NeedsRestart` `Border` and its Restart now `Button` at :531-536 |
| `Views/SettingsView.xaml:569`, `:580`, `:581-585`, `:587-592`, `:593-598` | the `Catalogue` `ItemsControl` and its `Name`, `Note`, `StateLabel`, `SizeLabel` cells |
| `Views/SettingsView.xaml:343`, `:365-374`, `:384-395` | the `InstalledModels` `ItemsControl`, its Delete `Button`, and the `HasDataMessage` `Border` |
| `MainWindow.xaml:162-169`, `:206-217` | the Runtime and `Runtime.BackendLabel` row, and the `Runtime.HasBackendNote` border, in the status popup |
| `MainWindow.xaml:270-275` | `ChromeButton x:Name="DownloadsButton"` bound to `OpenDownloadsCommand` |
| `Views/FirstRunView.xaml:82`, `:92-93`, `:137-146` | the `Items` `ItemsControl` holding the runtime rows, and the outer and indicator `CheckBox` |
| `Views/FirstRunView.xaml:175-189`, `:191-195`, `:202-207`, `:211-216`, `:287-288`, `:310-315` | the `PresenceNote` tooltip border, `Summary`, `BlockedReason`, `SizeLabel`, `DownloadSizeLabel`, and the primary install `Button` |
| `Themes/Controls.xaml:1391` | `Style x:Key="ChoiceCard" TargetType="Button"`, the backend card chrome |

### 4.4 User-facing strings naming an accelerator
| Exact string | file:line |
|---|---|
| `"NVIDIA and AMD graphics"` :17, `"NVIDIA graphics"` :18, `"AMD graphics"` :19, `"No discrete graphics detected"` :20 | `Runtime/Inference/RuntimeBackend.cs:17-20` |
| `"This machine cannot run it"` :43, `"Not downloaded yet"` :44 | `Runtime/Inference/RuntimeBackend.cs:43-44` |
| `"CPU"` and `"Works on any machine. Best for short phrases."` :130, `"Vulkan"` and `"GPU acceleration for AMD, Intel and NVIDIA."` :131, `"CUDA"` and `"NVIDIA only. Fastest where it runs."` :132 | `Runtime/Inference/RuntimeBackend.cs:130-132` |
| `" The CUDA build needs cublas64_13.dll and cublasLt64_13.dll beside it; these ship with the runtime, not with the NVIDIA driver."` | `Runtime/Inference/BetterRuntime.cs:192` |
| `$"{wanted}.dll was chosen but is not installed, so {loaded}.dll is running instead."` :184, `$"{wanted}.dll is installed but could not be loaded, so {loaded}.dll is running instead.{missing} ({reason})"` :195 | `Runtime/Inference/BetterRuntime.cs:184`, `:195` |
| `"BetterRuntime (CPU)"` :127, `"Runs the models on the processor. Works on any machine."` :128 | `Runtime/Models/ComponentCatalog.cs:127-128` |
| `"BetterRuntime (AMD Vulkan)"` :138, `"Runs the models on the graphics card. Much faster than the processor."` :139, `"BetterRuntime (NVIDIA CUDA)"` :150, `"Runs the models on an NVIDIA GPU. Fastest where it runs."` :151 | `Runtime/Models/ComponentCatalog.cs:138-139`, `:150-151` |
| `"NVIDIA cuBLAS, which the CUDA runtime calls for matrix multiplication."` :183, `"NVIDIA cuBLASLt, which cublas64_13.dll itself imports."` :191 | `Runtime/Models/ComponentCatalog.cs:183`, `:191` |
| `"This machine cannot run it - no NVIDIA graphics detected."` :202, `"This machine cannot run it - no Vulkan-capable graphics detected."` :204 | `Runtime/Models/ComponentCatalog.cs:202`, `:204` |
| `"BetterRuntimeCUDA" => "NVIDIA CUDA",` :209, `"BetterRuntimeVulkan" => "AMD Vulkan",` :210, `"BetterRuntimeCPU" => "CPU",` :211 | `App/ViewModels/RuntimeStatusViewModel.cs:209-211` |
| `RuntimeBackend.Cuda => "NVIDIA CUDA",` :217, `RuntimeBackend.Vulkan => "AMD Vulkan",` :218, `_ => "CPU",` :219 | `App/ViewModels/RuntimeStatusViewModel.cs:217-219` |
| `(false, false, _) => "Cannot run here",` | `App/ViewModels/SettingsViewModel.cs:215` |
| `$"{component.Name} is the runtime this session is using, and Windows will not delete a library that is loaded. Pick another runtime above, restart, then remove this one."` | `App/ViewModels/SettingsViewModel.cs:858-859` |
| `$"{component.Name} is the only runtime installed. Removing it would leave nothing able to translate, so install another one first."` | `App/ViewModels/SettingsViewModel.cs:867-868` |
| `" found on this machine. A flavour your hardware cannot run stays unavailable."` | `App/Views/SettingsView.xaml:452` |
| `"Restart BetterTranslator to use this. A runtime already loaded cannot be swapped while the app is open."` | `App/Views/SettingsView.xaml:527` |
| `Text="Runtime"` :163; `AutomationProperties.Name="Models and runtimes"` and `ToolTip="Models and runtimes - what is installed and what can be downloaded"` :274-275 | `App/MainWindow.xaml:163`, `:274-275` |
| `$"{component.Name} needs {companion.FileName} beside it and there is no link configured for it, so installing would leave a runtime that cannot load."` :67-68, `$"{companion.FileName} could not be installed: {beside.Failure}"` :84 | `Runtime/Downloads/DownloadManager.cs:67-68`, `:84` |
| `$"{name} is here but cannot load without {string.Join(" and ", Missing)}, so the rest will be downloaded."` | `Runtime/Downloads/ModelResolver.cs:65` |
| `"No translation runtime is installed. Looked in "` plus `". Open Models and runtimes and install the CPU runtime."` | `Runtime/Inference/RuntimeSupport.cs:118-120` |
| none, 25 entries and none match | `App/Resources/Strings.resx` |

### 4.5 What a CPU-only wrapper must omit
| Kind | Items | file:line |
|---|---|---|
| properties | `Backends`, `HardwareLabel`, `SelectedBackend`, `RunningBackend`, `NeedsRestart` | `App/ViewModels/SettingsViewModel.cs:504`, `:513-514`, `:516-517`, `:526-527`, `:534` |
| properties | the whole `BackendOptionViewModel` type | `App/ViewModels/SettingsViewModel.cs:234-276` |
| properties | `CatalogueRow.IsSupported` and the `"Cannot run here"` arm of `StateLabel` | `App/ViewModels/SettingsViewModel.cs:200`, `:215` |
| properties | `BackendLabel`, `BackendNote`, `HasBackendNote`, and the `_backend` constructor parameter | `App/ViewModels/RuntimeStatusViewModel.cs:207-221`, `:227`, `:229`, `:27`, `:32`, `:36` |
| properties | `FirstRunViewModel.HardwareLabel`, already bound nowhere | `App/ViewModels/FirstRunViewModel.cs:174` |
| properties | `IsBlocked`, `BlockedReason`, `HasBlockedReason`, `IsIncomplete` | `App/ViewModels/InstallItemViewModel.cs:113`, `:115`, `:117`, `:68` |
| properties | `MainWindowViewModel.RunningBackend` | `App/ViewModels/MainWindowViewModel.cs:320` |
| properties | `ModelComponent.Companions`, `.UnsupportedReason`, `.IsSupported`, and the companion term of `.InstallBytes` | `Runtime/Models/ModelComponent.cs:91`, `:82`, `:85`, `:101` |
| commands | `GetFlavourCommand`, `ChooseBackendCommand`, `RestartCommand`, `ShowRuntimeCommand` | `App/ViewModels/SettingsViewModel.cs:763-773`, `:775-784`, `:963-977`, `:691-692` |
| view elements | the whole Runtime pane; the Runtime rail entry; the `HardwareLabel` `Run` pair; the `Backends` `ItemsControl` and its template; `BlockedReason`; the `Recommended` label; the fetch button; the `NeedsRestart` notice; the `StateLabel` cell | `Views/SettingsView.xaml:441-541`, `:52-55`, `:452`, `:455-515`, `:476-480`, `:482-488`, `:503-511`, `:517-538`, `:587-592` |
| view elements | the Runtime and `BackendLabel` row and the `BackendNote` border in the status popup; the install card `BlockedReason` | `MainWindow.xaml:162-169`, `:206-217`; `Views/FirstRunView.xaml:197-207` |
| strings | every string in 4.4 except the CPU ones. Must not appear: `"NVIDIA and AMD graphics"`, `"NVIDIA graphics"`, `"AMD graphics"`, `"No discrete graphics detected"`, `"This machine cannot run it"`, `"Not downloaded yet"`, `"Vulkan"`, `"GPU acceleration for AMD, Intel and NVIDIA."`, `"CUDA"`, `"NVIDIA only. Fastest where it runs."`, `"BetterRuntime (AMD Vulkan)"`, `"Runs the models on the graphics card. Much faster than the processor."`, `"BetterRuntime (NVIDIA CUDA)"`, `"Runs the models on an NVIDIA GPU. Fastest where it runs."`, `"NVIDIA cuBLAS, which the CUDA runtime calls for matrix multiplication."`, `"NVIDIA cuBLASLt, which cublas64_13.dll itself imports."`, `"This machine cannot run it - no NVIDIA graphics detected."`, `"This machine cannot run it - no Vulkan-capable graphics detected."`, `"NVIDIA CUDA"`, `"AMD Vulkan"`, `"Cannot run here"`, `" found on this machine. A flavour your hardware cannot run stays unavailable."`, `"Restart BetterTranslator to use this. A runtime already loaded cannot be swapped while the app is open."`, `"cublas64_13.dll"`, `"cublasLt64_13.dll"`, `"BetterRuntimeCUDA.dll"`, `"BetterRuntimeVulkan.dll"`, `"nvapi64.dll"`, `"nvcuda.dll"`, `"atiadlxx.dll"`, `"amdhip64.dll"`, `"vulkan-1.dll"` | see 4.4 |
| strings that survive | `"CPU"`, `"Works on any machine. Best for short phrases."`, `"BetterRuntime (CPU)"`, `"Runs the models on the processor. Works on any machine."`, `"BetterRuntimeCPU"` | see 4.4 |

### 4.6 What it must keep
| Area | Members | file:line | XAML anchor |
|---|---|---|---|
| effort tiers | `TranslationEffort`; `EffortTier`, `EffortSelection`, `EffortTiers` with `.Simple`, `.Thinking`, `.All`, `.Default`, `.Resolve`, `.NothingInstalledNotice` | `Core/Models/TranslationEffort.cs:11-18`; `Core/Models/EffortTiers.cs:3`, `:5`, `:10`, `:14-22`, `:55-80`, `:26-27` | `Views/ChatView.xaml:831`, `:837` |
| effort tiers | `TranslationSettingsViewModel.Efforts`, `.SelectedEffort`, `.Effort`, `.EffortLabel`, `.HasSelectedEffort`, `.SelectionNotice`, `.IsEffortMenuOpen` | `App/ViewModels/TranslationSettingsViewModel.cs:89`, `:91-92`, `:95`, `:97`, `:99`, `:101-102`, `:196-197` | `Views/ChatView.xaml:1397`, `:1372`, `:831`, `:1389`; `MainWindow.xaml:807` |
| effort tiers | `ToggleEffortMenuCommand`, `ChooseEffortCommand`, `GetModelCommand`; `EffortOptionViewModel.Label`, `.Note`, `.IsInstalled`, `.IsChosen`, `.IsBlocked`, `.InstallHint`, `.BlockedTooltip`, `.RadioNote`, `.Model` | `:294-295`, `:301-314`, `:317-322`; `:22`, `:25`, `:27-28`, `:34-35`, `:37`, `:40`, `:43`, `:49-51`, `:109-110` | `Views/ChatView.xaml:1365`, `:1405`, `:1403-1433`; `MainWindow.xaml:834`, `:843`, `:828-853` |
| model selection | `SettingsViewModel.Models`, `.SelectedModel`, `.HasModels`, `.ModelsFolderLabel`, `ChooseModelCommand`, `RescanModelsCommand` | `App/ViewModels/SettingsViewModel.cs:507`, `:519-520`, `:536`, `:543-545`, `:794-799`, `:801-810` | `Views/SettingsView.xaml:614`, `:648`, `:608`, `:618`, `:652` |
| model selection | `ModelOptionViewModel.Name`, `.Publisher`, `.SizeLabel`, `.IsChosen`; `SettingsViewModel.InstalledModels`, `DeleteModelCommand`, `.ModelsTotalLabel`, the model rows of `.Catalogue` | `:283`, `:285`, `:287`, `:289`; `:577`, `:872-912`, `:582-583`, `:510` | `Views/SettingsView.xaml:627`, `:628`, `:634`, `:343`, `:365-374`, `:331`, `:569` |
| model selection | `RuntimeStatusViewModel.ModelLabel`, `.HasModel`; `MainWindowViewModel.ResolveModelPath()` and `Runtime.SelectionNote` | `App/ViewModels/RuntimeStatusViewModel.cs:235-237`, `:103`; `App/ViewModels/MainWindowViewModel.cs:546-567`, `:564` | `MainWindow.xaml:175`, `:254-265` |
| model selection | `FirstRunViewModel.Items` model rows, `AddCustomModelCommand`, `RemoveCustomModelCommand`, `CustomModelLink`, `LinkRejection` | `App/ViewModels/FirstRunViewModel.cs:88`, `:260-286`, `:288-299`, `:117-118`, `:121-122` | `Views/FirstRunView.xaml:82`, `:251-253`, `:259-264`, `:266-271`, `:218-228` |
| thread count, context size | `ModelParams.NThreads`, commented `0 = hardware concurrency`, never assigned anywhere in the tree; `LocalTranslator.ContextTokens = 4096`, a `private const` applied at `:268`, sent at `HostedSession.cs:135`, read at `HostProtocol.cs:23`, used at `Host/Program.cs:308`; `BetterRuntimeModel.ContextSize` read back at `BetterRuntime.cs:295`, `:317` | `Runtime/Inference/BetterRuntime.cs:48`; `Runtime/Inference/LocalTranslator.cs:36` | neither has a ViewModel member or a XAML anchor. Keep both fields |
| sampler settings | `TranslationSettingsViewModel.Temperature`, `.TemperatureReadout`, `.TemperatureIsAdjustable`, `.TemperatureNote`, `.UserPrompt`, `.UserPromptPlaceholder`, `ResetToDefaultsCommand` | `App/ViewModels/TranslationSettingsViewModel.cs:200-201`, `:247-248`, `:234-235`, `:237-240`, `:213-214`, `:250-251`, `:324-334` | `MainWindow.xaml:886`, `:865`, `:887`, `:889-890`, `:907`, `:914`, `:909`, `:926`; step token `Themes/Metrics.xaml:224` used at `MainWindow.xaml:883-884` |
| sampler settings | `SamplerSettings`, `SamplerConfigGuard.Recommended`, `.AppliesTo`, `.Validate`, `SamplerAdvisory.Describe`, `MainWindowViewModel.ReportSamplerAdvisories`, `RuntimeStatusViewModel.ConfigAdvisories`, `.HasConfigAdvisories`; `TranslationJob.Sampling()` with `MaxTokens`, `Temperature`, `TopP`, `TopK`, `RepeatPenalty`, `Seed` and the matching `HostRequest` fields | `Runtime/Verification/SamplerConfigGuard.cs:5`, `:14-20`; `App/ViewModels/SamplerAdvisory.cs:26-39`; `App/ViewModels/MainWindowViewModel.cs:487-519`; `App/ViewModels/RuntimeStatusViewModel.cs:250-255`; `Runtime/Inference/TranslationJob.cs:141-186`, defaults `:211-216`; `Runtime/Inference/HostProtocol.cs:47-57` | `MainWindow.xaml:247`, `:248` |

## 5. Screens the parity harness should register
| Screen id | Source view file | State | ViewModel state that produces it |
|---|---|---|---|
| `window-shell` | `MainWindow.xaml` | normal, maximized | `WindowState`, drives the inset at `MainWindow.xaml.cs:358` and the Restore glyph at `:341-352` |
| `window-panels` | `MainWindow.xaml` | sidebar, preview and right panel each open, closed or mid-animation | `IsSidebarVisible`, `IsRightPanelVisible`, `Workspace.Preview` open state, and the three width properties |
| `workspace-mode` | `MainWindow.xaml` | simple, advanced, memory | `WorkspaceMode` through `MainWindowViewModel.IsMemoryMode` |
| `project-scope` | `MainWindow.xaml` | unset (selector at `MainWindow.xaml:325`), set (chip at `:349`), menu closed, menu open, locked row | project scope null or set, `IsProjectMenuOpen`, `ProjectScopes` with `IsLocked` and `LockedReason` |
| `runtime-status-popup` | `MainWindow.xaml` | closed, open, each of the four notice borders present or absent, and the can start, can pause, can unload button permutations | `Runtime.IsPanelOpen`, `HasBackendNote`, `HasPromptWarning`, `HasConfigAdvisories`, `HasSelectionNote`, `CanStart`, `CanPause`, `CanUnload` |
| `advanced-panel` | `MainWindow.xaml` | populated, effort blocked, prompt empty | `Translation.Efforts`, `EffortOptionViewModel.IsBlocked`, `Translation.UserPrompt` |
| `size-readout-pill` | `MainWindow.xaml` | visible, hidden | `ReportWindowSize` plus the hide timer at `MainWindowViewModel.cs:249` |
| `chat-transcript` | `Views/ChatView.xaml` | empty, populated, loading skeleton, error | `Entries` count, `EntryViewModel` phase, `SkeletonLines` while streaming, `HasFailed` with Retry |
| `chat-entry-renderers` | `Views/ChatView.xaml` | word, sentence, markdown flow, JSON rows, syntax, verified, marked memory segments | the mutually exclusive renderer flags on `EntryViewModel` |
| `chat-entry-file-slot` | `Views/ChatView.xaml` | queued, progress, loading skeleton, done | `FileSlotState` |
| `chat-entry-menus` | `Views/ChatView.xaml` | context menu open, row menu open | `ContextMenu` at `ChatView.xaml:489-526`, `Menus.OpensRowMenu` at `:596` |
| `chat-header` | `Views/ChatView.xaml` | idle, renaming, metrics shown, metrics hidden, regenerate offer shown or dismissed | `ChatNameEditor` visibility, `ShowMetrics`, `HasChatMetrics`, `RegenerateOfferText` non-empty |
| `chat-composer` | `Views/ChatView.xaml` | empty with placeholder, typed, sending, drop target | composer text, `IsGenerating`, `IsDropTarget` |
| `chat-attachments` | `Views/ChatView.xaml` | none, few, overflowing, attach menu closed, attach menu open | `Attachments` count against the capped strip scroller, and the attach `Popup` at `ChatView.xaml:1003` |
| `chat-language-picker` | `Views/ChatView.xaml` | source open, target open, search empty, filtered, unavailable rows | `SourceLanguagesView` and `LanguagesView` filters, `IsAvailable` and `Reason` |
| `chat-effort-menu` | `Views/ChatView.xaml` | closed, open with a blocked item | `Translation.IsEffortMenuOpen`, `EffortOptionViewModel.IsBlocked` |
| `memory-word-popover` | `Views/ChatView.xaml` | closed, open with alternatives, own-word field open | `OpenMemoryWord`, `Alternatives` |
| `sidebar-chat-list` | `Views/SidebarView.xaml` | empty, populated, pinned present, pinned collapsed, long list virtualized | `HasChats`, `HasPinned`, `PinnedView`, `ChatsView` |
| `sidebar-search` | `Views/SidebarView.xaml` | closed, mid sweep, open, typed | `IsSearchOpen` and the search text |
| `file-preview` | `Views/FilePreviewView.xaml` | closed, source version, translated version, formatted, raw, monospace, translating, run note | `HasRenderingSwitch`, `IsMonospace`, `IsTranslating`, `HasRunNote`, `IsVersionMenuOpen` |
| `memory-overview` | `MainWindow.xaml` | empty, populated idle, indexing, rail populated, learned empty | `Memory.IsEmpty`, `Memory.ProjectSources`, indexing progress at `MainWindow.xaml:716` |
| `retrieval-map` | `Views/RetrievalMapView.xaml` | empty, populated, fit, panned, zoomed, node hovered, mid drag, activity empty, activity populated | `IsEmpty`, `World`, `MapCamera`, `HoveredNode`, `HasActivity`, `Activity` |
| `retrieval-map-reindex-menu` | `Views/RetrievalMapView.xaml` | closed, stage 1 scopes, stage 2 pick sources | `IsPickingSources`, `ReindexScopes`, `PickableSources` |
| `add-sources` | `Views/AddSourcesView.xaml` | no chats with two cards disabled, nothing selected, one selected, multi selected | `HasChats`, the five selection flags, `CanStart` and `BlockedReason` |
| `first-run-choose` | `Views/FirstRunView.xaml` | default folder, picked folder, item selected, item installed here, item blocked, custom link rejected | `IsChoosing`, `IsUsingDefaultFolder`, `InstallItemViewModel.IsSelected`, `.IsInstalledHere`, `.IsBlocked`, `LinkRejection` |
| `first-run-installing` | `Views/FirstRunView.xaml` | loading with per-row progress, installed | `IsInstalling`, `InstallItemViewModel.IsInstalled` and `Percent` |
| `first-run-start` | `Views/FirstRunView.xaml` | just chat enabled, repository locked, folder locked | `IsChoosingStart` |
| `settings-memory` | `Views/SettingsView.xaml` | populated | `IsMemoryTab` |
| `settings-your-data` | `Views/SettingsView.xaml` | default folder, custom folder, folder moved, models empty, models populated, delete confirming | `IsDataTab`, `HasCustomDataFolder`, `DataFolderMoved`, `InstalledModels.Count`, `IsConfirmingDeleteEverything` |
| `settings-runtime` | `Views/SettingsView.xaml` | selectable, blocked, recommended, fetchable, needs restart | `IsRuntimeTab`, `BackendOptionViewModel.CanSelect`, `.BlockedReason`, `.IsRecommended`, `.CanFetch`, `NeedsRestart` |
| `settings-downloads` | `Views/SettingsView.xaml` | catalogue populated, no models found | `IsDownloadsTab`, `HasModels` |
| `settings-agent` | `Views/SettingsView.xaml` | MCP off, MCP on, token warning | `IsAgentTab`, `McpEnabled`, `McpNeedsToken`, `McpStatus` |
| `settings-config` | `Views/SettingsView.xaml` | valid, invalid (error), edited, surface widened | `IsConfigTab` widens the surface, `SelectedConfig.IsValid`, `.IsEdited` |
| `settings-updates` | `Views/SettingsView.xaml` | idle, checking (loading), download available, downloading, ready, interaction locked | `IsUpdatesTab`, `Updates.IsWorking`, `.UpdateFound`, `.IsDownloading`, `.UpdateReady`, `.CanInteract` |
| `restart-notice` | `MainWindow.xaml` | counting down, held | `RestartNoticeState` through `RestartNoticeViewModel` |
| `confirm-dialog` | `MainWindow.xaml` | open | `ActiveDialog`, set by the `showDialog` delegate given to `ChatWorkspaceViewModel` |
| `update-toast` | `MainWindow.xaml` | download available, downloading, ready | `Updates.ShowNotice`, `.IsDownloading`, `.UpdateReady` |
