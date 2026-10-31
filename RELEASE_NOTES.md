<!--
Copyright (C) 2026 Hyprism Launcher
SPDX-License-Identifier: GPL-3.0-only
-->

# Hyprism Together preview

This preview adds a native Windows Together screen to the REXCRAFT88 Hyprism fork.

## Included

- Up to four players with separate official Hytale accounts and distinct installed instances.
- Per-player controller selection, touchpad mouse, menu-to-Escape mapping, RGB color, and vibration test where supported.
- Selectable display, draggable player order, two-player side-by-side or stacked layout, three-player asymmetric layout, and four-player grid.
- Explicit profile-scoped asynchronous session lookup and game launch, with owner checks before launch.
- Bundled controller host and matching SDL runtime with native build source and notices.

## Preview limitations

The preview build and automated tests do not verify physical controller behavior, every SDL device alias, authenticated gameplay, or compatibility with every Hytale update. Each player needs an official account that owns Hytale, and the current UI requires a different installed instance per player and a shared game branch. Controller options depend on the device and driver. The feature arranges local windows and input only.
