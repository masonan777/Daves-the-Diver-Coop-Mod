using System;
using HarmonyLib;
using UnityEngine;
using ActionName = DRInput.DRInputAsset.ActionMap_InGame.ActionName;
using InGameInput = InputSystemWrapper.InputAsset<DRInput.DRInputAsset.SchemeName, DRInput.DRInputAsset.MapName>
    .ActionMap<DRInput.DRInputAsset.ActionMap_InGame.ActionName>.Input;
using Phase = InputSystemWrapper.ActionPhase;

namespace DaveTheDiverCoop;

// Dave reads all his controls in PlayerCharacter.OnDirectHandler(input) by
// asking the input object questions ("was Fire pressed?"). Player 2's diver is
// handed player 1's input object once per frame, and while it is reading, those
// questions are answered from player 2's keys instead.
internal static class P2Dispatch
{
    private static InGameInput _input;
    private static int _dispatchFrame = -1;
    private static bool _loggedFirst;

    internal static bool Active { get; set; }

    internal static void Capture(InGameInput input)
    {
        if (input != null)
            _input = input;
    }

    internal static void Clear() => _input = null;

    // Player 1's real aim button (outside player 2's turn).
    internal static bool P1AimHeld()
    {
        if (_input == null || Active)
            return false;
        try
        {
            var phase = _input.GetPhase(ActionName.Aim);
            return phase == Phase.Started || phase == Phase.Performed || phase == Phase.Holding;
        }
        catch
        {
            return false;
        }
    }

    internal static void Dispatch(PlayerCharacter p2)
    {
        if (p2 == null || _input == null || _dispatchFrame == Time.frameCount)
            return;
        _dispatchFrame = Time.frameCount;
        Active = true;
        try
        {
            p2.OnDirectHandler(_input);
            if (!_loggedFirst)
            {
                _loggedFirst = true;
                Plugin.Logger.LogInfo("Player 2 controls are now fed through the game's own input handling");
            }
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning($"Player 2 input dispatch failed: {exception.Message}");
        }
        finally
        {
            Active = false;
        }
    }

    // Game action -> player 2 key. Menu is left out so player 2 cannot open
    // player 1's menu.
    private static bool TryMap(ActionName action, out P2Keys.Action key)
    {
        switch (action)
        {
            case ActionName.Interaction:
            case ActionName.InteractionP2:
                key = P2Keys.Action.Interact;
                return true;
            case ActionName.Aim:
            case ActionName.QTE:
            case ActionName.QTE_Harpoon:
                key = P2Keys.Action.Ok;
                return true;
            case ActionName.Fire:
            case ActionName.FireHookGun:
            case ActionName.Grab:
            case ActionName.FireItem:
                key = P2Keys.Action.Fire;
                return true;
            case ActionName.Melee:
                key = P2Keys.Action.Back;
                return true;
            case ActionName.LongDash:
            case ActionName.ActiveItem:
                key = P2Keys.Action.Dash;
                return true;
            case ActionName.ShortDash:
                key = P2Keys.Action.ShortDash;
                return true;
            case ActionName.Item:
            case ActionName.BreakItem:
                key = P2Keys.Action.Item;
                return true;
            case ActionName.SwitchItem:
                key = P2Keys.Action.SwitchItem;
                return true;
            case ActionName.SwitchWeapon:
                key = P2Keys.Action.SwitchWeapon;
                return true;
            default:
                key = default;
                return false;
        }
    }

    internal static bool Answer(Phase phase, ActionName action)
    {
        if (!TryMap(action, out var key))
            return false;
        return phase switch
        {
            Phase.Started or Phase.Performed => P2Keys.Pressed(key),
            Phase.Canceled => P2Keys.Released(key),
            Phase.Holding => P2Keys.Held(key),
            _ => false,
        };
    }

    internal static bool Held(ActionName action, float seconds) =>
        TryMap(action, out var key) && P2Keys.Held(key) && P2Keys.HeldSeconds(key) >= seconds;

    internal static Phase PhaseOf(ActionName action)
    {
        if (!TryMap(action, out var key))
            return Phase.None;
        if (P2Keys.Pressed(key))
            return Phase.Started;
        if (P2Keys.Released(key))
            return Phase.Canceled;
        return P2Keys.Held(key) ? Phase.Holding : Phase.None;
    }
}

// Remember player 1's input object; never let the game itself hand input to
// player 2's diver.
[HarmonyPatch(typeof(PlayerCharacter), nameof(PlayerCharacter.OnDirectHandler))]
internal static class PlayerInputCapturePatch
{
    private static bool Prefix(PlayerCharacter __instance, InGameInput __0)
    {
        if (SecondDiver.IsPlayer2(__instance))
            return P2Dispatch.Active;
        P2Dispatch.Capture(__0);
        return true;
    }
}

[HarmonyPatch(typeof(InGameInput), "OnAction_Impl")]
internal static class InputActionImplPatch
{
    private static bool Prefix(ref bool __result, Phase __0, ActionName __1)
    {
        if (!P2Dispatch.Active)
            return !FakeP1.Answer(__0, __1, ref __result);
        __result = P2Dispatch.Answer(__0, __1);
        return false;
    }
}

[HarmonyPatch(typeof(InGameInput), "OnHold_Impl")]
internal static class InputHoldImplPatch
{
    private static bool Prefix(ref bool __result, float __0, ActionName __1)
    {
        if (!P2Dispatch.Active)
            return !FakeP1.Hold(__1, __0, ref __result);
        __result = P2Dispatch.Held(__1, __0);
        return false;
    }
}

[HarmonyPatch(typeof(InGameInput), "OnAction", new[] { typeof(ActionName) })]
internal static class InputActionPatch
{
    private static bool Prefix(ref bool __result, ActionName __0)
    {
        if (!P2Dispatch.Active)
            return !FakeP1.Answer(Phase.Started, __0, ref __result);
        __result = P2Dispatch.Answer(Phase.Started, __0);
        return false;
    }
}

[HarmonyPatch(typeof(InGameInput), "OnCancel", new[] { typeof(ActionName) })]
internal static class InputCancelPatch
{
    private static bool Prefix(ref bool __result, ActionName __0)
    {
        if (!P2Dispatch.Active)
            return !FakeP1.Answer(Phase.Canceled, __0, ref __result);
        __result = P2Dispatch.Answer(Phase.Canceled, __0);
        return false;
    }
}

[HarmonyPatch(typeof(InGameInput), "OnHold", new[] { typeof(ActionName) })]
internal static class InputHoldPatch
{
    private static bool Prefix(ref bool __result, ActionName __0)
    {
        if (!P2Dispatch.Active)
            return !FakeP1.Answer(Phase.Holding, __0, ref __result);
        __result = P2Dispatch.Answer(Phase.Holding, __0);
        return false;
    }
}

[HarmonyPatch(typeof(InGameInput), "GetPhase")]
internal static class InputPhasePatch
{
    private static bool Prefix(ref Phase __result, ActionName __0)
    {
        if (!P2Dispatch.Active)
            return !FakeP1.PhaseOf(__0, ref __result);
        __result = P2Dispatch.PhaseOf(__0);
        return false;
    }
}

// Test bot: holds player 1's aim button by answering player 1's own input
// questions (only while the bot asks for it).
internal static class FakeP1
{
    private static bool _held;
    private static int _pressFrame = -1, _releaseFrame = -1;
    private static float _since;

    internal static void Press()
    {
        _held = true;
        _pressFrame = Time.frameCount;
        _since = Time.realtimeSinceStartup;
    }

    internal static void Release()
    {
        if (!_held)
            return;
        _held = false;
        _releaseFrame = Time.frameCount;
    }

    private static bool Mine(ActionName action) => action == ActionName.Aim && (_held || _releaseFrame == Time.frameCount);

    internal static bool Answer(Phase phase, ActionName action, ref bool result)
    {
        if (!Mine(action))
            return false;
        result = phase switch
        {
            Phase.Started or Phase.Performed => _pressFrame == Time.frameCount,
            Phase.Canceled => _releaseFrame == Time.frameCount,
            Phase.Holding => _held,
            _ => false,
        };
        return true;
    }

    internal static bool Hold(ActionName action, float seconds, ref bool result)
    {
        if (!Mine(action))
            return false;
        result = _held && Time.realtimeSinceStartup - _since >= seconds;
        return true;
    }

    internal static bool PhaseOf(ActionName action, ref Phase result)
    {
        if (!Mine(action))
            return false;
        result = _pressFrame == Time.frameCount ? Phase.Started
            : _releaseFrame == Time.frameCount ? Phase.Canceled
            : _held ? Phase.Holding : Phase.None;
        return true;
    }
}
