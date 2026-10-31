<!--
Copyright (C) 2026 Hyprism Launcher
SPDX-License-Identifier: GPL-3.0-only
-->

# Hyprism Together preview

## Preview 3

- Corrected the Russian Together subtitle and refreshed its screenshot to describe one shared installation.

## Preview 2

- All players can launch from one installed game instance and branch.
- Account data uses the original Together layout under `%APPDATA%/Hytale/SplitScreen/Accounts/<player UUID>/<branch>/UserData`.
- Existing Together account folders are reused. Previous Hyprism instance data is not moved or merged automatically.
- Running game processes share an installation identity for update, repair, and deletion guards. A profile cannot be launched twice at the same time.
- Together refuses an incomplete, stale, or pending-update installation. It does not modify shared installation files while sibling games are running.

This preview adds a native Windows Together screen to the REXCRAFT88 Hyprism fork.

## Included

- Up to four players with separate official Hytale accounts and distinct installed instances.
- Per-player controller selection, touchpad mouse, menu-to-Escape mapping, RGB color, and vibration test where supported.
- Selectable display, draggable player order, two-player side-by-side or stacked layout, three-player asymmetric layout, and four-player grid.
- Explicit profile-scoped asynchronous session lookup and game launch, with owner checks before launch.
- Bundled controller host and matching SDL runtime with native build source and notices.

## Preview limitations

The preview build and automated tests do not verify physical controller behavior, every SDL device alias, authenticated gameplay, or compatibility with every Hytale update. Each player needs an official account that owns Hytale, and the current UI requires a different installed instance per player and a shared game branch. Controller options depend on the device and driver. The feature arranges local windows and input only.
