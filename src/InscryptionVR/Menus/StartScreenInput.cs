using System;
using System.Reflection;
using BepInEx.Logging;
using DiskCardGame;
using UnityEngine;
using UiButton = UnityEngine.UI.Button;

namespace InscryptionVR
{
    internal static class StartScreenInput
    {
        private const string Lambda = "<StartSequence>b__";
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic;

        private static ManualLogSource _logger;
        private static VrInput _input;
        private static FieldInfo _startButton;
        private static FieldInfo _slottedCard;
        private static MethodInfo _creditsExit;

        internal static void Install(ManualLogSource logger, VrInput input)
        {
            _logger = logger;
            _input = input;
            try
            {
                AnyKeyWait().SetValue(null, new Func<bool>(Pressed));
                logger.LogInfo("Title screen: either trigger continues where the game waits for any key.");
            }
            catch (Exception exception)
            {
                logger.LogWarning("Title screen controller input unavailable; press a keyboard key there instead: " + exception.Message);
            }
            _startButton = typeof(FirstPlaySceneController).GetField("startButton", BindingFlags.Instance | Any);
            _slottedCard = typeof(MenuController).GetField("slottedCard", BindingFlags.Instance | Any);
            _creditsExit = typeof(MenuCreditsSceneController).GetMethod("ReturnToStartScreen", BindingFlags.Instance | Any, null, Type.EmptyTypes, null);
            if (_startButton == null || _slottedCard == null || _creditsExit == null)
                logger.LogWarning("Main menu controller shortcuts partly unavailable (first-launch start " + (_startButton != null) +
                    ", back out of a menu card " + (_slottedCard != null) + ", leave credits " + (_creditsExit != null) + ").");
        }

        internal static FieldInfo AnyKeyWait()
        {
            Type closures = typeof(StartScreenController).GetNestedType("<>c", Any);
            if (closures == null) throw new MissingMemberException("DiskCardGame.StartScreenController", "<>c");
            foreach (MethodInfo method in closures.GetMethods(BindingFlags.Instance | BindingFlags.DeclaredOnly | Any))
            {
                if (!method.Name.StartsWith(Lambda, StringComparison.Ordinal) || method.ReturnType != typeof(bool) || method.GetParameters().Length != 0) continue;
                FieldInfo field = closures.GetField("<>9__" + method.Name.Substring(Lambda.Length), BindingFlags.Static | Any);
                if (field != null && field.FieldType == typeof(Func<bool>)) return field;
            }
            throw new MissingFieldException(closures.FullName, "StartSequence any-key wait");
        }

        private static bool Pressed()
        {
            if (Input.anyKey) return true;
            if (_input == null) return false;
            _input.Refresh();
            return _input.Active && (_input.Left.TriggerEdge == 1 || _input.Right.TriggerEdge == 1);
        }

        internal static bool TryPressFirstPlayStart()
        {
            if (_startButton == null || VrScenes.Current != VrScenes.StartMenu) return false;
            FirstPlaySceneController first = UnityEngine.Object.FindObjectOfType<FirstPlaySceneController>();
            if (first == null || !first.isActiveAndEnabled) return false;
            UiButton button = _startButton.GetValue(first) as UiButton;
            if (button == null || !button.gameObject.activeInHierarchy || !button.enabled || !button.interactable) return false;
            if (button.onClick.GetPersistentEventCount() > 0) button.onClick.Invoke();
            else first.OnStartButtonPressed();
            if (_logger != null) _logger.LogInfo("First-launch start button pressed from VR.");
            return true;
        }

        internal static bool TryBack()
        {
            string scene = VrScenes.Current;
            if (scene == VrScenes.MenuCredits) return TryLeaveCredits();
            if (scene != VrScenes.StartMenu || _slottedCard == null) return false;
            MenuController menu = UnityEngine.Object.FindObjectOfType<MenuController>();
            if (menu == null || !menu.CardInSlot || menu.DoingCardTransition || menu.TransitioningToScene) return false;
            MenuCard card = _slottedCard.GetValue(menu) as MenuCard;
            if (card == null || !card.Slotted) return false;
            menu.OnCardSelected(card);
            VrLog.Chatter(_logger, "Main menu: backed out of " + card.MenuAction + " from VR.");
            return true;
        }

        private static bool TryLeaveCredits()
        {
            if (_creditsExit == null) return false;
            MenuCreditsSceneController credits = UnityEngine.Object.FindObjectOfType<MenuCreditsSceneController>();
            if (credits == null || !credits.enabled) return false;
            _creditsExit.Invoke(credits, null);
            if (_logger != null) _logger.LogInfo("Menu credits left from VR.");
            return true;
        }
    }
}
