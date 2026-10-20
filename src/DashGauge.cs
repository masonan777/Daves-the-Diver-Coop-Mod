using HarmonyLib;

namespace DaveTheDiverCoop;

// The short-dash gauge updates always go to player 1's oxygen panel. When the
// update is about player 2's dash (player 2's turn, or only player 2's dash is
// cooling down), show it on player 2's panel instead and leave player 1's alone.
internal static class DashGauge
{
    private static int _logs;

    internal static PlayerStateInfoPanelUI Player2Panel()
    {
        var panel = P2Oxygen.Panel;
        return panel != null ? panel.GetComponent<PlayerStateInfoPanelUI>() : null;
    }

    internal static bool ForPlayer2(PlayerStateInfoPanelUI ui, out PlayerStateInfoPanelUI target, out bool byTurn)
    {
        target = null;
        byTurn = false;
        var p1 = SecondDiver.Player1;
        var p2 = SecondDiver.Player2;
        if (ui == null || p1 == null || p2 == null)
            return false;
        target = Player2Panel();
        if (target == null || ui.Pointer == target.Pointer)
            return false;
        if (SecondDiver.InPlayer2Turn)
        {
            byTurn = true;
            return true;
        }
        var p1Cooling = p1.GetComponent<ShortDashAbility>()?.dashCooldown?.IsActive == true;
        var p2Cooling = p2.GetComponent<ShortDashAbility>()?.dashCooldown?.IsActive == true;
        return p2Cooling && !p1Cooling;
    }

    internal static void Note(string what)
    {
        if (_logs++ < 6)
            Plugin.Logger.LogInfo($"Short-dash gauge: {what} sent to player 2's panel");
    }
}

[HarmonyPatch(typeof(PlayerStateInfoPanelUI), nameof(PlayerStateInfoPanelUI.FillShortDashSlider))]
internal static class DashGaugeFill
{
    private static bool Prefix(PlayerStateInfoPanelUI __instance, float __0, float __1)
    {
        if (!DashGauge.ForPlayer2(__instance, out var target, out _))
            return true;
        DashGauge.Note($"fill({__0:F2}, {__1:F2})");
        target.FillShortDashSlider(__0, __1);
        return false;
    }
}

[HarmonyPatch(typeof(PlayerStateInfoPanelUI), nameof(PlayerStateInfoPanelUI.SetShortDashSliderValue))]
internal static class DashGaugeSet
{
    private static bool Prefix(PlayerStateInfoPanelUI __instance, float __0)
    {
        if (!DashGauge.ForPlayer2(__instance, out var target, out var byTurn))
            return true;
        // Outside player 2's turn a "full" value is player 1's own (ready) gauge.
        if (!byTurn && __0 >= 0.999f)
            return true;
        DashGauge.Note($"set({__0:F2})");
        target.SetShortDashSliderValue(__0);
        return false;
    }
}
