# Changelog

All notable changes to this project will be documented in this file.

## [0.1.0.8] - 2026-10-03

### Fixed
- **Brio v0.8.0.12 Compatibility**: Resolved reflection failure caused by disposed `IServiceProvider` instances by referencing `UIManager.Instance` and `ModalManager.Instance` directly.
- **Assembly Lifecycle Re-binding**: Traversed active assemblies in reverse order during reflection initialization so the plugin binds to the active Brio runtime rather than any stale or disposed instances.
- **Nested Child Window Stack**: Implemented native tracking hooks for `igBeginChild_Str`, `igBeginChild_ID`, `igBeginPopupModal`, and `igEndPopup` with a per-frame stack, preventing inner widgets from prematurely closing the library info pane.
- **Injected Mode Layout & Scrollbar Elimination**: Sized the injected preview canvas to the remaining pane space after header rendering and added margin padding, eliminating vertical scrollbar overflow and right-edge clipping.
- **Legacy .cmp Parsing Guard**: Prevented unhandled deserialization errors when inspecting legacy `.cmp` files in Brio's library.

## [0.1.0.7] - 2026-10-03

### Added
- **Anamnesis & Universal Rig Bone Alias Support**: Added automatic translation for English bone names (`Waist`, `SpineA/B/C`, `Neck`, `Head`, `ArmLeft/Right`, `LegLeft/Right`, etc.) and external rig naming conventions (`Pelvis`, `Hips`, `LeftArm`, `LeftLeg`, `Spine1-3`, `Chest`, etc.) to native game bone identifiers (`j_*`, `n_*`), enabling 3D mannequin previews for Anamnesis poses.
- **Partial Pose & Prop Fallback Centering**: Added root/pelvis fallback cascading (`j_kosi` -> `j_sebo_a` -> `j_sebo_b` -> `n_hara` -> `n_root` -> centroid) so partial poses and accessory/prop poses center and auto-frame correctly.
- **Prop & Isolated Bone Fallback Rendering**: If a pose contains bones but no humanoid mannequin limbs can be formed (such as prop poses or hand-only poses), all active visible bones render as joint spheres to ensure the preview is never empty.
- **Delimiter Resilient Vector Parsing**: Enhanced vector and quaternion parsing to support space-, tab-, and comma-delimited transform values.

## [0.1.0.6] - 2026-09-16

### Changed
- **Window Collapse Support**: Removed `ImGuiWindowFlags.NoCollapse` from the Settings window and Brio integration settings renderer to enable native title bar collapse buttons.

## [0.1.0.5] - 2026-08-19

### Fixed
- **Game Freeze on Startup / Directory Scan**: Moved folder structure scanning to an asynchronous background task (`Task.Run`), preventing game freezes when opening `/posemanager` or loading large pose folders.
- **Linux / Proton Symlink Loop Crash**: Added canonical path tracking (`visited` HashSet) and recursion depth limits to prevent infinite recursion loops caused by Wine drive mappings (e.g. `Z:\` / `dosdevices` loops).
- **Cross-Platform Default Path**: Changed default pose directory from hardcoded `D:\...` to dynamic user `MyDocuments/Brio/Poses` directory, avoiding OS I/O timeouts on systems without a `D:` drive.
- **Native ImGui Pointer Safety**: Capped string reads in `GetUtf8String` to 512 bytes with exception handling to prevent infinite loops on corrupt or non-null-terminated native pointers.

## [0.1.0.4] - 2026-08-19

### Added
- **Brio Standalone Library Support**: Expanded 3D mannequin pose preview from modal-only ("Import Pose -> From File") to also support the standalone Brio Library window.
- **Brio File Browser Preview**: Added preview support for Brio's "Browse for File" dialogs (`Import Poses`), automatically attaching a live 3D mannequin preview snapped to the file browser.
- **Smart Screen-Boundary Snapping**: In Attached Mode, the preview window automatically flips and snaps to the left side of Brio if moved near the right edge of the screen, keeping it visible and within viewport bounds.
- **Quick Settings Access**: Added an interactive Settings Cog button to both Injected and Attached preview modes.

### Fixed
- **Modal Interactivity & Layering**: Fixed Settings window dimming and input blocking during Brio modal sessions and file browsing dialogs, ensuring settings remain in the foreground and fully interactive.
- **Off-Screen Child Pane Culling**: Decoupled Attached preview and Settings rendering from Brio's inner child panes to top-level window completion (`igEnd`), preventing preview disappearance when Brio is moved partially off-screen.

## [0.1.0.3] - 2026-08-13

### Fixed
- **Disabled .cmp Direct Brio Import**: Disabled "Apply to Brio Actor" for legacy Concept Matrix (`.cmp`) files to prevent character model/bone distortion (turning into a ball). Added hover tooltips and info panel guidance directing users to "Use Brio Pose Import".
- **Brio Reflection Initialization Retry**: Removed permanent `failed` lock in `BrioReflectionHelper` so reflection initialization retries dynamically if Brio was temporarily uninitialized or loaded after PoseManager.

## [0.1.0.2] - 2026-08-13

### Fixed
- **Brio Apply Tooltip Hover**: Enabled `ImGuiHoveredFlags.AllowWhenDisabled` on the context menu's "Apply to Brio Actor" option so tooltips render properly when the menu item is greyed out.

## [0.1.0.1] - 2026-07-08

### Added
- **Auto-Frame Viewport Quick-Toggle**: Automatically adjusts camera target and zoom to fit the character mannequin perfectly within the viewport canvas.
- **Auto-Focus & Auto-Reset viewports**: Added quick-toggle options to center the camera on character chest level and zero position/rotation offsets when loading a pose.
- **Fading Intensity Slider**: Custom slider in settings to control maximum depth shading opacity clamps.
- **Show in Windows Explorer**: Right-click context option on folders, files, and preview images to inspect them in Explorer.
- **Settings UX**: Enabled scrollbars and added a minimum window size constraint `(400, 300)`.

### Fixed
- **Erratic Depth Fading**: Anchored depth fog calculations to the pelvis bone (`j_kosi`) rather than camera zoom, preventing wireframe flickering during camera orbits.
- **Absolute Pose Offsets**: Centered mannequin root coordinates by dynamically subtracting the pelvis bone coordinate during projection, resolving centering bugs for absolute coordinate exports (like `dominated.pose`).
- **Dynamic Proportions (Anti-Chibi Fix)**: scaled joints and limb lines by the viewport height to maintain thin, sleek proportions across all window sizes.
- **Brio Integration Parity**: Fully synced auto-framing, auto-focusing, auto-resetting, coordinate centering, and fading intensity settings into the Brio Library Preview windows.

## [0.1.0.0-beta] - 2026-06-28

### Added
- **Initial Beta Release**: First public testing release of PoseManager for FINAL FANTASY XIV.
- **Interactive 3D Skeleton Viewport**: Full interactive mannequin rendering for previewing character poses before applying them.
- **Brio Integration**: Direct integration to preview and apply poses seamlessly into Brio.
