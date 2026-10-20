using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace DaveTheDiverCoop;

// A biting fish (shark etc.) works on "the player". When it bites player 2,
// run that fish's bite code, the bite struggle state and the button-mash bar
// as player 2's turn, so player 2 is the one held and player 2's B presses
// break free.
internal static class BiteRedirect
{
    private static readonly HashSet<IntPtr> _onPlayer2 = new();
    private static int _logs;

    internal static bool Active => _onPlayer2.Count > 0 && SecondDiver.Player2 != null;

    internal static bool Targets(Component fish) => fish != null && _onPlayer2.Contains(fish.Pointer);

    internal static void Mark(Component fish, Collider2D other)
    {
        var p2 = SecondDiver.Player2;
        if (fish == null || other == null || p2 == null)
            return;
        if (!other.transform.IsChildOf(p2.transform))
        {
            // Touching player 1 again: this fish's next bite is player 1's.
            var p1 = SecondDiver.Player1;
            if (p1 != null && other.transform.IsChildOf(p1.transform))
                _onPlayer2.Remove(fish.Pointer);
            return;
        }
        if (_onPlayer2.Add(fish.Pointer) && _logs++ < 10)
            Plugin.Logger.LogInfo($"Bite: {fish.name} touched player 2; its bite now targets player 2");
    }

    internal static void Unmark(Component fish)
    {
        if (fish != null && _onPlayer2.Remove(fish.Pointer) && _logs++ < 10)
            Plugin.Logger.LogInfo($"Bite: {fish.name} finished with player 2");
    }

    internal static void Clear() => _onPlayer2.Clear();

    internal static bool Begin(Component fish)
    {
        var p2 = SecondDiver.Player2;
        if (p2 == null || !Targets(fish))
            return false;
        SecondDiver.BeginPlayer2Turn(p2);
        return true;
    }

    internal static void End(bool began)
    {
        if (began && SecondDiver.Player2 != null)
            SecondDiver.EndPlayer2Turn(SecondDiver.Player2);
    }

    // For the mash bar (not tied to a fish): player 2's turn while any bite
    // is on player 2, with player 2's keys answering.
    internal static bool BeginAny()
    {
        var p2 = SecondDiver.Player2;
        if (!Active || p2 == null)
            return false;
        SecondDiver.BeginPlayer2Turn(p2);
        return true;
    }
}

[HarmonyPatch(typeof(DR.AI.Biteable), nameof(DR.AI.Biteable.OnTriggerEnter2D))]
internal static class BiteTrigger
{
    private static void Prefix(DR.AI.Biteable __instance, Collider2D __0, ref bool __state)
    {
        BiteRedirect.Mark(__instance, __0);
        __state = BiteRedirect.Begin(__instance);
    }
    private static void Postfix(bool __state) => BiteRedirect.End(__state);
}

[HarmonyPatch(typeof(DR.AI.Biteable), nameof(DR.AI.Biteable.Update))]
internal static class BiteUpdate
{
    private static void Prefix(DR.AI.Biteable __instance, ref bool __state) => __state = BiteRedirect.Begin(__instance);
    private static void Postfix(bool __state) => BiteRedirect.End(__state);
}

[HarmonyPatch(typeof(DR.AI.Biteable), nameof(DR.AI.Biteable.HitBiteDamage))]
internal static class BiteDamage
{
    private static void Prefix(DR.AI.Biteable __instance, ref bool __state) => __state = BiteRedirect.Begin(__instance);
    private static void Postfix(bool __state) => BiteRedirect.End(__state);
}

[HarmonyPatch(typeof(DR.AI.Biteable), nameof(DR.AI.Biteable.OpenQTE))]
internal static class BiteOpenQte
{
    // Biting grabs (mako, porbeagle, squid...): with grabs off in co-op, the
    // bite still hits but the hold/struggle is skipped and the bite ends.
    private static bool Prefix(DR.AI.Biteable __instance, ref bool __state)
    {
        __state = false;
        if (NoSharkGrab.Skip("bite hold", NoSharkGrab.FishName(__instance), BiteRedirect.Targets(__instance), __instance))
        {
            try { __instance.BiteSkillEnd(); }
            catch (Exception exception) { Plugin.Logger.LogWarning($"Bite end after skipped hold: {exception.Message}"); }
            BiteRedirect.Unmark(__instance);
            return false;
        }
        __state = BiteRedirect.Begin(__instance);
        return true;
    }
    private static void Postfix(bool __state) => BiteRedirect.End(__state);
}

[HarmonyPatch(typeof(DR.AI.Biteable), nameof(DR.AI.Biteable.StartResultFail))]
internal static class BiteFailStart
{
    private static void Prefix(DR.AI.Biteable __instance, ref bool __state) => __state = BiteRedirect.Begin(__instance);
    private static void Postfix(bool __state) => BiteRedirect.End(__state);
}

[HarmonyPatch(typeof(DR.AI.Biteable), nameof(DR.AI.Biteable.EndResultFail))]
internal static class BiteFailEnd
{
    private static void Prefix(DR.AI.Biteable __instance, ref bool __state) => __state = BiteRedirect.Begin(__instance);
    private static void Postfix(DR.AI.Biteable __instance, bool __state)
    {
        BiteRedirect.End(__state);
        BiteRedirect.Unmark(__instance);
    }
}

[HarmonyPatch(typeof(DR.AI.Biteable), nameof(DR.AI.Biteable.BiteSkillEnd))]
internal static class BiteSkillEnd
{
    private static void Prefix(DR.AI.Biteable __instance, ref bool __state) => __state = BiteRedirect.Begin(__instance);
    private static void Postfix(DR.AI.Biteable __instance, bool __state)
    {
        BiteRedirect.End(__state);
        BiteRedirect.Unmark(__instance);
    }
}

[HarmonyPatch(typeof(DR.AI.Biteable), nameof(DR.AI.Biteable.SettingSkillBite))]
internal static class BiteSetting
{
    private static void Prefix(DR.AI.Biteable __instance, ref bool __state) => __state = BiteRedirect.Begin(__instance);
    private static void Postfix(bool __state) => BiteRedirect.End(__state);
}

[HarmonyPatch(typeof(DR.AI.BiteQTEState), nameof(DR.AI.BiteQTEState.OnEnter))]
internal static class BiteStateEnter
{
    private static void Prefix(ref bool __state) => __state = BiteRedirect.BeginAny();
    private static void Postfix(bool __state) => BiteRedirect.End(__state);
}

[HarmonyPatch(typeof(DR.AI.BiteQTEState), nameof(DR.AI.BiteQTEState.OnUpdate))]
internal static class BiteStateUpdate
{
    private static void Prefix(ref bool __state) => __state = BiteRedirect.BeginAny();
    private static void Postfix(bool __state) => BiteRedirect.End(__state);
}

[HarmonyPatch(typeof(DR.AI.BiteQTEState), nameof(DR.AI.BiteQTEState.OnExit))]
internal static class BiteStateExit
{
    private static void Prefix(ref bool __state) => __state = BiteRedirect.BeginAny();
    private static void Postfix(bool __state) => BiteRedirect.End(__state);
}

// The mash bar: read player 2's keys while player 2 is the one bitten, and
// count player 2's B presses as defending.
[HarmonyPatch(typeof(PushButtonQTE), nameof(PushButtonQTE.Update))]
internal static class BiteMashUpdate
{
    private static int _logs;

    private static void Prefix(PushButtonQTE __instance, ref bool __state)
    {
        __state = BiteRedirect.BeginAny();
        if (!__state)
            return;
        if (P2Keys.Pressed(P2Keys.Action.Back) || P2Keys.Pressed(P2Keys.Action.Ok))
        {
            try
            {
                __instance.OnDeffecneAttack();
                if (_logs++ < 5)
                    Plugin.Logger.LogInfo("Bite: player 2 mash press counted");
            }
            catch (Exception exception)
            {
                if (_logs++ < 5)
                    Plugin.Logger.LogWarning($"Bite: mash press failed: {exception.Message}");
            }
        }
    }

    private static void Postfix(bool __state) => BiteRedirect.End(__state);
}

[HarmonyPatch(typeof(PushButtonQTE), nameof(PushButtonQTE.Initialize))]
internal static class BiteMashInit
{
    private static void Prefix(ref bool __state) => __state = BiteRedirect.BeginAny();
    private static void Postfix(bool __state) => BiteRedirect.End(__state);
}

[HarmonyPatch(typeof(PushButtonQTE), nameof(PushButtonQTE.Finish))]
internal static class BiteMashFinish
{
    private static void Prefix(ref bool __state) => __state = BiteRedirect.BeginAny();
    private static void Postfix(bool __state) => BiteRedirect.End(__state);
}
