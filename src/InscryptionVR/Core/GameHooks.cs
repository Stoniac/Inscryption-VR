using System;
using System.Collections.Generic;
using System.Reflection;
using DiskCardGame;
using UnityEngine;

namespace InscryptionVR
{
    internal static class GameHooks
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly FieldInfo CursorRayCamera = typeof(InteractionCursor).GetField("rayCamera", Members);
        private static readonly FieldInfo CursorCurrent = typeof(InteractionCursor).GetField("currentMainInputInteractable", Members);
        private static readonly FieldInfo CursorDown = typeof(InteractionCursor).GetField("cursorDownInteractable", Members);
        private static readonly FieldInfo CursorExcluded = typeof(InteractionCursor).GetField("excludedLayers", Members);
        private static readonly MethodInfo CursorDisabledWith = typeof(InteractionCursor).GetMethod("InteractionDisabledWith", Members);
        private static readonly MethodInfo CursorRaycast = GenericRaycast();
        private static readonly PropertyInfo PauseActiveCursor = typeof(PauseMenu).GetProperty("ActivePauseCursor", Members);
        private static readonly MethodInfo PauseSetPaused = typeof(PauseMenu).GetMethod("SetPaused", Members, null, new[] { typeof(bool) }, null);
        private static readonly FieldInfo PauseMenuController = typeof(PauseMenu).GetField("menuController", Members);
        private static readonly FieldInfo PauseClose = typeof(PauseMenu).GetField("closeMenuInteractable", Members);
        private static readonly FieldInfo MenuSlotted = typeof(MenuController).GetField("slottedCard", Members);
        private static readonly FieldInfo AudioActiveSfx = typeof(AudioController).GetField("activeSFX", Members);

        internal static readonly string Missing = FindMissing();

        internal static List<AudioSource> ActiveSfx()
        {
            AudioController audio = AudioController.Instance;
            return audio == null || AudioActiveSfx == null ? null : AudioActiveSfx.GetValue(audio) as List<AudioSource>;
        }

        private static MethodInfo GenericRaycast()
        {
            MethodInfo method = typeof(InteractionCursor).GetMethod("RaycastForInteractable", Members);
            return method != null && method.IsGenericMethodDefinition ? method.MakeGenericMethod(typeof(MainInputInteractable)) : null;
        }

        private static string FindMissing()
        {
            var missing = new List<string>();
            if (CursorRayCamera == null) missing.Add("InteractionCursor.rayCamera");
            if (CursorCurrent == null) missing.Add("InteractionCursor.currentMainInputInteractable");
            if (CursorDown == null) missing.Add("InteractionCursor.cursorDownInteractable");
            if (CursorExcluded == null) missing.Add("InteractionCursor.excludedLayers");
            if (CursorDisabledWith == null) missing.Add("InteractionCursor.InteractionDisabledWith");
            if (CursorRaycast == null) missing.Add("InteractionCursor.RaycastForInteractable");
            if (PauseActiveCursor == null) missing.Add("PauseMenu.ActivePauseCursor");
            if (PauseSetPaused == null) missing.Add("PauseMenu.SetPaused");
            if (PauseMenuController == null) missing.Add("PauseMenu.menuController");
            if (PauseClose == null) missing.Add("PauseMenu.closeMenuInteractable");
            if (MenuSlotted == null) missing.Add("MenuController.slottedCard");
            return missing.Count == 0 ? null : string.Join(", ", missing.ToArray());
        }

        internal static void Require()
        {
            if (Missing != null) throw new MissingMemberException("Inscryption cursor/pause contract changed: " + Missing);
        }

        internal static bool IsPaused
        {
            get
            {
                PauseMenu pause = PauseMenu.instance;
                if (pause == null) return false;
                if (pause.Paused || pause.Unpausing) return true;
                InteractionCursor cursor = PauseCursor(pause);
                return cursor != null && cursor.enabled && cursor.gameObject.activeInHierarchy;
            }
        }

        internal static InteractionCursor PauseCursor(PauseMenu pause)
        {
            return pause == null || PauseActiveCursor == null ? null : PauseActiveCursor.GetValue(pause, null) as InteractionCursor;
        }

        internal static void SetPaused(PauseMenu pause, bool paused) { PauseSetPaused.Invoke(pause, new object[] { paused }); }

        internal static MenuController PauseMenuControllerOf(PauseMenu pause) { return PauseMenuController.GetValue(pause) as MenuController; }

        internal static MainInputInteractable PauseCloseButton(PauseMenu pause) { return PauseClose.GetValue(pause) as MainInputInteractable; }

        internal static MenuCard SlottedCard(MenuController menu) { return MenuSlotted.GetValue(menu) as MenuCard; }

        internal static Camera RayCamera(InteractionCursor cursor) { return CursorRayCamera.GetValue(cursor) as Camera; }

        internal static MainInputInteractable Current(InteractionCursor cursor) { return CursorCurrent.GetValue(cursor) as MainInputInteractable; }

        internal static void SetCurrent(InteractionCursor cursor, MainInputInteractable target) { CursorCurrent.SetValue(cursor, target); }

        internal static MainInputInteractable Down(InteractionCursor cursor) { return CursorDown.GetValue(cursor) as MainInputInteractable; }

        internal static void SetDown(InteractionCursor cursor, MainInputInteractable target) { CursorDown.SetValue(cursor, target); }

        internal static bool Disabled(InteractionCursor cursor, InteractableBase target)
        {
            return (bool)CursorDisabledWith.Invoke(cursor, new object[] { target });
        }

        internal static int RaycastMask(InteractionCursor cursor)
        {
            var excluded = CursorExcluded.GetValue(cursor) as List<string>;
            return excluded == null ? Physics.DefaultRaycastLayers : ~LayerMask.GetMask(excluded.ToArray());
        }

        internal static MainInputInteractable RaycastAtCursor(InteractionCursor cursor)
        {
            return CursorRaycast.Invoke(cursor, new object[] { RaycastMask(cursor), cursor.transform.position }) as MainInputInteractable;
        }
    }
}
