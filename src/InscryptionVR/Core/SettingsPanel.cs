using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace InscryptionVR
{
    internal static class SettingsPanel
    {
        private const string HandsTab = "Hands";
        private const string TableTab = "Table";
        private const string ViewTab = "View";
        private const string AudioTab = "Audio & Text";
        private const string PerformanceTab = "Performance";
        private static readonly float[] SnapAngles = { 30f, 45f, 60f, 90f };

        internal static List<VrSetting> Build(VrConfig c, VrSystems s)
        {
            var settings = new List<VrSetting>();

            c.HandStyle.SettingChanged += delegate { s.GameHands.SetStyle(c.HandStyle.Value); };
            settings.Add(ChoiceSetting(HandsTab, "Hand style", c.HandStyle, GameHands.Styles));
            c.FlipGameHands.SettingChanged += delegate { s.GameHands.SetFlip(c.FlipGameHands.Value); };
            settings.Add(ToggleSetting(HandsTab, "Flip game hands", c.FlipGameHands));
            c.HandColor.SettingChanged += delegate { s.Hands.SetPalette(c.HandColor.Value); };
            settings.Add(ChoiceSetting(HandsTab, "Hand color", c.HandColor, HologramHands.Palettes));
            EventHandler reshape = delegate { s.Hands.SetShape(c.HandSize.Value, c.HandAngle.Value); };
            c.HandSize.SettingChanged += reshape;
            c.HandAngle.SettingChanged += reshape;
            settings.Add(NumberSetting(HandsTab, "Hand size", c.HandSize, 0.05f, 0.7f, 1.3f, Percent));
            settings.Add(AngleSetting(HandsTab, "Hand angle", c.HandAngle, 5f, 30f));
            c.HandOpacity.SettingChanged += delegate { s.Hands.SetOpacity(c.HandOpacity.Value); };
            settings.Add(NumberSetting(HandsTab, "Hand opacity", c.HandOpacity, 0.05f, 0.01f, 1f, PercentOrOff));
            c.LeftHanded.SettingChanged += delegate { s.Input.SetLeftHanded(c.LeftHanded.Value); };
            settings.Add(ToggleSetting(HandsTab, "Left-handed", c.LeftHanded));
            if (s.Grabber != null)
            {
                c.CardGrabReach.SettingChanged += delegate { s.Grabber.SetReach(c.CardGrabReach.Value); };
                settings.Add(NumberSetting(HandsTab, "Grab reach", c.CardGrabReach, 0.01f, 0.02f, 0.3f, Centimeters));
                c.TableStopsHands.SettingChanged += delegate { s.Grabber.SetTableStopsHands(c.TableStopsHands.Value); };
                settings.Add(ToggleSetting(HandsTab, "Table stops hands", c.TableStopsHands));
                c.CardHoverGlow.SettingChanged += delegate { s.Grabber.SetCardHoverGlow(c.CardHoverGlow.Value); };
                c.InteractableGlow.SettingChanged += delegate { GrabGlow.Enabled = c.InteractableGlow.Value; };
                settings.Add(ToggleSetting(HandsTab, "Interactable glow", c.InteractableGlow));
                settings.Add(ToggleSetting(HandsTab, "Card glow", c.CardHoverGlow));
            }
            if (s.Pointer != null)
            {
                c.EnableLaser.SettingChanged += delegate { s.Pointer.SetLaser(c.EnableLaser.Value); };
                settings.Add(ToggleSetting(HandsTab, "Laser", c.EnableLaser));
            }

            if (s.Grabber != null)
            {
                c.LanePreviewStyle.SettingChanged += delegate { s.Grabber.SetLanePreview(c.LanePreviewStyle.Value); };
                settings.Add(ChoiceSetting(TableTab, "Lane preview", c.LanePreviewStyle, LanePreview.Styles));
                c.CardRowLanes.SettingChanged += delegate { s.Grabber.SetCardRowLanes(c.CardRowLanes.Value); };
                settings.Add(NumberSetting(TableTab, "Card row distance", c.CardRowLanes, 0.1f, 0.8f, 3f, delegate(float value) { return value.ToString("0.0") + " lanes"; }));
                c.CardRowOverlap.SettingChanged += delegate { s.Grabber.SetCardRowOverlap(c.CardRowOverlap.Value); };
                settings.Add(NumberSetting(TableTab, "Card overlap", c.CardRowOverlap, 0.05f, 0f, 0.8f, Percent));
                c.CardRowHover.SettingChanged += delegate { s.Grabber.SetCardRowHover(c.CardRowHover.Value); };
                settings.Add(ChoiceSetting(TableTab, "Card hover", c.CardRowHover, CardRow.HoverStyles));
                settings.Add(ToggleSetting(TableTab, "Simplified events", c.SimplifiedEvents));
                if (c.EnableSacrificeKnife.Value)
                {
                    EventHandler sacrificeGaze = delegate { s.Grabber.ConfigureSacrificeGaze(c.SacrificeShake.Value, c.SacrificeMarker.Value, c.SacrificeHeartbeat.Value); };
                    c.SacrificeShake.SettingChanged += sacrificeGaze;
                    c.SacrificeMarker.SettingChanged += sacrificeGaze;
                    c.SacrificeHeartbeat.SettingChanged += sacrificeGaze;
                    settings.Add(ChoiceSetting(TableTab, "Sacrifice shake", c.SacrificeShake, SacrificeGaze.ShakeModes));
                    settings.Add(ChoiceSetting(TableTab, "Sacrifice marker", c.SacrificeMarker, SacrificeGaze.MarkerModes));
                    settings.Add(ToggleSetting(TableTab, "Heartbeat", c.SacrificeHeartbeat));
                }
                c.EnableCardInspection.SettingChanged += delegate { s.Grabber.SetInspection(c.EnableCardInspection.Value); };
                settings.Add(ToggleSetting(TableTab, "Card info panel", c.EnableCardInspection));
                if (c.EnableRuleBookHands.Value)
                {
                    c.RuleBookWidth.SettingChanged += delegate { s.Grabber.SetRuleBookWidth(c.RuleBookWidth.Value); };
                    settings.Add(NumberSetting(TableTab, "Rule book size", c.RuleBookWidth, 0.05f, 0.2f, 0.9f, Centimeters));
                }
                if (c.EnableBellTouch.Value)
                {
                    c.BellMinSlapSpeed.SettingChanged += delegate { s.Grabber.SetBellMinSlapSpeed(c.BellMinSlapSpeed.Value); };
                    settings.Add(NumberSetting(TableTab, "Bell slap speed", c.BellMinSlapSpeed, 0.25f, 0f, 5f,
                        delegate(float value) { return value <= 0f ? "Any touch" : value.ToString("0.00") + " m/s"; }));
                }
            }
            if (s.Haptics != null)
            {
                c.GameHapticStrength.SettingChanged += delegate { s.Haptics.SetStrength(c.GameHapticStrength.Value); };
                c.VoiceHapticStrength.SettingChanged += delegate { s.Haptics.SetVoiceStrength(c.VoiceHapticStrength.Value); };
                settings.Add(NumberSetting(TableTab, "Game haptics", c.GameHapticStrength, 0.1f, 0f, 1f, PercentOrOff));
                settings.Add(NumberSetting(TableTab, "Voice rumble", c.VoiceHapticStrength, 0.05f, 0f, 1f, PercentOrOff));
            }

            c.SeatHeight.SettingChanged += delegate { s.Stereo.SetSeatHeight(c.SeatHeight.Value); };
            settings.Add(NumberSetting(ViewTab, "Seat height", c.SeatHeight, 0.02f, -0.5f, 0.5f, SignedCentimeters));
            c.WorldUnitsPerMeter.SettingChanged += delegate { s.Stereo.SetWorldUnitsPerMeter(c.WorldUnitsPerMeter.Value); };
            settings.Add(WorldSizeSetting(ViewTab, c.WorldUnitsPerMeter));
            c.PosePredictionFrames.SettingChanged += delegate { s.Stereo.SetPosePrediction(c.PosePredictionFrames.Value); };
            settings.Add(NumberSetting(ViewTab, "Pose prediction", c.PosePredictionFrames, 0.5f, 0f, 3f,
                delegate(float value) { return value <= 0f ? "Off" : value.ToString("0.0") + " fr"; }));
            c.SnapTurnDegrees.SettingChanged += delegate { s.Walk.SetSnapDegrees(c.SnapTurnDegrees.Value); };
            settings.Add(StepsSetting(ViewTab, "Snap turn", c.SnapTurnDegrees, SnapAngles, delegate(float value) { return value.ToString("0") + "°"; }));
            c.BossCloseupMode.SettingChanged += delegate { s.Closeup.SetMode(c.BossCloseupMode.Value); };
            settings.Add(ChoiceSetting(ViewTab, "Boss closeup", c.BossCloseupMode, BossCloseup.Modes));
            c.BossCloseupDistance.SettingChanged += delegate { s.Closeup.SetDistance(c.BossCloseupDistance.Value); };
            settings.Add(NumberSetting(ViewTab, "Closeup distance", c.BossCloseupDistance, 0.05f, 0.2f, 3f, Meters));
            c.EndlessSceneryLayers.SettingChanged += delegate { s.Scenery.Refresh(); };
            c.Optimization.SettingChanged += delegate { s.Scenery.Refresh(); };
            settings.Add(NumberSetting(ViewTab, "Boss scenery rows", c.EndlessSceneryLayers, 1f, 0f, 12f,
                delegate(float value) { return value < 0.5f ? "Off" : "+" + Mathf.RoundToInt(value); }));
            c.MoonVoid.SettingChanged += delegate { s.Moon.SetEnabled(c.MoonVoid.Value); };
            settings.Add(ToggleSetting(ViewTab, "Moon void", c.MoonVoid));
            if (s.Screen != null)
            {
                c.MenuScreenWidth.SettingChanged += delegate { s.Screen.SetWidth(c.MenuScreenWidth.Value); };
                c.MenuScreenDistance.SettingChanged += delegate { s.Screen.SetDistance(c.MenuScreenDistance.Value); };
                settings.Add(NumberSetting(ViewTab, "Menu screen size", c.MenuScreenWidth, 0.2f, 0.8f, 6f, Meters));
                settings.Add(NumberSetting(ViewTab, "Menu distance", c.MenuScreenDistance, 0.25f, 1f, 5f, Meters));
            }
            c.EyeTracking.SettingChanged += delegate { s.Eyes.SetEnabled(c.EyeTracking.Value); };
            VrSetting eyes = ToggleSetting(ViewTab, "Eye tracking", c.EyeTracking);
            eyes.Value = delegate { return c.EyeTracking.Value ? "ON (" + EyeGaze.Status + ")" : "OFF"; };
            settings.Add(eyes);
            settings.Add(ButtonSetting(ViewTab, "Recenter view", s.Stereo.Recenter));

            c.EnableHeadTrackedAudio.SettingChanged += delegate { s.Audio.SetEnabled(c.EnableHeadTrackedAudio.Value); };
            settings.Add(ToggleSetting(AudioTab, "3D audio", c.EnableHeadTrackedAudio));
            c.EnableVrSubtitles.SettingChanged += delegate { s.Dialogue.SetSubtitles(c.EnableVrSubtitles.Value); };
            settings.Add(ToggleSetting(AudioTab, "Subtitles", c.EnableVrSubtitles));
            c.SubtitleStyle.SettingChanged += delegate { s.Dialogue.SetStyle(c.SubtitleStyle.Value); };
            settings.Add(ChoiceSetting(AudioTab, "Subtitle style", c.SubtitleStyle, VrDialogue.Styles));
            c.SubtitleHeight.SettingChanged += delegate { s.Dialogue.SetHeight(c.SubtitleHeight.Value); };
            settings.Add(NumberSetting(AudioTab, "Subtitle height", c.SubtitleHeight, 0.05f, -0.5f, 1.5f, SignedCentimeters));
            c.AutoAdvancePace.SettingChanged += delegate { s.Dialogue.SetAutoAdvance(c.AutoAdvancePace.Value); };
            settings.Add(AutoAdvanceSetting(AudioTab, c.AutoAdvancePace));
            c.WaveSkipsDialogue.SettingChanged += delegate { s.Skip.SetEnabled(c.WaveSkipsDialogue.Value); };
            settings.Add(ToggleSetting(AudioTab, "Wave to skip", c.WaveSkipsDialogue));
            c.LeshyLooksAtYou.SettingChanged += delegate { s.Gaze.SetEnabled(c.LeshyLooksAtYou.Value); };
            settings.Add(ToggleSetting(AudioTab, "Leshy looks at you", c.LeshyLooksAtYou));
            c.LeshyNoticesStare.SettingChanged += delegate { s.Gaze.SetNotice(c.LeshyNoticesStare.Value); };
            settings.Add(ToggleSetting(AudioTab, "Leshy notices stare", c.LeshyNoticesStare));
            c.LeshyWatchesSacrifice.SettingChanged += delegate { s.Gaze.SetWatchSacrifice(c.LeshyWatchesSacrifice.Value); };
            settings.Add(ToggleSetting(AudioTab, "Leshy watches victim", c.LeshyWatchesSacrifice));
            c.ShowDialogueBars.SettingChanged += delegate { s.Stereo.SetLetterbox(c.ShowDialogueBars.Value); };
            settings.Add(ToggleSetting(AudioTab, "Black bars", c.ShowDialogueBars));
            c.ShowTips.SettingChanged += delegate { s.Tips.SetEnabled(c.ShowTips.Value); };
            settings.Add(ToggleSetting(AudioTab, "Tips", c.ShowTips));
            settings.Add(ButtonSetting(AudioTab, "Reset tips", s.Tips.ResetSeen));

            EventHandler optimize = delegate { s.Stereo.SetOptimization(c.Optimization.Value, c.HalfFramerate.Value); };
            c.Optimization.SettingChanged += optimize;
            c.HalfFramerate.SettingChanged += optimize;
            c.Optimization.SettingChanged += delegate
            {
                float cap = Optimization.RenderScaleCap(Optimization.Level(c.Optimization.Value));
                if (cap > 0f && c.RenderScale.Value > cap) c.RenderScale.Value = cap;
            };
            settings.Add(ChoiceSetting(PerformanceTab, "Optimization", c.Optimization, Optimization.Levels));
            settings.Add(HalfFramerateSetting(PerformanceTab, c.HalfFramerate, c.Optimization));
            c.RenderScale.SettingChanged += delegate { s.Stereo.SetRenderScale(c.RenderScale.Value); };
            settings.Add(NumberSetting(PerformanceTab, "Render quality", c.RenderScale, 0.05f, StereoRenderer.MinRenderScale, StereoRenderer.MaxRenderScale, Percent));
            c.DesktopMirror.SettingChanged += delegate { s.Stereo.SetDesktopMirror(c.DesktopMirror.Value); };
            settings.Add(ToggleSetting(PerformanceTab, "Monitor shows VR", c.DesktopMirror));
            c.LogRenderTiming.SettingChanged += delegate { s.Timing.SetEnabled(c.LogRenderTiming.Value); };
            settings.Add(ToggleSetting(PerformanceTab, "Timing log", c.LogRenderTiming));
            return settings;
        }

        private static VrSetting HalfFramerateSetting(string tab, ConfigEntry<string> entry, ConfigEntry<string> level)
        {
            VrSetting setting = ChoiceSetting(tab, "Half framerate", entry, Optimization.HalfRateModes);
            setting.Value = delegate
            {
                if (entry.Value != Optimization.Auto) return entry.Value;
                return "Auto (" + (Optimization.HalfRate(Optimization.Level(level.Value), Optimization.Auto) ? "On" : "Off") + ")";
            };
            return setting;
        }

        private static VrSetting NumberSetting(string tab, string label, ConfigEntry<float> entry, float step, float min, float max, Func<float, string> format)
        {
            var setting = new VrSetting();
            setting.Tab = tab;
            setting.Label = label;
            setting.Value = delegate { return format(entry.Value); };
            setting.Step = delegate(int direction)
            {
                float next = Mathf.Clamp(Mathf.Round((entry.Value + direction * step) / step) * step, min, max);
                entry.Value = (float)Math.Round(next, 3);
            };
            float reset = (float)entry.DefaultValue;
            setting.Activate = delegate { entry.Value = reset; };
            return setting;
        }

        private static VrSetting WorldSizeSetting(string tab, ConfigEntry<float> entry)
        {
            var setting = new VrSetting();
            setting.Tab = tab;
            setting.Label = "World size";
            setting.Value = delegate { return Mathf.RoundToInt(VrConfig.DefaultWorldUnitsPerMeter / entry.Value * 100f) + "%"; };
            setting.Step = delegate(int direction)
            {
                float percent = Mathf.Round(VrConfig.DefaultWorldUnitsPerMeter / entry.Value * 20f) / 20f + direction * 0.05f;
                percent = Mathf.Clamp(percent, 0.5f, 2f);
                entry.Value = (float)Math.Round(VrConfig.DefaultWorldUnitsPerMeter / percent, 3);
            };
            setting.Activate = delegate { entry.Value = VrConfig.DefaultWorldUnitsPerMeter; };
            return setting;
        }

        private const float AutoAdvanceStep = 0.25f;
        private const float AutoAdvanceFirstOn = 1f;
        private const float AutoAdvanceMin = 0.5f;
        private const float AutoAdvanceMax = 2f;

        private static VrSetting AutoAdvanceSetting(string tab, ConfigEntry<float> entry)
        {
            var setting = new VrSetting();
            setting.Tab = tab;
            setting.Label = "Auto-advance";
            setting.Value = delegate { return entry.Value <= 0f ? "Off" : "Pace " + entry.Value.ToString("0.00") + "x"; };
            setting.Step = delegate(int direction)
            {
                if (entry.Value <= 0f)
                {
                    if (direction > 0) entry.Value = AutoAdvanceFirstOn;
                    return;
                }
                float next = Mathf.Round((entry.Value + direction * AutoAdvanceStep) / AutoAdvanceStep) * AutoAdvanceStep;
                entry.Value = next < AutoAdvanceMin - 0.001f ? 0f : (float)Math.Round(Mathf.Min(next, AutoAdvanceMax), 3);
            };
            float reset = (float)entry.DefaultValue;
            setting.Activate = delegate { entry.Value = reset; };
            return setting;
        }

        private static VrSetting AngleSetting(string tab, string label, ConfigEntry<float> entry, float step, float limit)
        {
            var setting = new VrSetting();
            setting.Tab = tab;
            setting.Label = label;
            setting.Value = delegate { return (entry.Value > 0f ? "+" : "") + entry.Value.ToString("0") + "°"; };
            setting.Step = delegate(int direction)
            {
                entry.Value = Mathf.Clamp(Mathf.Round((entry.Value + direction * step) / step) * step, -limit, limit);
            };
            float reset = (float)entry.DefaultValue;
            setting.Activate = delegate { entry.Value = reset; };
            return setting;
        }

        private static VrSetting StepsSetting(string tab, string label, ConfigEntry<float> entry, float[] options, Func<float, string> format)
        {
            var setting = new VrSetting();
            setting.Tab = tab;
            setting.Label = label;
            setting.Value = delegate { return format(entry.Value); };
            setting.Step = delegate(int direction)
            {
                int nearest = 0;
                for (int i = 1; i < options.Length; i++)
                    if (Mathf.Abs(options[i] - entry.Value) < Mathf.Abs(options[nearest] - entry.Value)) nearest = i;
                entry.Value = options[Mathf.Clamp(nearest + direction, 0, options.Length - 1)];
            };
            float reset = (float)entry.DefaultValue;
            setting.Activate = delegate { entry.Value = reset; };
            return setting;
        }

        private static VrSetting ChoiceSetting(string tab, string label, ConfigEntry<string> entry, string[] choices)
        {
            var setting = new VrSetting();
            setting.Tab = tab;
            setting.Label = label;
            setting.Value = delegate { return entry.Value; };
            setting.Step = delegate(int direction)
            {
                int index = Array.IndexOf(choices, entry.Value);
                if (index < 0) index = 0;
                entry.Value = choices[(index + direction + choices.Length) % choices.Length];
            };
            return setting;
        }

        private static VrSetting ToggleSetting(string tab, string label, ConfigEntry<bool> entry)
        {
            var setting = new VrSetting();
            setting.Tab = tab;
            setting.Label = label;
            setting.Value = delegate { return entry.Value ? "ON" : "OFF"; };
            setting.Activate = delegate { entry.Value = !entry.Value; };
            return setting;
        }

        private static VrSetting ButtonSetting(string tab, string label, Action action)
        {
            var setting = new VrSetting();
            setting.Tab = tab;
            setting.Label = label;
            setting.Activate = action;
            return setting;
        }

        private static string Percent(float value) { return Mathf.RoundToInt(value * 100f) + "%"; }

        private static string PercentOrOff(float value) { return value <= 0f ? "Off" : Percent(value); }

        private static string Centimeters(float value) { return Mathf.RoundToInt(value * 100f) + " cm"; }

        private static string SignedCentimeters(float value)
        {
            int cm = Mathf.RoundToInt(value * 100f);
            return (cm > 0 ? "+" : "") + cm + " cm";
        }

        private static string Meters(float value) { return value.ToString("0.00") + " m"; }
    }
}
