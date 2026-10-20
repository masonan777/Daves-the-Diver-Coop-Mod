using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace DaveTheDiverCoop;

// Diagnostics: which diver runs each step of mouse aiming, and what it returns.
// Rate-limited per step and per diver.
internal static class AimTrace
{
    private static readonly Dictionary<string, float> _next = new();
    private static readonly Dictionary<string, int> _counts = new();

    internal static void Log(PlayerCharacter character, string step, string detail)
    {
        if (UnityEngine.InputSystem.Mouse.current?.rightButton.isPressed != true)
            return;
        var who = SecondDiver.IsPlayer2(character) ? "P2" : "P1";
        var key = who + step;
        _counts[key] = _counts.TryGetValue(key, out var count) ? count + 1 : 1;
        if (_next.TryGetValue(key, out var next) && Time.realtimeSinceStartup < next)
            return;
        _next[key] = Time.realtimeSinceStartup + 1f;
        Plugin.Logger.LogInfo($"AimTrace {who} {step} (x{_counts[key]}): {detail} aim={character.aimInput}");
    }
}

[HarmonyPatch(typeof(PlayerCharacter), nameof(PlayerCharacter.OnAimAxisMouse))]
internal static class TraceAimAxisMousePatch
{
    private static float _nextDetail;

    private static string DescribeScheme(PlayerCharacter character)
    {
        try
        {
            var scheme = character.inputAsset?.currentScheme;
            return scheme == null ? "?" : scheme.HasValue ? scheme.Value.ToString() : "none";
        }
        catch (System.Exception exception)
        {
            return "read failed: " + exception.Message;
        }
    }

    private static void Postfix(PlayerCharacter __instance)
    {
        AimTrace.Log(__instance, "OnAimAxisMouse", "");
        if (SecondDiver.IsPlayer2(__instance) || SecondDiver.Player2 == null)
            return;
        // Workaround: with player 2 present, the game's own mouse-aim callback
        // stops before computing a direction, so compute it the same way.
        var mouse = UnityEngine.InputSystem.Mouse.current;
        if (mouse == null)
            return;
        try
        {
            var direction = __instance.GetDirectionPlayerToMouse(mouse.position.ReadValue());
            if (direction.sqrMagnitude > 0.0001f)
                __instance.aimInput = direction;
            if (Time.realtimeSinceStartup >= _nextDetail && mouse.rightButton.isPressed)
            {
                _nextDetail = Time.realtimeSinceStartup + 2f;
                Plugin.Logger.LogInfo(
                    $"Mouse-aim workaround P1: direction={direction} keyboardMouse={__instance.IsKeyboardMouse()} " +
                    $"scheme={DescribeScheme(__instance)} recentDevice={__instance.inputAsset?.recentDevice?.name} " +
                    $"sameAssetAsP2={__instance.inputAsset?.Pointer == SecondDiver.Player2?.inputAsset?.Pointer}");
            }
        }
        catch (System.Exception exception)
        {
            Plugin.Logger.LogWarning($"Mouse-aim workaround failed: {exception.Message}");
        }
    }
}

[HarmonyPatch(typeof(PlayerCharacter), nameof(PlayerCharacter.LookAimAxis))]
internal static class TraceLookAimAxisPatch
{
    private static void Postfix(PlayerCharacter __instance, bool __result) =>
        AimTrace.Log(__instance, "LookAimAxis", $"result={__result}");
}

[HarmonyPatch(typeof(PlayerCharacter), nameof(PlayerCharacter.RangeAimAxis))]
internal static class TraceRangeAimAxisPatch
{
    private static void Postfix(PlayerCharacter __instance, bool __result) =>
        AimTrace.Log(__instance, "RangeAimAxis", $"result={__result}");
}

[HarmonyPatch(typeof(PlayerCharacter), nameof(PlayerCharacter.GetDirectionPlayerToMouse))]
internal static class TraceDirectionToMousePatch
{
    private static void Postfix(PlayerCharacter __instance, Vector2 __result, Vector2 __0) =>
        AimTrace.Log(__instance, "GetDirectionPlayerToMouse", $"in={__0} out={__result}");
}

[HarmonyPatch(typeof(PlayerCharacter), nameof(PlayerCharacter.IsKeyboardMouse))]
internal static class TraceIsKeyboardMousePatch
{
    private static void Postfix(PlayerCharacter __instance, bool __result) =>
        AimTrace.Log(__instance, "IsKeyboardMouse", $"result={__result}");
}
