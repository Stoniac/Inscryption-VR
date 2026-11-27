using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace InscryptionVR
{
    internal sealed class VrConfig
    {
        internal readonly ConfigEntry<bool> EnableStereoRenderer;
        internal readonly ConfigEntry<string> LaunchMode;
        internal readonly ConfigEntry<float> WorldUnitsPerMeter;
        internal readonly ConfigEntry<float> RenderScale;
        internal readonly ConfigEntry<float> PosePredictionFrames;
        internal readonly ConfigEntry<bool> DisableDesktopVsync;
        internal readonly ConfigEntry<bool> StereoImageEffects;
        internal readonly ConfigEntry<string> Optimization;
        internal readonly ConfigEntry<string> HalfFramerate;
        internal readonly ConfigEntry<bool> DesktopMirror;
        internal readonly ConfigEntry<float> SeatHeight;
        internal readonly ConfigEntry<bool> EnableSanctumVr;
        internal readonly ConfigEntry<bool> EnableFinaleVr;
        internal readonly ConfigEntry<bool> EnableKayceeMenuVr;
        internal readonly ConfigEntry<bool> EnableMainMenuVr;
        internal readonly ConfigEntry<float> MenuScreenDistance;
        internal readonly ConfigEntry<float> MenuScreenWidth;

        internal readonly ConfigEntry<string> HandStyle;
        internal readonly ConfigEntry<bool> FlipGameHands;
        internal readonly ConfigEntry<string> HandColor;
        internal readonly ConfigEntry<float> HandSize;
        internal readonly ConfigEntry<float> HandAngle;
        internal readonly ConfigEntry<float> HandOpacity;
        internal readonly ConfigEntry<bool> LeftHanded;
        internal readonly ConfigEntry<float> SnapTurnDegrees;

        internal readonly ConfigEntry<bool> EnableControllerPointer;
        internal readonly ConfigEntry<bool> EnableLaser;
        internal readonly ConfigEntry<bool> EnableCardGrab;
        internal readonly ConfigEntry<float> CardGrabReach;
        internal readonly ConfigEntry<float> TableHalfWidthLanes;
        internal readonly ConfigEntry<float> TableHalfDepthLanes;
        internal readonly ConfigEntry<bool> EnableBellTouch;
        internal readonly ConfigEntry<float> BellTouchReach;
        internal readonly ConfigEntry<float> BellMinSlapSpeed;
        internal readonly ConfigEntry<float> CardRowLanes;
        internal readonly ConfigEntry<float> CardRowOverlap;
        internal readonly ConfigEntry<string> CardRowHover;
        internal readonly ConfigEntry<bool> EnableSacrificeKnife;
        internal readonly ConfigEntry<float> KnifeLength;
        internal readonly ConfigEntry<float> KnifeLanesLeft;
        internal readonly ConfigEntry<float> KnifeLanesToward;
        internal readonly ConfigEntry<bool> EnableEventHands;
        internal readonly ConfigEntry<bool> EnableMapFigurine;
        internal readonly ConfigEntry<bool> EnableItemHands;
        internal readonly ConfigEntry<float> ItemSlamSpeed;
        internal readonly ConfigEntry<bool> EnableCardInspection;
        internal readonly ConfigEntry<bool> EnableBoardLift;
        internal readonly ConfigEntry<bool> EnableScaleTouch;
        internal readonly ConfigEntry<bool> EnableCandleWarmth;
        internal readonly ConfigEntry<bool> EnableTotemHands;
        internal readonly ConfigEntry<bool> EnableBoneTokens;
        internal readonly ConfigEntry<bool> EnableEyeballSocket;
        internal readonly ConfigEntry<bool> EnableItemPull;
        internal readonly ConfigEntry<bool> EnableBoulderKnock;
        internal readonly ConfigEntry<bool> EnableCampfireWarmth;
        internal readonly ConfigEntry<bool> EnableCabinDials;
        internal readonly ConfigEntry<bool> EnableStoneRites;
        internal readonly ConfigEntry<bool> SimplifiedEvents;
        internal readonly ConfigEntry<bool> EnableRuleBookHands;
        internal readonly ConfigEntry<float> RuleBookWidth;
        internal readonly ConfigEntry<bool> TableStopsHands;
        internal readonly ConfigEntry<bool> CardHoverGlow;
        internal readonly ConfigEntry<bool> InteractableGlow;
        internal readonly ConfigEntry<bool> LeshyLooksAtYou;
        internal readonly ConfigEntry<bool> WaveSkipsDialogue;
        internal readonly ConfigEntry<bool> EyeTracking;
        internal readonly ConfigEntry<bool> LeshyNoticesStare;
        internal readonly ConfigEntry<bool> LeshyWatchesSacrifice;
        internal readonly ConfigEntry<string> SacrificeShake;
        internal readonly ConfigEntry<string> SacrificeMarker;
        internal readonly ConfigEntry<bool> SacrificeHeartbeat;
        internal readonly ConfigEntry<string> BossCloseupMode;
        internal readonly ConfigEntry<float> BossCloseupDistance;
        internal readonly ConfigEntry<float> EndlessSceneryLayers;
        internal readonly ConfigEntry<bool> MoonVoid;
        internal readonly ConfigEntry<string> LanePreviewStyle;
        internal readonly ConfigEntry<bool> EnableHeadTrackedAudio;
        internal readonly ConfigEntry<bool> ShowTips;
        internal readonly ConfigEntry<string> SeenTips;

        internal readonly ConfigEntry<bool> EnableGameHaptics;
        internal readonly ConfigEntry<float> GameHapticStrength;
        internal readonly ConfigEntry<float> VoiceHapticStrength;

        internal readonly ConfigEntry<bool> EnableVrPauseMenu;
        internal readonly ConfigEntry<float> PauseMenuDistance;
        internal readonly ConfigEntry<bool> PauseMirrorFlip;

        internal readonly ConfigEntry<bool> EnableVrSubtitles;
        internal readonly ConfigEntry<bool> ShowDialogueBars;
        internal readonly ConfigEntry<string> SubtitleStyle;
        internal readonly ConfigEntry<float> SubtitleHeight;
        internal readonly ConfigEntry<float> AutoAdvancePace;
        internal readonly ConfigEntry<bool> LogRenderTiming;
        internal readonly ConfigEntry<bool> VerboseLogging;

        internal VrConfig(ConfigFile config)
        {
            EnableStereoRenderer = config.Bind("Rendering", "EnableStereoRenderer", true,
                "Show the cabin in your headset through SteamVR. Turn off to play on the monitor only.");
            LaunchMode = config.Bind("Rendering", "LaunchMode", VrLaunch.Auto, new ConfigDescription(
                "Auto: start in VR when SteamVR is already running at launch, otherwise on the monitor. FlagOnly: start in VR only with the -vr launch option. " +
                "Either way, -vr forces VR (and starts SteamVR) and -novr forces the monitor.",
                new AcceptableValueList<string>(VrLaunch.Modes)));
            WorldUnitsPerMeter = config.Bind("Rendering", "WorldUnitsPerMeter", DefaultWorldUnitsPerMeter, Range(
                "Game world units per real metre. Raise it if you feel small and the cards look huge; lower it if you feel like a giant. Adjustable live in the VR pause menu.", 1f, 40f));
            RenderScale = config.Bind("Rendering", "RenderScale", 0.65f, Range(
                "Fraction of SteamVR's recommended eye resolution. Above 100% supersamples. Adjustable live in the VR pause menu (Render quality).", StereoRenderer.MinRenderScale, StereoRenderer.MaxRenderScale));
            PosePredictionFrames = config.Bind("Rendering", "PosePredictionFrames", 2f, Range(
                "How many display frames ahead the head and hands are predicted. Reduces swimming when you move. Zero uses the current pose.", 0f, 3f));
            DisableDesktopVsync = config.Bind("Rendering", "DisableDesktopVsync", true,
                "Turn off the monitor's vsync while in VR so the headset, not the desktop refresh rate, paces frames.");
            StereoImageEffects = config.Bind("Rendering", "StereoImageEffects", true,
                "Show the game's color grading, posterize, vignette and grain in each eye, and its fades and blinks as head-locked overlays.");
            Optimization = config.Bind("Rendering", "Optimization", InscryptionVR.Optimization.Off, new ConfigDescription(
                "Off: render as before. Balanced: while you're at the table, stop drawing the game a third time for the monitor window, render the eyes without HDR, " +
                "and cap shadow distance and per-pixel lights. Standalone: everything in Balanced, plus no shadows, one per-pixel light, lower detail at distance, " +
                "no per-eye color grading, and half framerate (see HalfFramerate). Meant for playing on the headset itself, such as a Steam Frame. " +
                "Picking Balanced or Standalone in the VR pause menu also lowers Render quality to 60% or 50% if it's higher. Adjustable live in the VR pause menu.",
                new AcceptableValueList<string>(InscryptionVR.Optimization.Levels)));
            HalfFramerate = config.Bind("Rendering", "HalfFramerate", InscryptionVR.Optimization.Auto, new ConfigDescription(
                "Run the game at half the headset's refresh rate and let SteamVR fill in every other frame. Halves the work per second, " +
                "but your hands and moving objects update less smoothly. Auto: on only with Optimization = Standalone. Adjustable live in the VR pause menu.",
                new AcceptableValueList<string>(InscryptionVR.Optimization.HalfRateModes)));
            DesktopMirror = config.Bind("Rendering", "DesktopMirror", true,
                "Show the headset's left-eye view in the monitor window instead of the flat game camera, cropped to fill the window. " +
                "The VR pause menu and the floating menu screens still show the flat game, because they capture the monitor image. Adjustable live in the VR pause menu.");
            SeatHeight = config.Bind("Rendering", "SeatHeightMeters", 0f, Range(
                "Raises or lowers your seat at the table. Adjustable live in the VR pause menu.", -0.5f, 0.5f));
            EnableSanctumVr = config.Bind("Rendering", "EnableSanctumVr", true,
                "Stay in VR when you die and wake on the sanctum floor: hand Leshy the cards for your death card, type your name on a floating keyboard, " +
                "and do the same for the victory card. Turn off to play the sanctum on the monitor.");
            EnableFinaleVr = config.Bind("Rendering", "EnableFinaleVr", true,
                "Stay in VR for the moonlit walk to Leshy's cabin before the final battle: walk with the sticks like in the cabin, and the seat glides up to " +
                "the giant's table for the deck trial so you can grab the cards. Turn off to play that walk on the monitor.");
            EnableKayceeMenuVr = config.Bind("Rendering", "EnableKayceeMenuVr", true,
                "Stay in VR on the Kaycee's Mod menu (challenges, starter deck, run summary, unlocks) and its credits. The menu shows on a big floating screen: " +
                "point either controller at it and pull the trigger to click. Turn off to use the monitor there.");
            EnableMainMenuVr = config.Bind("Rendering", "EnableMainMenuVr", true,
                "Start in VR: the title screen, main menu, options, loading screens and menu credits show on the same floating screen, so the game can be played " +
                "without a monitor. Pull either trigger to get past the title, point and pull to click, and press B or Y to back out of a menu card or the credits. " +
                "Turn off to use the monitor there.");
            MenuScreenDistance = config.Bind("UI", "MenuScreenDistanceMeters", 2f, Range(
                "How far in front of you the floating menu screen (main menu and Kaycee's Mod menu) floats.", 1f, 5f));
            MenuScreenWidth = config.Bind("UI", "MenuScreenWidthMeters", 2.4f, Range(
                "Width of the floating menu screen (main menu and Kaycee's Mod menu).", 0.8f, 6f));

            HandStyle = config.Bind("Hands", "HandStyle", GameHands.Game, new ConfigDescription(
                "Game: at the table, your hands are copies of the game's own hand model (matching your character), lit by the cabin. Hologram: the see-through hologram hands. " +
                "The holograms are always used outside the cabin. Adjustable live in the VR pause menu.", new AcceptableValueList<string>(GameHands.Styles)));
            FlipGameHands = config.Bind("Hands", "FlipGameHands", false,
                "Swap which game hand model is mirrored. Turn this on if the game hands look inside out or have their thumbs on the wrong side. Adjustable live in the VR pause menu.");
            HandColor = config.Bind("Hands", "HandColor", "Cyan", new ConfigDescription(
                "Color of your hologram hands. Off hides them (interactions still work).", new AcceptableValueList<string>(HologramHands.Palettes)));
            HandSize = config.Bind("Hands", "HandSize", 1f, Range("Size of the hologram hands relative to an average adult hand.", 0.7f, 1.3f));
            HandAngle = config.Bind("Hands", "HandAngleDegrees", 0f, Range(
                "Tilts the hologram hands around the controller grip if they don't line up with your real hands.", -30f, 30f));
            HandOpacity = config.Bind("Hands", "HandOpacity", 0.5f, Range("How solid the hologram hands look. Their edges stay a little brighter.", 0.01f, 1f));
            LeftHanded = config.Bind("Hands", "LeftHanded", false,
                "Swap hand roles: hold cards (and the laser) in your left hand and the sacrifice knife in your right, with the knife stored on the right of the table. " +
                "Pause moves to the right B button; walking in the cabin moves to the right stick and turning to the left. Adjustable live in the VR pause menu.");
            SnapTurnDegrees = config.Bind("Hands", "SnapTurnDegrees", 90f, Range(
                "How far one flick of the turning stick turns you while walking around the cabin. Adjustable live in the VR pause menu.", 15f, 180f));

            EnableControllerPointer = config.Bind("Input", "EnableControllerPointer", true,
                "Either trigger advances dialogue. Also hosts the optional laser (EnableLaser). F10 toggles.");
            EnableLaser = config.Bind("Input", "EnableLaser", false,
                "Fallback laser on your card hand (right, or left with LeftHanded): point at things and pull the trigger to select them. Off by default because hands handle battles, the map, items and events. " +
                "Can be toggled live in the VR pause menu.");
            EnableCardGrab = config.Bind("Input", "EnableCardGrab", true,
                "Grab cards, piles, items and event cards with grip. Release a card over one of your lanes to play it. F10 toggles.");
            CardGrabReach = config.Bind("Input", "CardGrabReachMeters", 0.08f, Range(
                "How close your fingertips must be to something to grab it by touch.", 0.02f, 0.3f));
            TableHalfWidthLanes = config.Bind("Input", "TableHalfWidthLanes", 4.5f, Range(
                "Half-width of the tabletop that catches dropped cards, in lane widths from the board center.", 1f, 12f));
            TableHalfDepthLanes = config.Bind("Input", "TableHalfDepthLanes", 3f, Range(
                "Half-depth of the tabletop that catches dropped cards, in lane widths from the board center.", 1f, 10f));
            EnableBellTouch = config.Bind("Input", "EnableBellTouch", true,
                "Touch or slap the combat bell with an open hand or fingertip to end your turn.");
            BellTouchReach = config.Bind("Input", "BellTouchReachMeters", 0.03f, Range(
                "How close your hand must come to the bell to ring it.", 0f, 0.3f));
            BellMinSlapSpeed = config.Bind("Input", "BellMinSlapSpeed", 0f, Range(
                "Minimum downward hand speed, in metres per second, to ring the bell. The hand must always come down onto the bell from above, mostly straight down and at least 0.15 m/s, so sweeping through it sideways never rings it.", 0f, 5f));
            CardRowLanes = config.Bind("Input", "CardRowLanesTowardPlayer", 1.3f, Range(
                "How far toward you from your lanes your unplayed cards lie on the table, in lane widths. Adjustable live in the VR pause menu.", 0.8f, 3f));
            CardRowOverlap = config.Bind("Input", "CardRowOverlap", 0.45f, Range(
                "How much each unplayed card on the table is covered by its left neighbour, as a fraction of a card's width. The right side, with cost and health, stays visible. " +
                "Adjustable live in the VR pause menu.", 0f, 0.8f));
            CardRowHover = config.Bind("Input", "CardRowHover", CardRow.Spread, new ConfigDescription(
                "What happens when your hand is close enough to grab an unplayed card on the table. Spread: the card stays where it is and the cards to its left slide aside " +
                "so its whole face shows. Rise: the card lifts slightly above its neighbours. Adjustable live in the VR pause menu.",
                new AcceptableValueList<string>(CardRow.HoverStyles)));
            EnableSacrificeKnife = config.Bind("Input", "EnableSacrificeKnife", true,
                "Sacrifice with a knife stuck in the table on your off-hand side: hold a card that costs blood in your card hand and grip the knife's handle with your other hand, in either order " +
                "(held point-down, blade out of your little-finger side), then stab or slash your creatures. A hard upright stab (within 45 degrees of vertical) sinks the tip in until you pull it out; " +
                "a slanted stab, or leaning a stuck knife past 45 degrees, drags the tip along the table so you can carve across several creatures until you lift it. " +
                "Let go of the card or the knife, or stab an empty spot, to cancel. Once the blood is paid, let go of the card over an empty lane to play it.");
            KnifeLength = config.Bind("Input", "SacrificeKnifeLengthMeters", 0.28f, Range("Length of the sacrifice knife.", 0.1f, 0.6f));
            KnifeLanesLeft = config.Bind("Input", "SacrificeKnifeLanesLeft", 1.3f, Range(
                "How far beyond your outermost lane, on the knife-hand side, the stored knife sits, in lane widths.", 0f, 6f));
            KnifeLanesToward = config.Bind("Input", "SacrificeKnifeLanesTowardPlayer", 0.4f, Range(
                "How far toward you from your lane row the stored knife sits, in lane widths. Negative moves it toward Leshy.", -3f, 3f));
            EnableEventHands = config.Bind("Input", "EnableEventHands", true,
                "Handle every event by hand: grip a face-down card to flip it, pull a face-up card clear to take it, carry deck cards onto altar slots, " +
                "touch the confirm stone, and grip anything else the event offers (trader cards, boulders, eyeballs, totem pieces). " +
                "Grip with nothing in reach to select what the hand is aimed at.");
            EnableMapFigurine = config.Bind("Input", "EnableMapFigurine", true,
                "Move on the map by picking up your figurine and setting it down on a lit path node.");
            EnableItemHands = config.Bind("Input", "EnableItemHands", true,
                "Pick up items and use them with a motion: pliers to your mouth, slam bottles, flip the hourglass, hold the magnifier to your eye, wind the watch, " +
                "wave the fan, touch a card with scissors, hook or knife, or swing the hammer.");
            ItemSlamSpeed = config.Bind("Input", "ItemSlamMetersPerSecond", 1f, Range(
                "How fast a bottle must be brought down onto the table to break it. The piggy bank needs 20% more.", 0.3f, 5f));
            EnableCardInspection = config.Bind("Input", "EnableCardInspection", false,
                "Bring a card you're holding within about 30 cm of your face to read its cost, stats and what each sigil does on a panel beside it.");
            EnableBoardLift = config.Bind("Input", "EnableBoardLift", true,
                "On your turn, grip any card on the board, yours or Leshy's (including the cards he has queued), to lift it and look at it. Let go and it glides back to its place. " +
                "Nothing in the game changes.");
            EnableScaleTouch = config.Bind("Input", "EnableScaleTouch", true,
                "Poke a pan of the scale to make it bob, and grip a tooth on the scale to pick it up and fidget with it. Let go and it drops back onto its pan; the score never changes.");
            EnableCandleWarmth = config.Bind("Input", "EnableCandleWarmth", true,
                "Hold a hand near a lit candle to feel a soft, warm rumble that grows as you get closer, and watch the flame lean away from your hand. A candle that's out just gives a faint tick.");
            EnableTotemHands = config.Bind("Input", "EnableTotemHands", true,
                "At the Woodcarver, the finished totem comes apart into its base and a loose head. Pick up the head and screw it onto the base clockwise, " +
                "about one full turn (regrip as you go); turning it back loosens it. You can also lift the base in your other hand and screw them together in the air. " +
                "It counts as built once it's tight. In battle, grip your totem to pick it up and turn it over; it buzzes in your hand when it fires and glides back when you let go.");
            EnableBoneTokens = config.Bind("Input", "EnableBoneTokens", true,
                "Your bone tokens are loose objects on the table: flick or push them with a fingertip, or grip one to pick it up, then toss or stack it. " +
                "The game still counts and spends them as usual, and any that fall off the table come back to the pile.");
            EnableEyeballSocket = config.Bind("Input", "EnableEyeballSocket", true,
                "At the eyeball box, the eyes are loose: pick one up, roll it around in your fingers, and press it into your empty eye socket (bring it to your face) to choose it.");
            EnableItemPull = config.Bind("Input", "EnableItemPull", true,
                "At the backpack (and when the Woodcarver offers new totem pieces), grip an item and pull it out toward you to take it. Let go before it's clear and it slides back.");
            EnableBoulderKnock = config.Bind("Input", "EnableBoulderKnock", true,
                "When the Prospector asks where to strike, knock twice on a boulder with your knuckles instead of gripping it.");
            EnableCampfireWarmth = config.Bind("Input", "EnableCampfireWarmth", true,
                "At the campfire, hold your hands over the fire to feel its heat flicker and grow as you get closer. Too close and it crackles and stings.");
            EnableCabinDials = config.Bind("Input", "EnableCabinDials", true,
                "While walking around the cabin, grip the safe's combination dials or the cuckoo clock's knobs (touch them, or point at them from further away) and twist your wrist to turn them, " +
                "one click per number or hour, in either direction.");
            EnableStoneRites = config.Bind("Input", "EnableStoneRites", true,
                "The confirm stones are hidden. At the mysterious stones, a dagger appears in front of the sacrifice once both cards are placed: grab it with both hands and plunge it into the sacrifice. " +
                "At the campfire, hold both hands by the fire for a second to warm the card (and to push your luck); take the card back to pull away.");
            SimplifiedEvents = config.Bind("Input", "SimplifiedEvents", false,
                "Turns off the hand gestures that replace simple presses at events: the dagger plunge and campfire warming (the confirm stones come back), pulling items out, knocking on boulders, " +
                "socketing an eyeball, screwing the totem together, twisting the cabin dials, and looking away for the mycologists. Each one becomes a plain grip or press. Adjustable live in the VR pause menu.");
            EventStyle.Simplified = SimplifiedEvents.Value;
            SimplifiedEvents.SettingChanged += delegate { EventStyle.Simplified = SimplifiedEvents.Value; };
            EnableRuleBookHands = config.Bind("Input", "EnableRuleBookHands", true,
                "Grip the rule book on the table to open it in that hand like a real book. Swipe your other hand across the pages, or flick either stick left or right, to turn them. " +
                "Let go, or press B on your card hand, to close it. When the game opens the book itself, it appears in your off hand (or floats in front of you if both hands are busy).");
            RuleBookWidth = config.Bind("Input", "RuleBookWidthMeters", 0.45f, Range(
                "Width of the open rule book in your hand. Adjustable live in the VR pause menu.", 0.2f, 0.9f));
            TableStopsHands = config.Bind("Hands", "TableStopsHands", true,
                "Your hologram hands stop at the tabletop instead of sinking through it, with a tap when they touch down. Adjustable live in the VR pause menu.");
            CardHoverGlow = config.Bind("Hands", "CardHoverGlow", true,
                "A card glows when your hand is close enough to grab it. Turn off to keep only the spread (or lift) and tick. Adjustable live in the VR pause menu.");
            InteractableGlow = config.Bind("Hands", "InteractableGlow", true,
                "Things you can grab or use (items, the knife, totems, bones, event objects, cards) glow when your hand can reach them. Turn off to remove every glow; Card glow then has no effect. Cabin objects never glow. Adjustable live in the VR pause menu.");
            LeshyLooksAtYou = config.Bind("UI", "LeshyLooksAtYou", true,
                "Leshy's head and eyes turn toward your headset while he talks, unless the game has him looking at something else. Adjustable live in the VR pause menu.");
            WaveSkipsDialogue = config.Bind("UI", "WaveSkipsDialogue", true,
                "While Leshy talks, wave him away twice quickly with an empty hand raised to chest height or higher (side to side, back and forth, or a shooing flap of the wrist) to skip the rest of the dialogue, " +
                "for example a boss intro. Adjustable live in the VR pause menu.");
            EyeTracking = config.Bind("UI", "EyeTracking", true,
                "Use the headset's eye tracking (Steam Frame) when it reports a gaze. A small helper program next to the plugin reads it from SteamVR, so the game's own controls are not affected. " +
                "SteamVR may ask once whether to share eye tracking data with PC apps. Nothing happens on headsets without eye tracking. Adjustable live in the VR pause menu.");
            LeshyNoticesStare = config.Bind("UI", "LeshyNoticesStare", true,
                "Needs eye tracking. Stare at Leshy's face and he looks back at you; rest your eyes on a spot on the table and his gaze follows yours for a few seconds. " +
                "He never overrides where the game wants him to look. Adjustable live in the VR pause menu.");
            LeshyWatchesSacrifice = config.Bind("UI", "LeshyWatchesSacrifice", true,
                "Needs eye tracking. While you choose a sacrifice, Leshy's eyes go straight to whichever creature you are looking at. Adjustable live in the VR pause menu.");
            SacrificeShake = config.Bind("Input", "SacrificeShake", SacrificeGaze.ShakeGame, new ConfigDescription(
                "Which creatures tremble while you choose a sacrifice. Game: all of them, as in the flat game. Look at knife: they all tremble only while you look at the dagger. " +
                "Look at creature: only the one you are looking at trembles. Knife or creature: all tremble while you look at the dagger, otherwise only the one you look at. Off: none. " +
                "Everything except Game needs eye tracking and falls back to Game without it. Adjustable live in the VR pause menu.",
                new AcceptableValueList<string>(SacrificeGaze.ShakeModes)));
            SacrificeMarker = config.Bind("Input", "SacrificeMarker", SacrificeGaze.MarkerKnife, new ConfigDescription(
                "Where the game's sacrifice hover marker appears while you hold the dagger. Knife: over the creature under the blade. Knife and eyes: only when the creature under the blade is also the one you look at. " +
                "Eyes: over whichever creature you look at. The eye options fall back to Knife without eye tracking. Adjustable live in the VR pause menu.",
                new AcceptableValueList<string>(SacrificeGaze.MarkerModes)));
            SacrificeHeartbeat = config.Bind("Input", "SacrificeHeartbeat", true,
                "Needs eye tracking. While you hold the dagger and look at a creature you could sacrifice, the dagger hand feels its heartbeat, faster and stronger as the blade gets closer. " +
                "Adjustable live in the VR pause menu.");
            BossCloseupMode = config.Bind("View", "BossCloseup", BossCloseup.MoveMe, new ConfigDescription(
                "When a boss puts on his mask at the start of a boss fight and the game zooms in on his face. Move me: you glide toward Leshy until your faces are " +
                "BossCloseupDistanceMeters apart. Move Leshy: Leshy leans in to you instead, leaving his hands on the table. Off: nothing moves. " +
                "Adjustable live in the VR pause menu.", new AcceptableValueList<string>(BossCloseup.Modes)));
            BossCloseupDistance = config.Bind("View", "BossCloseupDistanceMeters", 0.7f, Range(
                "How far apart your face and Leshy's end up during the boss closeup.", 0.2f, 3f));
            EndlessSceneryLayers = config.Bind("View", "EndlessSceneryLayers", 6f, Range(
                "Extra rows added beyond the outermost row of the Angler's hooked fish and the Trapper's knife conveyors, so they seem to go on into the dark. " +
                "Zero leaves the scenery as the game builds it. With Optimization = Standalone, half as many are added. Adjustable live in the VR pause menu.", 0f, 12f));
            MoonVoid = config.Bind("View", "MoonVoid", true,
                "In the final fight, when Leshy's photo of the moon lands on the table as the giant Moon card, the cabin vanishes into blackness around the table, Leshy and the table props, " +
                "and the moon hangs huge and far below. The cabin comes back when the moon card dies or the battle ends. Adjustable live in the VR pause menu.");
            LanePreviewStyle = config.Bind("Input", "LanePreview", LanePreview.Ghost, new ConfigDescription(
                "What a lane shows while you hold a card over it. Ghost: a see-through copy of the card in the lane, with a red outline when the game wouldn't let you play it there. " +
                "Glow: the game's own slot highlight, only when the card can go there. Off: nothing. Ghost and Glow also put a soft shadow under the card. " +
                "Adjustable live in the VR pause menu.", new AcceptableValueList<string>(LanePreview.Styles)));
            EnableHeadTrackedAudio = config.Bind("Audio", "EnableHeadTrackedAudio", true,
                "Hear the cabin from your headset's position and direction instead of the game's fixed camera, and play card and knife sounds fully from where your hands are. " +
                "Adjustable live in the VR pause menu.");

            EnableGameHaptics = config.Bind("Haptics", "EnableGameHaptics", true,
                "Buzz the controllers when your creatures are hit or die, when the scale tips, and along with Leshy's voice.");
            GameHapticStrength = config.Bind("Haptics", "GameHapticStrength", 1f, Range("Overall strength of game haptics.", 0f, 1f));
            VoiceHapticStrength = config.Bind("Haptics", "VoiceHapticStrength", 0.35f, Range(
                "Peak strength of the rumble that follows Leshy's voice. Zero turns it off.", 0f, 1f));

            EnableVrPauseMenu = config.Bind("Pause", "EnableVrPauseMenu", true,
                "Show the game's pause menu on a floating screen with a VR settings panel. The left Y (or menu) button pauses and goes back; with LeftHanded it's the right B button.");
            PauseMenuDistance = config.Bind("Pause", "PauseMenuDistanceMeters", 0.55f, Range(
                "How far in front of you the pause screen appears. Keep it within reach to touch it.", 0.3f, 1.5f));
            PauseMirrorFlip = config.Bind("Pause", "PauseMirrorFlipVertical", true,
                "Flip the pause screen vertically. Turn off if it appears upside down.");

            EnableVrSubtitles = config.Bind("UI", "EnableVrSubtitles", true,
                "Show dialogue as subtitles floating above Leshy's head, or above the center of your view when he isn't there.");
            ShowDialogueBars = config.Bind("UI", "ShowDialogueBars", true,
                "Show the black bars that close in from the top and bottom while Leshy speaks or an item is aimed. Full blinks and fades still show. " +
                "Adjustable live in the VR pause menu.");
            SubtitleStyle = config.Bind("UI", "SubtitleStyle", VrDialogue.PanelStyle, new ConfigDescription(
                "Panel: white text on a dark box. Game: the base game's own look, with the speaker's colour (Leshy's rough orange), chunky pixel letters that jitter, " +
                "and the game's soft shadow instead of a box. Adjustable live in the VR pause menu.", new AcceptableValueList<string>(VrDialogue.Styles)));
            SubtitleHeight = config.Bind("UI", "SubtitleHeightMeters", 0.35f, Range(
                "How far above Leshy's head the subtitles float. Adjustable live in the VR pause menu.", -0.5f, 1.5f));
            AutoAdvancePace = config.Bind("UI", "AutoAdvancePace", 0f, Range(
                "Advance dialogue that waits for the trigger on its own, after a reading time that grows with the square root of the line's length (at least " +
                VrDialogue.MinReadSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s divided by the pace, never under " +
                VrDialogue.FloorReadSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s; once the text has finished scrolling in, pauses included, it stays at least " +
                VrDialogue.MinAfterTextSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s divided by the pace). " +
                "The clock keeps running if you look away, but the line only moves on while you face Leshy or the subtitles. " +
                "1 is the normal pace, 2 reads twice as fast, 0.5 half as fast. Zero turns it off; the trigger still works either way. " +
                "Adjustable live in the VR pause menu (Auto-advance).", 0f, 2f));
            ShowTips = config.Bind("UI", "ShowTips", true,
                "Float a short tip over the dagger, bell, piles, items and other things the first time each interaction comes up. Adjustable live in the VR pause menu.");
            SeenTips = config.Bind("UI", "SeenTips", "",
                "Tips you've already seen or acted on. Clear it, or use Reset tips in the VR pause menu, to see them all again.");
            LogRenderTiming = config.Bind("Diagnostics", "LogRenderTiming", false, "Every 5 seconds, log SteamVR frame timing plus how long the game's frames take and how much of that is eye rendering and hand interactions. " +
                "Adjustable live in the VR pause menu (Timing log).");
            VerboseLogging = config.Bind("Diagnostics", "VerboseLogging", false,
                "Write every input-state change (pointer and hand status, menu clicks, dialogue advances) to the log. Off keeps the log to events that matter.");
        }

        internal const float DefaultWorldUnitsPerMeter = 7.7f;
        private const float OldWorldUnitsPerMeter = 7f;

        internal void MigrateDefaults(ManualLogSource logger)
        {
            if (Mathf.Abs(WorldUnitsPerMeter.Value - OldWorldUnitsPerMeter) > 0.001f) return;
            WorldUnitsPerMeter.Value = DefaultWorldUnitsPerMeter;
            logger.LogInfo("World scale moved from the old default " + OldWorldUnitsPerMeter + " to " + DefaultWorldUnitsPerMeter + " units per metre (the world is about 10% smaller).");
        }

        internal void RemoveObsoleteEntries(ConfigFile config, ManualLogSource logger)
        {
            try
            {
                PropertyInfo property = typeof(ConfigFile).GetProperty("OrphanedEntries", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var orphans = property != null ? property.GetValue(config, null) as Dictionary<ConfigDefinition, string> : null;
                if (orphans == null || orphans.Count == 0) return;
                string stereo;
                bool enabled;
                if (orphans.TryGetValue(new ConfigDefinition("Experimental", "EnableStereoRenderer"), out stereo) && bool.TryParse(stereo.Trim(), out enabled))
                    EnableStereoRenderer.Value = enabled;
                string preview;
                bool showPreview;
                if (orphans.TryGetValue(new ConfigDefinition("Input", "ShowLanePreview"), out preview) && bool.TryParse(preview.Trim(), out showPreview) && !showPreview)
                    LanePreviewStyle.Value = LanePreview.Off;
                string advance;
                float advanceSeconds;
                if (orphans.TryGetValue(new ConfigDefinition("UI", "AutoAdvanceSeconds"), out advance) &&
                    float.TryParse(advance.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out advanceSeconds) && advanceSeconds > 0f)
                    AutoAdvancePace.Value = 1f;
                int count = orphans.Count;
                orphans.Clear();
                config.Save();
                logger.LogInfo("Removed " + count + " obsolete settings from the config file.");
            }
            catch (Exception exception)
            {
                logger.LogWarning("Could not tidy obsolete config settings: " + exception.Message);
            }
        }

        private static ConfigDescription Range(string description, float min, float max)
        {
            return new ConfigDescription(description, new AcceptableValueRange<float>(min, max));
        }
    }
}
