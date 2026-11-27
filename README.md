# Inscryption VR

A fan-made VR mod for **Inscryption** (Act 1 and Kaycee's Mod), played through **SteamVR** with your own two hands.

You sit at Leshy's table in the headset. Pick up cards, slap the bell, pay blood with the dagger, screw the totem together, flip through the rule book, walk around the cabin, and type your name on the death card. Everything goes through the game's own rules and save files: the mod never patches game code.

> **Early release.** The mod is developed on a Quest 3 over Steam Link and on a Steam Frame. Many features are new and haven't been tested on other headsets yet. If something breaks, please [open an issue](../../issues) with your `BepInEx/LogOutput.log`.

## Features

- **Stereo rendering** of the cabin, the sanctum, the finale walk, the main menu and the Kaycee's Mod menu. The game keeps running on the monitor, which can mirror the headset view.
- **Hands.** You get the game's own hands, or translucent hologram hands. Fingers follow grip, trigger and thumb touch, and anything you can grab at the table glows when you reach for it (you can turn the glow off).
- **Cards.** Your hand lies face up in an overlapping row on the table and spills into a second row when it gets long. Grab a card between finger and thumb, drop it over a lane to play it, drag it across the row to reorder, pass it between hands, and pull new cards off the draw piles. A ghost or glow in the lane shows where the card will land.
- **Sacrifices.** Pick up the dagger from the table and stab or slash your creatures to pay blood. The dagger sticks in the table and can carve. With eye tracking, Leshy watches the victim and your hand feels a heartbeat.
- **Items by motion.** Bring the pliers to your mouth, slam bottles on the table, turn the hourglass over, hold the magnifier to your eye, wind the watch, wave the fan, swing the hammer.
- **Table toys.** Lift cards on the board, poke the scales and pick up the teeth, warm your hand over the candles, throw bone tokens, screw the totem head onto its base.
- **Events.** Turn over and pull reward cards, carry deck cards onto altars, plunge a two-handed dagger at the mysterious stones, warm both hands at the campfire, socket an eyeball, knock on boulders, and carry your figurine across the map. "Simplified events" switches the elaborate gestures off.
- **Cabin.** Stand up from the table, walk the cabin with the stick, turn safe dials and clock hands with your wrist.
- **Dialogue.** Leshy's lines float above his head, either as a panel or in the game's own pixel font. Optional auto-advance paced to each line's length, wave a hand to skip, and a toggle for the black bars.
- **Extras.** Leshy looks at you while he talks and notices when you stare (with eye tracking). Bosses get a close-up intro, boss scenery stretches into the distance, and the moon fight drops away into a starry void.
- **Settings panel.** A floating settings panel in the pause menu applies every change at once.
- **Performance presets** for standalone headsets (Balanced / Standalone), half-framerate mode and live render quality.

## Requirements

- Windows PC with the **Steam** or **Epic** version of Inscryption.
- **SteamVR** and a PC VR headset (Quest via Steam Link, Air Link or Virtual Desktop; Index; Vive; Steam Frame, and others). Controllers with grip, trigger and a thumbstick.
- **BepInEx 5.4 (x86)**. The "with-BepInEx" download includes it.

## Install

1. Download the latest zip from the [Releases](../../releases) page:
   - `InscryptionVR-<version>-with-BepInEx.zip` if you've never modded Inscryption.
   - `InscryptionVR-<version>-plugin-only.zip` if you already have BepInEx 5 x86 installed.
2. Open the game folder. In Steam: right-click Inscryption → **Manage** → **Browse local files**.
3. Extract the zip **into that folder**, so `winhttp.dll` and the `BepInEx` folder sit next to `Inscryption.exe`.
4. Start SteamVR and put the headset on, **then** launch the game. If SteamVR is running, the game starts in VR; if not, it runs normally on the monitor, with a reminder in the corner of the title screen. VR starts at the title screen and can't be started later in the same session.
5. Optional, in Steam: right-click Inscryption → **Properties** → **Launch Options**. `-vr` always starts in VR (and launches SteamVR if needed); `-novr` always starts on the monitor. Set `Rendering.LaunchMode = FlagOnly` in the config to start VR only with `-vr`.

To uninstall, delete `BepInEx/plugins/InscryptionVR`. To remove BepInEx as well, also delete `winhttp.dll`, `doorstop_config.ini` and the `BepInEx` folder.

## Controls

The **main hand** is your right hand, or your left with **Left-handed** on. The other hand is the **off hand**.

| Action | How |
| --- | --- |
| Grab a card, item or event object | Touch it and squeeze grip, or point at it and squeeze |
| Play a card | Let go over one of your lanes |
| Reorder your hand | Let go of a card over the row |
| Pay blood | Hold the card in one hand, take the dagger with the other, and stab or slash your creatures |
| End your turn | Slap the bell from above |
| Advance dialogue | Pull and release either trigger |
| Skip dialogue | Wave an empty hand quickly side to side, or flap your wrist |
| Open the rule book | Grip the book on the table. Swipe with the other hand or flick a stick to turn pages. Let go, or press B, to close it |
| Pause / back in menus | Left Y or the menu button (right B when left-handed) |
| Stand up from the map | Main-hand stick down |
| Walk the cabin | Off-hand stick, relative to where you look |
| Turn / interact ahead / sit down | Main-hand stick left/right turns; up interacts or sits back down |
| Leave deck review | Main-hand stick down, or either trigger |
| Menus on the floating screen | Point and pull the trigger; B/Y goes back |

| Key | Action |
| --- | --- |
| F8 | Recenter the seat to where you're facing |
| F9 | Stop VR rendering until the game restarts |
| F10 | Turn hand interactions and the pointer on or off |

SteamVR's recenter and your headset's own recenter both work.

## Settings

Pause the game in VR to open the settings panel beside the pause menu. Point and pull the trigger, or poke it with a fingertip. Press a number to reset it to its default.

| Tab | Settings |
| --- | --- |
| Hands | Hand style (game or hologram), flip game hands, hand color, size, angle and opacity, left-handed, grab reach, table stops hands, card glow, interactable glow, laser |
| Table | Lane preview, card row distance and overlap, card hover, simplified events, sacrifice shake, marker and heartbeat, card info panel, rule book size, bell slap speed, game haptics, voice rumble |
| View | Seat height, world size, pose prediction, snap turn, boss close-up, boss scenery rows, moon void, menu screen size and distance, eye tracking, recenter |
| Audio & Text | 3D audio, subtitles, subtitle style and height, auto-advance pace, wave to skip, Leshy looks at you / notices stare / watches victim, black bars, tips |
| Performance | Optimization preset, half framerate, render quality, monitor shows VR, timing log |

Everything else, including switches for each individual system, is in `BepInEx/config/local.inscryption.vr.cfg`. It's created on first launch.

## Troubleshooting

- **Nothing shows in the headset.** Check that SteamVR was running before the game started (or add `-vr` to the launch options), and that `-novr` isn't in them. Playing standalone under Proton, use `-vr`, since the game can't see SteamVR's processes there. Look in `BepInEx/LogOutput.log` for lines starting with `[Info   :Inscryption VR]`.
- **Low framerate, or playing standalone on a Steam Frame.** Set **Optimization** to Balanced or Standalone in the Performance tab and lower Render quality. Turn on **Timing log** to write per-frame timings to the log.
- **The view is too high, too low or the wrong size.** Change **Seat height** and **World size** in the View tab, then recenter with F8 or your headset's recenter.
- **Hands feel misaligned.** Adjust **Hand angle** and **Hand size** in the Hands tab, or switch Hand style.
- **Bug reports.** Please attach `BepInEx/LogOutput.log` and name your headset and how it's connected. Turning on `Diagnostics.VerboseLogging` in the config makes the log more detailed.

## Building from source

You need Windows, PowerShell 5.1+ and Inscryption installed. The managed plugin builds with the .NET Framework `csc.exe` that ships with Windows (C# 5), so no Visual Studio or .NET SDK is required.

```powershell
.\scripts\bootstrap.ps1
.\scripts\bootstrap-openvr.ps1
.\scripts\build.ps1
.\scripts\build-native.ps1
.\scripts\test-controls.ps1
.\scripts\install-local.ps1
```

| Script | What it does |
| --- | --- |
| `bootstrap.ps1` | Downloads BepInEx 5.4.23.2 x86 into `.dependencies` (SHA-256 checked) |
| `bootstrap-openvr.ps1` | Downloads OpenVR v2.12.14 `openvr_api.dll` and its C# bindings (SHA-256 checked) |
| `build.ps1` | Builds `InscryptionVR.dll` against your installed game |
| `build-native.ps1` | Downloads portable Zig 0.13.0 and builds the native renderer `InscryptionVR.Native.dll` and the eye-tracking helper `InscryptionVR.Eyes.exe` |
| `test-controls.ps1` | Offline checks: input state machines, the OpenVR ABI, hand-model renders, and every reflected game member against your installed game |
| `test-native.ps1` | Runs the native renderer against a mock OpenVR runtime |
| `install-local.ps1` | Copies the build into the game (backs up the old plugin, config and log to `artifacts/installed-backups`) |
| `package.ps1` | Builds both release zips and `SHA256SUMS.txt` in `artifacts/release` |
| `collect-runtime.ps1` | Copies logs and config into `artifacts/runtime-snapshots` for debugging |

The scripts find the Steam or Epic install on their own; pass `-GamePath` for any other location. Game assemblies are referenced from your install and are never copied into the repository.

### Project layout

| Folder | Contents |
| --- | --- |
| `src/InscryptionVR/Core` | Plugin entry point, config, settings panel, scene list, shared game hooks |
| `src/InscryptionVR/Rendering` | Stereo renderer, per-eye image effects, performance presets, timing |
| `src/InscryptionVR/Input` | Controller sampling, pointer, haptic mixer, eye tracking, input shims for game prompts |
| `src/InscryptionVR/Hands` | Hand model, hologram and game hands, grab glow, carrying |
| `src/InscryptionVR/Battle` | Card row, grabbing, piles, lanes, dagger and sacrifices, items, rule book, table toys |
| `src/InscryptionVR/Events` | Map and event interactions (rewards, altars, stones, campfire, eyeballs) |
| `src/InscryptionVR/Scenes` | Cabin walking and dials, sanctum, finale, boss close-ups, scenery, moon void |
| `src/InscryptionVR/Menus` | Pause menu panel, floating menu screen, title-screen input |
| `src/InscryptionVR/Feedback` | Subtitles, tips, spatial audio, game haptics |
| `native` | C++ OpenVR compositor bridge and eye-tracking helper |
| `tools` | Validation harnesses and game-inspection scripts |
| `docs` | Design notes: [hands](docs/hologram-hands.md), [events and items](docs/event-controls.md), [dialogue](docs/dialogue-integration.md), [native renderer](docs/native-renderer.md) |

## Credits and license

Inscryption VR is released under the [MIT License](LICENSE).

It uses [BepInEx](https://github.com/BepInEx/BepInEx) (LGPL-2.1) and Valve's [OpenVR SDK](https://github.com/ValveSoftware/openvr) (BSD-3-Clause, license shipped as `OpenVR-LICENSE.txt`).

This is an unofficial fan project. It isn't affiliated with or endorsed by Daniel Mullins Games or Devolver Digital. You need to own Inscryption to use it, and no game files are included.
