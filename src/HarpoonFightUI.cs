using HarmonyLib;
using UnityEngine;

namespace DaveTheDiverCoop;

// Reeling in a strong fish shows a mash / stick-shake gauge that follows "the
// player" (player 1). Remember which diver is fighting a fish, and while it's
// player 2, place the gauge over player 2.
internal static class HarpoonFight
{
    private static PlayerCharacter _fighter;

    internal static bool Player2Fighting
    {
        get
        {
            var p2 = SecondDiver.Player2;
            return p2 != null && _fighter != null && _fighter.Pointer == p2.Pointer;
        }
    }

    internal static void Started(PlayerCharacter who)
    {
        _fighter = who;
        Plugin.Logger.LogInfo($"Harpoon fight started by {(SecondDiver.IsPlayer2(who) ? "player 2" : "player 1")}");
    }

    internal static void Ended(PlayerCharacter who)
    {
        if (_fighter != null && who != null && _fighter.Pointer == who.Pointer)
            _fighter = null;
    }
}

[HarmonyPatch(typeof(PlayerCharacter), nameof(PlayerCharacter.Event_OnFightnHarpoonProjectile))]
internal static class HarpoonFightStart
{
    private static void Postfix(PlayerCharacter __instance) => HarpoonFight.Started(__instance);
}

[HarmonyPatch(typeof(PlayerCharacter), nameof(PlayerCharacter.OnEndFightHarpoonProjectile))]
internal static class HarpoonFightEnd
{
    private static void Postfix(PlayerCharacter __instance) => HarpoonFight.Ended(__instance);
}

[HarmonyPatch(typeof(StickShakeFightUI), nameof(StickShakeFightUI.LateUpdate))]
internal static class HarpoonFightGaugeOverP2
{
    private static void Prefix(ref bool __state)
    {
        __state = HarpoonFight.Player2Fighting && !SecondDiver.InPlayer2Turn;
        if (__state)
            SecondDiver.BeginPlayer2Turn(SecondDiver.Player2);
    }

    private static void Postfix(bool __state)
    {
        if (__state)
            SecondDiver.EndPlayer2Turn(SecondDiver.Player2);
    }
}
