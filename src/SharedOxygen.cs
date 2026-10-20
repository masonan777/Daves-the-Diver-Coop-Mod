using HarmonyLib;
using UnityEngine;

namespace DaveTheDiverCoop;

// Oxygen and health are one bar in this game. Player 2 shares player 1's tank:
// player 2 does not breathe on its own, and anything that changes player 2's
// oxygen (damage, refills) is applied to player 1's real breath handler, so the
// HUD, hit effects and running out of air all work the normal way.
internal static class SharedOxygen
{
    // Off by default now: each diver has their own tank (see P2Oxygen).
    internal static bool Shared => P2Oxygen.SharedTank;

    internal static bool IsPlayer2Handler(PlayerBreathHandler handler) =>
        Shared && handler != null && SecondDiver.Player2 != null &&
        handler.Pointer == SecondDiver.Player2.BreathHandler?.Pointer;

    internal static PlayerBreathHandler PlayerOneHandler => SecondDiver.Player1?.BreathHandler;

    // Keep player 2's number equal to the shared tank so any change it makes is
    // a change to the shared value.
    internal static void Mirror()
    {
        if (!Shared)
            return;
        var p2 = SecondDiver.Player2?.BreathHandler;
        var p1 = PlayerOneHandler;
        if (p1 == null || p2 == null)
            return;
        try
        {
            if (!Mathf.Approximately(p2.HP, p1.HP))
                p2.HP = p1.HP;
        }
        catch
        {
        }
    }

    private static int _forwardLogs;

    internal static void LogForward(string what, float value)
    {
        if (_forwardLogs++ < 10)
            Plugin.Logger.LogInfo($"Shared oxygen: player 2 {what} {value:F1} -> applied to the shared tank");
    }
}









// Taking a hit (e.g. grabbing a sea urchin) runs the "hit" flash on the breath
// handler. Player 2's copy has no HUD to flash and throws, which aborted the
// pickup halfway. Run it on player 1's handler instead.



