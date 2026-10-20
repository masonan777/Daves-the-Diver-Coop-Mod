using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace DaveTheDiverCoop;

// Player 1 on a controller: with player 2 present the game's stick-aim
// callbacks stop before setting the aim direction (the same thing that broke
// mouse aiming). Set it from player 1's controller sticks directly.
internal static class PadAimFix
{
    internal static Vector2 Stick(bool right)
    {
        var best = Vector2.zero;
        try
        {
            var pads = Gamepad.all;
            for (var i = 0; i < pads.Count; i++)
            {
                var pad = pads[i];
                if (pad == null || !pad.enabled)
                    continue; // player 2's controller is switched off for the game
                var value = (right ? pad.rightStick : pad.leftStick).ReadValue();
                if (value.sqrMagnitude > best.sqrMagnitude)
                    best = value;
            }
        }
        catch
        {
        }
        return best.sqrMagnitude < 0.04f ? Vector2.zero : best;
    }

    // Every frame while player 1 aims: point the aim with the left stick (the
    // game's own controller layout), since its left-stick callback no longer
    // gets through with player 2 present.
    internal static void Apply(PlayerCharacter p)
    {
        if (!ForPlayerOne(p))
            return;
        try
        {
            var state = p.GetRangeActionState.ToString();
            if (state != "Aim" && state != "Fire")
                return;
            var value = Stick(false);
            if (value != Vector2.zero)
                p.aimInput = value.normalized;
        }
        catch
        {
        }
    }

    internal static bool ForPlayerOne(PlayerCharacter p) => p != null && SecondDiver.Player2 != null && !SecondDiver.IsPlayer2(p);
}

[HarmonyPatch(typeof(PlayerCharacter), nameof(PlayerCharacter.OnAimAxis))]
internal static class PadAimLeft
{
    private static void Postfix(PlayerCharacter __instance)
    {
        if (!PadAimFix.ForPlayerOne(__instance))
            return;
        var value = PadAimFix.Stick(false);
        if (value != Vector2.zero)
            __instance.aimInput = value;
    }
}

[HarmonyPatch(typeof(PlayerCharacter), nameof(PlayerCharacter.OnAimAxisRight))]
internal static class PadAimRight
{
    private static void Postfix(PlayerCharacter __instance)
    {
        if (!PadAimFix.ForPlayerOne(__instance))
            return;
        var value = PadAimFix.Stick(true);
        if (value != Vector2.zero)
            __instance.aimInputRight = value;
    }
}
