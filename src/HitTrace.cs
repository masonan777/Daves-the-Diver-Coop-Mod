using HarmonyLib;
using UnityEngine;

namespace DaveTheDiverCoop;

// Diagnostics for "the game froze when player 2 got hit": log entering and
// leaving each step of player 2's damage handling. The log is written
// immediately, so after a freeze the last "enter" without its "leave" shows
// where it got stuck.
internal static class HitTrace
{
    private static int _lines;

    internal static bool IsP2Breath(PlayerBreathHandler h) => h != null;

    internal static string Who(PlayerBreathHandler h) =>
        h != null && SecondDiver.Player2 != null && h.Pointer == SecondDiver.Player2.BreathHandler?.Pointer ? "P2" : "P1";

    internal static bool IsP2Panel(PlayerStateInfoPanelUI ui) => ui != null;

    internal static float WindowUntil;

    internal static void Gated(string text)
    {
        if (Time.realtimeSinceStartup < WindowUntil)
            Log(text);
    }

    internal static void Log(string text)
    {
        if (_lines++ < 400)
            Plugin.Logger.LogInfo($"HitTrace f{Time.frameCount} {text}");
    }
}

[HarmonyPatch(typeof(PlayerCharacter), nameof(PlayerCharacter.OnTakeDamage))]
internal static class HitTraceTakeDamage
{
    private static void Prefix(PlayerCharacter __instance, ref bool __state)
    {
        __state = __instance != null;
        if (__state) { HitTrace.WindowUntil = Time.realtimeSinceStartup + 3f; HitTrace.Log("enter OnTakeDamage"); }
    }
    private static void Postfix(bool __state) { if (__state) HitTrace.Log("leave OnTakeDamage"); }
}

[HarmonyPatch(typeof(PlayerBreathHandler), nameof(PlayerBreathHandler.SetHitHP))]
internal static class HitTraceSetHitHp
{
    private static void Prefix(PlayerBreathHandler __instance, float __0, ref bool __state)
    {
        __state = HitTrace.IsP2Breath(__instance);
        if (__state) HitTrace.WindowUntil = Time.realtimeSinceStartup + 3f;
        if (__state) HitTrace.Log($"enter SetHitHP({__0:F1}) hp={__instance.HP:F1}");
    }
    private static void Postfix(PlayerBreathHandler __instance, bool __state) { if (__state) HitTrace.Log($"leave SetHitHP hp={__instance.HP:F1}"); }
}

[HarmonyPatch(typeof(PlayerBreathHandler), nameof(PlayerBreathHandler.SetHP))]
internal static class HitTraceSetHp
{
    private static void Prefix(PlayerBreathHandler __instance, float __0, ref bool __state)
    {
        __state = HitTrace.IsP2Breath(__instance);
        if (__state && __0 < __instance.HP - 0.5f) HitTrace.Gated($"enter SetHP({__0:F1})"); else __state = false;
    }
    private static void Postfix(bool __state) { if (__state) HitTrace.Gated("leave SetHP"); }
}

[HarmonyPatch(typeof(PlayerBreathHandler), nameof(PlayerBreathHandler.SetHitState))]
internal static class HitTraceSetHitState
{
    private static void Prefix(PlayerBreathHandler __instance, ref bool __state)
    {
        __state = HitTrace.IsP2Breath(__instance);
        if (__state) HitTrace.Log("enter SetHitState");
    }
    private static void Postfix(bool __state) { if (__state) HitTrace.Log("leave SetHitState"); }
}

[HarmonyPatch(typeof(PlayerBreathHandler), nameof(PlayerBreathHandler.OnHitState))]
internal static class HitTraceOnHitState
{
    private static void Prefix(PlayerBreathHandler __instance, ref bool __state)
    {
        __state = HitTrace.IsP2Breath(__instance);
        if (__state) HitTrace.Log("enter OnHitState");
    }
    private static void Postfix(bool __state) { if (__state) HitTrace.Log("leave OnHitState"); }
}

[HarmonyPatch(typeof(PlayerStateInfoPanelUI), nameof(PlayerStateInfoPanelUI.SetOxygenInfo))]
internal static class HitTracePanelSetOxygen
{
    private static void Prefix(PlayerStateInfoPanelUI __instance, ref bool __state)
    {
        __state = HitTrace.IsP2Panel(__instance);
        if (__state) HitTrace.Gated("enter panel SetOxygenInfo");
    }
    private static void Postfix(bool __state) { if (__state) HitTrace.Gated("leave panel SetOxygenInfo"); }
}

[HarmonyPatch(typeof(PlayerStateInfoPanelUI), nameof(PlayerStateInfoPanelUI.PushBlinkState))]
internal static class HitTracePanelPush
{
    private static void Prefix(PlayerStateInfoPanelUI __instance, ref bool __state)
    {
        __state = HitTrace.IsP2Panel(__instance);
        if (__state) HitTrace.Log("enter panel PushBlinkState");
    }
    private static void Postfix(bool __state) { if (__state) HitTrace.Log("leave panel PushBlinkState"); }
}

[HarmonyPatch(typeof(PlayerStateInfoPanelUI), nameof(PlayerStateInfoPanelUI.ShowHighlightEffect))]
internal static class HitTracePanelHighlight
{
    private static void Prefix(PlayerStateInfoPanelUI __instance, ref bool __state)
    {
        __state = HitTrace.IsP2Panel(__instance);
        if (__state) HitTrace.Log("enter panel ShowHighlightEffect");
    }
    private static void Postfix(bool __state) { if (__state) HitTrace.Log("leave panel ShowHighlightEffect"); }
}

[HarmonyPatch(typeof(PlayerStateInfoPanelUI), nameof(PlayerStateInfoPanelUI.ShowCriticalMark))]
internal static class HitTracePanelCritical
{
    private static void Prefix(PlayerStateInfoPanelUI __instance, ref bool __state)
    {
        __state = HitTrace.IsP2Panel(__instance);
        if (__state) HitTrace.Log("enter panel ShowCriticalMark");
    }
    private static void Postfix(bool __state) { if (__state) HitTrace.Log("leave panel ShowCriticalMark"); }
}
