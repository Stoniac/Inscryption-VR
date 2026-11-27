using System;
using BepInEx;
using UnityEngine;

namespace InscryptionVR
{
    internal sealed class VrSystems
    {
        internal StereoRenderer Stereo;
        internal VrInput Input;
        internal VrDialogue Dialogue;
        internal DialogueSkip Skip;
        internal HologramHands Hands;
        internal ControllerPointer Pointer;
        internal CardGrabber Grabber;
        internal MenuScreen Screen;
        internal CabinWalk Walk;
        internal GameHaptics Haptics;
        internal SpatialAudio Audio;
        internal VrTips Tips;
        internal LeshyGaze Gaze;
        internal EyeGaze Eyes;
        internal BossCloseup Closeup;
        internal EndlessScenery Scenery;
        internal MoonVoid Moon;
        internal GameHands GameHands;
        internal RenderTimingDiagnostics Timing;
    }

    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInProcess("Inscryption.exe")]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "local.inscryption.vr";
        public const string PluginName = "Inscryption VR";
        public const string PluginVersion = "0.33.3";

        private VrConfig _config;

        private void Awake()
        {
            _config = new VrConfig(Config);
            _config.RemoveObsoleteEntries(Config, Logger);
            _config.MigrateDefaults(Logger);
            VrLog.Verbose = _config.VerboseLogging.Value;
            _config.VerboseLogging.SettingChanged += delegate { VrLog.Verbose = _config.VerboseLogging.Value; };
            Logger.LogInfo(PluginName + " " + PluginVersion + " loaded (Unity " + Application.unityVersion + ", " + IntPtr.Size * 8 + "-bit, " +
                SystemInfo.graphicsDeviceType + ", " + SystemInfo.graphicsDeviceName + ").");
            VrLaunch.Reason reason;
            if (!VrLaunch.ShouldStartVr(_config.LaunchMode.Value, out reason))
            {
                Logger.LogInfo("Starting on the monitor: " + VrLaunch.Describe(reason) + ".");
                if (reason != VrLaunch.Reason.FlatFlag) gameObject.AddComponent<FlatModeHint>().Auto = reason == VrLaunch.Reason.SteamVrClosed;
                return;
            }
            Logger.LogInfo("Starting in VR: " + VrLaunch.Describe(reason) + ".");
            if (!_config.EnableStereoRenderer.Value)
            {
                Logger.LogInfo("VR rendering is off (Rendering.EnableStereoRenderer); the game runs on the monitor only.");
                return;
            }
            try { StartVr(); }
            catch (Exception exception) { Logger.LogError("VR startup failed; the game continues on the monitor: " + exception); }
        }

        private void StartVr()
        {
            VrConfig c = _config;
            var s = new VrSystems();
            VrScenes.SanctumEnabled = c.EnableSanctumVr.Value;
            VrScenes.FinaleEnabled = c.EnableFinaleVr.Value;
            StartRendering(c, s);
            StartInput(c, s);
            StartDialogue(c, s);
            StartHands(c, s);
            StartWorld(c, s);
            StartMenus(c, s);
            StartScenes(c, s);
            if (c.EnableGameHaptics.Value)
            {
                s.Haptics = gameObject.AddComponent<GameHaptics>();
                s.Haptics.Initialize(Logger, s.Input, c.GameHapticStrength.Value, c.VoiceHapticStrength.Value);
            }
            if (c.EnableVrPauseMenu.Value)
                gameObject.AddComponent<VrPauseMenu>().Initialize(Logger, s.Stereo, s.Input, SettingsPanel.Build(c, s), c.PauseMenuDistance.Value, c.PauseMirrorFlip.Value);
        }

        private void StartRendering(VrConfig c, VrSystems s)
        {
            s.Stereo = gameObject.AddComponent<StereoRenderer>();
            s.Stereo.Initialize(Logger, c.WorldUnitsPerMeter.Value, c.RenderScale.Value, c.SeatHeight.Value,
                c.PosePredictionFrames.Value, c.DisableDesktopVsync.Value, c.StereoImageEffects.Value);
            s.Stereo.SetLetterbox(c.ShowDialogueBars.Value);
            s.Stereo.SetOptimization(c.Optimization.Value, c.HalfFramerate.Value);
            s.Stereo.SetDesktopMirror(c.DesktopMirror.Value);
            s.Timing = gameObject.AddComponent<RenderTimingDiagnostics>();
            s.Timing.Initialize(Logger, s.Stereo, c.LogRenderTiming.Value, delegate
            {
                return "optimization " + c.Optimization.Value + ", half framerate " + c.HalfFramerate.Value + ", render quality " +
                    Mathf.RoundToInt(c.RenderScale.Value * 100f) + "%";
            });
        }

        private void StartInput(VrConfig c, VrSystems s)
        {
            s.Input = new VrInput(Logger, s.Stereo);
            s.Input.SetLeftHanded(c.LeftHanded.Value);
            VictoryFeastInput.Install(Logger, s.Input);
            gameObject.AddComponent<SkipFootageMenu>().Initialize(Logger);
            gameObject.AddComponent<BoardViewReturn>().Initialize(Logger);
            gameObject.AddComponent<HapticMixer>().Initialize(Logger, s.Input);
            s.Audio = gameObject.AddComponent<SpatialAudio>();
            s.Audio.Initialize(Logger, s.Stereo, s.Input, c.EnableHeadTrackedAudio.Value);
            s.Tips = gameObject.AddComponent<VrTips>();
            s.Tips.Initialize(Logger, s.Stereo, c.ShowTips.Value, c.SeenTips);
        }

        private void StartDialogue(VrConfig c, VrSystems s)
        {
            s.Dialogue = gameObject.AddComponent<VrDialogue>();
            s.Dialogue.Initialize(Logger, s.Stereo, c.EnableVrSubtitles.Value, c.SubtitleHeight.Value, c.AutoAdvancePace.Value, c.SubtitleStyle.Value);
            s.Stereo.SetDialogue(s.Dialogue);
            gameObject.AddComponent<MergeLookAway>().Initialize(Logger, s.Stereo);
            s.Skip = gameObject.AddComponent<DialogueSkip>();
            s.Skip.Initialize(Logger, s.Stereo, s.Input, c.WaveSkipsDialogue.Value);
        }

        private void StartHands(VrConfig c, VrSystems s)
        {
            s.Hands = gameObject.AddComponent<HologramHands>();
            s.Hands.Initialize(Logger, s.Stereo, s.Input, c.HandColor.Value, c.HandSize.Value, c.HandAngle.Value, c.HandOpacity.Value);
            s.GameHands = gameObject.AddComponent<GameHands>();
            s.GameHands.Initialize(Logger, s.Stereo, s.Input, c.HandStyle.Value, c.FlipGameHands.Value);

            if (c.EnableControllerPointer.Value)
            {
                s.Pointer = gameObject.AddComponent<ControllerPointer>();
                s.Pointer.Initialize(Logger, s.Stereo, s.Input, s.Dialogue, c.EnableLaser.Value);
            }

            if (!c.EnableCardGrab.Value) return;
            s.Grabber = gameObject.AddComponent<CardGrabber>();
            s.Grabber.Initialize(Logger, s.Stereo, s.Input, c.CardGrabReach.Value, c.TableHalfWidthLanes.Value, c.TableHalfDepthLanes.Value,
                c.EnableBellTouch.Value, c.BellTouchReach.Value, c.BellMinSlapSpeed.Value);
            s.Grabber.ConfigureCards(c.CardRowLanes.Value, c.CardRowOverlap.Value, c.EnableSacrificeKnife.Value,
                c.KnifeLength.Value, c.KnifeLanesLeft.Value, c.KnifeLanesToward.Value);
            s.Grabber.ConfigureWorld(c.EnableEventHands.Value, c.EnableMapFigurine.Value, c.EnableItemHands.Value, c.ItemSlamSpeed.Value, c.EnableCardInspection.Value,
                c.EnableItemPull.Value, c.EnableBoulderKnock.Value);
            s.Grabber.ConfigureEvents(c.EnableEyeballSocket.Value, c.EnableCampfireWarmth.Value, c.EnableStoneRites.Value);
            s.Grabber.SetLanePreview(c.LanePreviewStyle.Value);
            s.Grabber.ConfigureTable(c.EnableBoardLift.Value, c.EnableScaleTouch.Value, c.EnableCandleWarmth.Value, c.TableStopsHands.Value,
                c.EnableTotemHands.Value, c.EnableBoneTokens.Value);
            s.Grabber.SetCardHoverGlow(c.CardHoverGlow.Value);
            GrabGlow.Enabled = c.InteractableGlow.Value;
            s.Grabber.SetCardRowHover(c.CardRowHover.Value);
            s.Grabber.ConfigureRuleBook(c.EnableRuleBookHands.Value, c.RuleBookWidth.Value);
            s.Grabber.ConfigureSacrificeGaze(c.SacrificeShake.Value, c.SacrificeMarker.Value, c.SacrificeHeartbeat.Value);
        }

        private void StartWorld(VrConfig c, VrSystems s)
        {
            s.Eyes = gameObject.AddComponent<EyeGaze>();
            s.Eyes.Initialize(Logger, s.Stereo, c.EyeTracking.Value);
            s.Gaze = gameObject.AddComponent<LeshyGaze>();
            s.Gaze.Initialize(Logger, s.Stereo, c.LeshyLooksAtYou.Value, c.LeshyNoticesStare.Value, c.LeshyWatchesSacrifice.Value);
            s.Closeup = gameObject.AddComponent<BossCloseup>();
            s.Closeup.Initialize(Logger, s.Stereo, c.BossCloseupMode.Value, c.BossCloseupDistance.Value);
            s.Scenery = gameObject.AddComponent<EndlessScenery>();
            s.Scenery.Initialize(Logger, delegate
            {
                int layers = Mathf.RoundToInt(c.EndlessSceneryLayers.Value);
                if (Optimization.Level(c.Optimization.Value) >= Optimization.LevelStandalone) layers = (layers + 1) / 2;
                return layers;
            });
            s.Moon = gameObject.AddComponent<MoonVoid>();
            s.Moon.Initialize(Logger, s.Stereo, c.MoonVoid.Value);
        }

        private void StartScenes(VrConfig c, VrSystems s)
        {
            if (VrScenes.SanctumEnabled)
            {
                if (s.Grabber != null) gameObject.AddComponent<SanctumHands>().Initialize(Logger, s.Input, s.Grabber);
                gameObject.AddComponent<NameKeyboard>().Initialize(Logger, s.Stereo, s.Input);
                SanctumShutter.Install(Logger, s.Input);
            }

            s.Walk = gameObject.AddComponent<CabinWalk>();
            s.Walk.Initialize(Logger, s.Stereo, s.Input, s.Grabber, s.Dialogue);
            s.Walk.SetSnapDegrees(c.SnapTurnDegrees.Value);
            if (c.EnableCabinDials.Value) s.Walk.EnableDials();

            if (VrScenes.FinaleEnabled)
            {
                gameObject.AddComponent<FinaleTrial>().Initialize(Logger, s.Stereo);
                gameObject.AddComponent<FinaleSky>().Initialize(Logger, s.Stereo);
            }
        }

        private void StartMenus(VrConfig c, VrSystems s)
        {
            if (!c.EnableKayceeMenuVr.Value && !c.EnableMainMenuVr.Value) return;
            var screen = gameObject.AddComponent<MenuScreen>();
            screen.Initialize(Logger, s.Stereo, s.Input, c.PauseMirrorFlip.Value, c.MenuScreenDistance.Value, c.MenuScreenWidth.Value);
            if (!screen.Available) return;
            s.Stereo.SetMenuScreen(screen);
            VrScenes.ScreenEnabled = c.EnableKayceeMenuVr.Value;
            VrScenes.MainMenuEnabled = c.EnableMainMenuVr.Value;
            s.Screen = screen;
            if (VrScenes.MainMenuEnabled) StartScreenInput.Install(Logger, s.Input);
        }
    }
}
