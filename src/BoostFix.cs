using HarmonyLib;
using UnityEngine;

namespace DaveTheDiverCoop;

// Using a helper item (booster etc.) asks "where is the mouse relative to
// Dave?". For player 2 there is no mouse, the answer is empty and the game
// crashes. Answer with player 2's own screen position (no mouse movement).


// While the booster runs, the game checks every frame whether the dash button
// is still held, outside the normal button handling. For player 2 that check
// read player 1's keys and stopped the booster at once. Run player 2's check
// as player 2 (his keys, his turn).


internal static class BoostTrace
{
    private static int _logs;
    private static readonly System.Collections.Generic.HashSet<string> _seen = new();

    internal static void Log(PlayerCharacter who, string what, string detail)
    {
        if (who == null || _logs > 60)
            return;
        var name = SecondDiver.IsPlayer2(who) ? "P2" : "P1";
        if (name == "P1" && !P2Keys.Joined)
            return;
        var key = name + what + detail;
        if (!_seen.Add(key))
            return;
        _logs++;
        string slot;
        try
        {
            var items = who.GetComponent<SubHelperItemInventory>();
            var current = items?.currentSlot;
            slot = $"slot={(current != null ? "yes" : "none")} focusedIndex={items?.FocusedSlotIndex} booster={who.PlayerBoosterState}";
        }
        catch (System.Exception e) { slot = "slot read failed: " + e.Message; }
        Plugin.Logger.LogInfo($"BoostTrace {name} {what} {detail} {slot}");
    }

    internal static void Reset() => _seen.Clear();
}

[HarmonyPatch(typeof(PlayerCharacter), nameof(PlayerCharacter.OnLongDash_Performed))]
internal static class BoostTraceLongDash { private static void Prefix(PlayerCharacter __instance) { BoostTrace.Reset(); BoostTrace.Log(__instance, "long dash pressed", ""); } }

[HarmonyPatch(typeof(PlayerCharacter), nameof(PlayerCharacter.SetUpdateBoosterTrigger))]
internal static class BoostTraceSetTrigger { private static void Prefix(PlayerCharacter __instance, bool __0) => BoostTrace.Log(__instance, "booster trigger set", $"on={__0}"); }

[HarmonyPatch(typeof(PlayerCharacter), nameof(PlayerCharacter.StopBoosterTrigger))]
internal static class BoostTraceStop { private static void Prefix(PlayerCharacter __instance) => BoostTrace.Log(__instance, "booster stopped", $"dashHeld={P2Keys.Held(P2Keys.Action.Dash)}"); }

[HarmonyPatch(typeof(PlayerCharacter), nameof(PlayerCharacter.Event_OnStartUseBooster))]
internal static class BoostTraceStartUse { private static void Prefix(PlayerCharacter __instance) => BoostTrace.Log(__instance, "booster started", ""); }

[HarmonyPatch(typeof(PlayerCharacter), nameof(PlayerCharacter.OnSubHelper))]
internal static class BoostTraceSubHelper { private static void Prefix(PlayerCharacter __instance) => BoostTrace.Log(__instance, "helper item used", ""); }

[HarmonyPatch(typeof(DashAbility), nameof(DashAbility.DoAbility))]
internal static class BoostTraceDoDash
{
    private static void Prefix(DashAbility __instance)
    {
        var owner = __instance.GetComponent<PlayerCharacter>();
        BoostTrace.Log(owner, "dash ability used", $"data={(__instance.OwnData != null)}");
    }
}
