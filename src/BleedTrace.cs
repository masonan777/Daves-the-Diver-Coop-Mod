using System.Text;
using HarmonyLib;
using UnityEngine;

namespace DaveTheDiverCoop;

// Diagnostics for "player 2 flashes / bleeds when player 1 was attacked".
// (Class names avoid "Trace": Plugin skips those unless EnableTraceHooks is on.)
// From the Cpp2IL dump: the flash is PlayerCharacter.BlinkPlayer; the blood +
// flash of a bite is PlayerCharacter.OnBiteHit, called on "the player" by
// Biteable.HitBiteDamage and the creature animation event
// AniClipEvents.DoDamageEvent. Each flash is logged with what caused it (when
// known) and the creatures near each diver. No hook on OnTakeDamage itself:
// damage-handling hooks have crashed the game before.
internal static class BleedWatch
{
    internal static string Source;
    private static int _lines;

    internal static void Log(string text)
    {
        CoopTest.Record(text);
        if (_lines++ < 80)
            Plugin.Logger.LogInfo($"BleedWatch f{Time.frameCount} {text}");
    }

    internal static string Who(PlayerCharacter p)
    {
        if (p == null)
            return "?";
        var p2 = SecondDiver.Player2;
        return p2 != null && p.Pointer == p2.Pointer ? "P2" : "P1";
    }

    // Big creatures within 6 m of each diver.
    internal static string Nearby()
    {
        var text = new StringBuilder();
        try
        {
            var p1 = SecondDiver.Player1;
            var p2 = SecondDiver.Player2;
            foreach (var fish in Object.FindObjectsOfType<DR.AI.FishAISystem>())
            {
                if (fish == null || !fish.gameObject.activeInHierarchy)
                    continue;
                var name = fish.name;
                if (!(name.Contains("Shark") || name.Contains("Saw") || name.Contains("Eel") || name.Contains("Mako")
                      || name.Contains("Marlin") || name.Contains("Squid") || name.Contains("Barracuda")))
                    continue;
                var at = fish.transform.position;
                var d1 = p1 != null ? Vector2.Distance(at, p1.transform.position) : 999f;
                var d2 = p2 != null ? Vector2.Distance(at, p2.transform.position) : 999f;
                if (d1 > 6f && d2 > 6f)
                    continue;
                text.Append($" {name} (P1 {d1:F1} m, P2 {d2:F1} m, holds P2={P2Grab.HoldsBy(fish)})");
            }
        }
        catch
        {
        }
        return text.Length == 0 ? " none" : text.ToString();
    }
}

[HarmonyPatch(typeof(PlayerCharacter), nameof(PlayerCharacter.BlinkPlayer))]
internal static class BleedWatchBlink
{
    private static void Prefix(PlayerCharacter __instance)
    {
        var p1 = SecondDiver.Player1;
        BleedWatch.Log($"flash on {BleedWatch.Who(__instance)} from {BleedWatch.Source ?? "a hit"} " +
                       $"(p2Turn={SecondDiver.InPlayer2Turn}, P2 held by mod={P2Grab.Held}, P1 in grab={p1 != null && p1.IsBattleQTEState}); " +
                       $"creatures near:{BleedWatch.Nearby()}");
    }
}

[HarmonyPatch(typeof(PlayerCharacter), nameof(PlayerCharacter.OnBiteHit))]
internal static class BleedWatchBiteHit
{
    private static void Prefix(PlayerCharacter __instance) =>
        BleedWatch.Log($"bite blood on {BleedWatch.Who(__instance)} from {BleedWatch.Source ?? "unknown"}");
}

[HarmonyPatch(typeof(DR.AI.Biteable), nameof(DR.AI.Biteable.HitBiteDamage))]
internal static class BleedWatchBiteDamage
{
    private static void Prefix(DR.AI.Biteable __instance) =>
        BleedWatch.Source = $"Biteable.HitBiteDamage of {NoSharkGrab.FishName(__instance)} (marked on P2={BiteRedirect.Targets(__instance)}, holds P2={P2Grab.HoldsBy(__instance)})";

    private static void Postfix() => BleedWatch.Source = null;
}

[HarmonyPatch(typeof(AniClipEvents.DoDamageEvent), nameof(AniClipEvents.DoDamageEvent.Fire))]
internal static class BleedWatchDamageEvent
{
    private static void Prefix(Animator __0) =>
        BleedWatch.Source = $"DoDamageEvent of {NoSharkGrab.FishName(__0)} (holds P2={P2Grab.HoldsBy(__0)})";

    private static void Postfix() => BleedWatch.Source = null;
}
