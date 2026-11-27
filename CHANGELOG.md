# Changelog

## 0.33.3

- Auto-advance: the reading clock keeps running when you look away, and a finished line moves on as soon as you look back at Leshy or the subtitles. Faster paces also shorten the minimum time on screen.

## 0.33.2

- The dagger at the mysterious stones lies a little closer to you.

## 0.33.1

- The dagger at the mysterious stones is there for the whole event and rises and sinks with the altar stone. You can pick it up any time; after a plunge it glides back onto the altar.
- The battle dagger comes and goes with the bell and scales. In the tutorial it arrives early, when the first sacrifice comes before the bell.

## 0.33.0

- The dagger at the mysterious stones rests on the altar stone's edge instead of floating in front of the card.
- The battle dagger is no longer darkened and slides onto the table at the start of a battle and off at the end.
- The color grading switches are gone. Grading is on unless Optimization is set to Standalone.

## 0.32.2

- With auto-advance on, short lines stay up for a moment after their text finishes.

## 0.32.1

- Waving to skip stops at the end of the event you waved at, instead of also cutting off Leshy's next remarks.
- After an auto-advance, a trigger press meant for the old line no longer skips the new one.

## 0.32.0

- Auto-advance is now a pace setting (Off, 0.5x to 2x). Each line gets a reading time based on its length.
- Wave to skip also works with quick wrist flaps.
- Tutorial: "get up from the table" now works with the main-hand stick down.
- Bottles vanish in your hand when slammed, instead of leaving a broken copy behind.

## 0.31.3

- The deck review at the start of a run closes on its own a few seconds after Leshy finishes talking.

## 0.31.2

- Fixed the tutorial stalling after the Stoat: the view now returns from the board on its own.

- Fixed SteamVR detection: the game didn't notice SteamVR was running and started on the monitor.

## 0.31.0

- The game picks VR or monitor on its own: if SteamVR is running when the game starts, it starts in VR; otherwise it runs on the monitor. No launch option needed.
- `-vr` still forces VR (and starts SteamVR). New `-novr` forces the monitor even with SteamVR open.
- `Rendering.LaunchMode` in the config: `Auto` (default) or `FlagOnly` for the old behaviour.

## 0.30.5

- Used items stay in your hand. The pliers, fan, dagger, watch, scissors, hook, knife and hammer no longer fly up off the table, and the game's floating first-person copy is hidden, though its effects still play. The item disappears when you let go.
- An item pulled from the backpack, or a new totem piece taken from the Woodcarver, stays in your hand until you let go.
- Woodcarver totem build: carry the pieces by hand. Set a base down on the stump; the head you're holding becomes the screw-on head right in your hand. The finished totem stays on the stump instead of flying off.

## 0.30.4

- New "Wave to skip" switch in the Audio & Text tab: wave an empty hand quickly to skip the current dialogue.
- The fan works with a single swing.
- The battle dagger and the stone-rite dagger sit in new spots.

## 0.30.3

- Rule book: the page text is zoomed to fill the page and is easier to read. Grabbing the book on the table needs your fingers on it. Swiping to turn pages needs trigger or grip held, and swiping left to right turns forward.

## 0.30.2

- New "Interactable glow" switch in the Hands tab. Turning it off removes every grab glow, cards included.
- Nothing in the cabin glows any more when you walk around (the clock, safe, doors and so on). The hand highlight and tick stay.

## 0.30.1

- The totem head is much easier to grab during the totem build. It wins over the base unless the base is clearly closer, a grip just after the head leaves reach still takes it, and the loose head sits closer to the base.
- Threading the head no longer flickers on and off near the top of the thread.

## 0.30.0, first public release

Everything listed in the README. Before this release the mod went through about thirty private versions:

- **0.30** Two-handed dagger rite at the mysterious stones, hands-over-the-fire campfire rite, and a "Simplified events" switch for all elaborate event gestures.
- **0.29** Moon void and starry sky for the moon fight, a night sky over the finale walk, two-row hand, color-grading toggles.
- **0.28** VR for the finale walk and its deck trial; the camcorder footage menu is skipped.
- **0.27** The monitor mirrors the headset view.
- **0.26** Boss scenery extends into the distance.
- **0.25** Eye-gaze sacrifice options (shake, marker, heartbeat, Leshy watches the victim).
- **0.24** Eye tracking through a separate helper process; Leshy notices your stare.
- **0.23** Performance presets for standalone headsets, half framerate, the game's pixel font for subtitles, text-scroll-aware auto-advance.
- **0.22** Game-style subtitles.
- **0.21** VR starts only with the `-vr` launch option.
- **0.20** Eyeball socket, item pulling, boulder knocking, campfire warmth, cabin dials.
- **0.19** Totem screw-on, battle totem, bone tokens.
- **0.18** Main menu in VR.
- **0.17** Rule book held in hand; dagger carving.
- **0.16** Boss intro close-up.
- **0.15** The game's own hand models as VR hands.
- **0.14** Board lift, hand reordering, table-stop hands, scales, candles, bell slap strength, Leshy's gaze.
- **0.13** Tabbed settings panel, live render quality, left-handed mode, snap-turn angle; dialogue auto-advance.
- **0.12** Hand pinning, deck review exit, lane preview styles, steadier card hover.
- **0.11** Face-up card row, death-card hand-over, standing up and walking the cabin.
- **0.10** Kaycee's Mod menu on a floating screen.
- **0.9** VR in the sanctum, with an in-world name keyboard.
- **0.8** Haptics mixer, hold poses, lane ghost, spatial audio, tips, knife-driven sacrifice.
- **0.7** Renderer survives scene changes; victory feast input; sigil info panel; subtitles above Leshy.
- **0.6** Card row on the table, live seat height and world size, a dagger that sticks in the table.
- **0.5** Hologram hands, shared controller input, event hands.
