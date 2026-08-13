# Changelog

All notable changes to this project will be documented in this file.

## [0.1.0.2] - 2026-08-13

### Fixed
- **Brio Apply Tooltip Hover**: Enabled `ImGuiHoveredFlags.AllowWhenDisabled` on the context menu's "Apply to Brio Actor" option so tooltips ("Requires active GPose mode" / "No active actors found in Brio") render properly when the menu item is greyed out.

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
