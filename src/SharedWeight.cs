using System;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace DaveTheDiverCoop;

// The dive's cargo (and its weight limit) is shared by both divers. Player 2's
// oxygen panel carries a copy of the weight display; show the same live value
// there by repeating every update player 1's display gets.
internal static class SharedWeight
{
    private static LootsInfoPanel _copy;
    private static bool _haveLast;
    private static float _a, _b, _c;
    private static int _logs;

    internal static void Attach(GameObject p2Panel)
    {
        _copy = null;
        if (p2Panel == null)
            return;
        var found = p2Panel.GetComponentsInChildren(Il2CppType.Of<LootsInfoPanel>(), true);
        _copy = found.Length > 0 ? found[0]?.TryCast<LootsInfoPanel>() : null;
        Plugin.Logger.LogInfo($"Shared weight: player 2 weight display {(_copy != null ? "found" : "not found")}");
        if (_copy != null && _haveLast)
            Mirror(_a, _b, _c);
    }

    internal static void Detach() => _copy = null;

    internal static bool IsCopy(LootsInfoPanel panel) => panel != null && _copy != null && panel.Pointer == _copy.Pointer;

    internal static void Remember(float a, float b, float c)
    {
        _a = a;
        _b = b;
        _c = c;
        _haveLast = true;
    }

    internal static void Mirror(float a, float b, float c)
    {
        if (_copy == null)
            return;
        try
        {
            _copy.UpdateWeight(a, b, c);
            if (_logs++ < 5)
                Plugin.Logger.LogInfo($"Shared weight: player 2 display updated ({a:F1}, {b:F1}, {c:F1})");
        }
        catch (Exception exception)
        {
            if (_logs++ < 5)
                Plugin.Logger.LogWarning($"Shared weight update failed: {exception.Message}");
        }
    }

    internal static void Run(Action<LootsInfoPanel> action)
    {
        if (_copy == null)
            return;
        try
        {
            action(_copy);
        }
        catch
        {
        }
    }
}

[HarmonyPatch(typeof(LootsInfoPanel), nameof(LootsInfoPanel.UpdateWeight))]
internal static class SharedWeightUpdate
{
    private static void Postfix(LootsInfoPanel __instance, float __0, float __1, float __2)
    {
        if (SharedWeight.IsCopy(__instance))
            return;
        SharedWeight.Remember(__0, __1, __2);
        SharedWeight.Mirror(__0, __1, __2);
    }
}

[HarmonyPatch(typeof(LootsInfoPanel), nameof(LootsInfoPanel.ResetWeight))]
internal static class SharedWeightReset
{
    private static void Postfix(LootsInfoPanel __instance)
    {
        if (!SharedWeight.IsCopy(__instance))
            SharedWeight.Run(copy => copy.ResetWeight());
    }
}

[HarmonyPatch(typeof(LootsInfoPanel), nameof(LootsInfoPanel.BlinkOverweight))]
internal static class SharedWeightBlink
{
    private static void Postfix(LootsInfoPanel __instance)
    {
        if (!SharedWeight.IsCopy(__instance))
            SharedWeight.Run(copy => copy.BlinkOverweight());
    }
}
