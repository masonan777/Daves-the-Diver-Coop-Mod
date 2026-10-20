using System;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace DaveTheDiverCoop;

// Grab / struggle sequences (shark drag, eel grab, dodge and mash prompts)
// only know about player 1. While player 2 is in the dive:
//  - creatures on [Coop] GrabBlockList never grab anyone (they broke even on
//    player 1, e.g. the sawtooth shark froze the divers and lost the HUD);
//  - any other creature's grab works normally on player 1, but is skipped
//    (the bite still lands) when it would grab player 2.
internal static class NoSharkGrab
{
    private static ConfigEntry<bool> _enabled;
    // Test bot: run once with the rules switched off (must then FAIL).
    internal static bool TestOff;
    private static ConfigEntry<string> _blockList;
    private static int _logs;

    internal static void Bind(ConfigFile config)
    {
        _enabled = config.Bind("Coop", "DisableSharkGrabs", true,
            "While player 2 is in the dive: skip grabs aimed at player 2, and all grabs by creatures on GrabBlockList.");
        _blockList = config.Bind("Coop", "GrabBlockList", "Saw",
            "Comma-separated parts of creature names whose grabs are skipped for both players (e.g. Saw, Eel).");
    }

    internal static bool Blocked(string fish)
    {
        if (string.IsNullOrEmpty(fish) || _blockList == null)
            return false;
        foreach (var part in _blockList.Value.Split(','))
        {
            var p = part.Trim();
            if (p.Length > 0 && fish.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        return false;
    }

    // fish: the creature's name (may be null); onPlayer2: the grab is aimed at player 2.
    internal static bool Skip(string what, string fish, bool onPlayer2, Component source = null)
    {
        if (_enabled == null || !_enabled.Value || SecondDiver.Player2 == null || TestOff)
        {
            CoopTest.Record($"grab step '{what}' by {fish ?? "?"}: not checked (mod's grab rules off)");
            return false;
        }
        var blocked = Blocked(fish);
        // A creature already holding player 2 (mod grab) must not grab player 1
        // as well (tiger shark had both divers in its mouth).
        var busy = !onPlayer2 && !blocked && source != null && P2Grab.HoldsBy(source);
        blocked |= busy;
        CoopTest.Record($"grab step '{what}' by {fish ?? "?"}: {(blocked || onPlayer2 ? "skipped" : "allowed")} (aimed at {(onPlayer2 ? "P2" : "P1")}{(busy ? ", already holding P2" : blocked ? ", block list" : "")})");
        if (!blocked && !onPlayer2)
        {
            if (_logs++ < 40)
                Plugin.Logger.LogInfo($"Grab allowed on player 1: {what} by {fish ?? "?"}");
            return false;
        }
        if (onPlayer2 && !blocked)
            P2Grab.TryBegin(source, what);
        if (_logs++ < 40)
            Plugin.Logger.LogInfo($"Shark grab skipped: {what} by {fish ?? "?"} ({(busy ? "already holding player 2" : blocked ? "on block list" : "aimed at player 2")})");
        // The bite already locked the diver for the grab that now won't come:
        // unlock them once the bite has played out.
        _unlockChecks = new[] { Time.time + 0.4f, Time.time + 1.2f, Time.time + 2.5f };
        return true;
    }

    private static float[] _unlockChecks;

    internal static void Tick()
    {
        if (_unlockChecks == null)
            return;
        var due = false;
        for (var i = 0; i < _unlockChecks.Length; i++)
            if (_unlockChecks[i] > 0f && Time.time >= _unlockChecks[i])
            {
                _unlockChecks[i] = -1f;
                due = true;
            }
        if (!due)
            return;
        var last = Array.TrueForAll(_unlockChecks, t => t < 0f);
        if (last)
        {
            _unlockChecks = null;
            // The skipped grab may also have hidden the HUD (sawtooth).
            CoopBehaviour.ResetHud();
        }
        foreach (var (diver, label) in new[] { (SecondDiver.Player1, "player 1"), (SecondDiver.Player2, "player 2") })
        {
            // Player 2 held by the mod's grab is meant to be locked.
            if (P2Grab.Held && diver == SecondDiver.Player2)
                continue;
            try
            {
                if (diver != null && (diver.IsActionLock || diver.IsMoveLocked))
                {
                    if (diver.IsActionLock)
                        diver.SetActionLock(false);
                    if (diver.IsMoveLocked)
                        diver.UnlockMove();
                    Plugin.Logger.LogInfo($"Skipped grab: {label} was left locked, unlocked");
                }
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning($"Skipped grab unlock {label}: {exception.Message}");
            }
        }
    }

    internal static string FishName(Component c)
    {
        if (c == null)
            return null;
        try
        {
            var fish = c.GetComponentInParent<DR.AI.FishAISystem>();
            return fish != null ? fish.name : c.transform.root.name;
        }
        catch
        {
            return c.name;
        }
    }

    // Is this creature nearer player 2 than player 1 (i.e. attacking player 2)?
    internal static bool CloserToPlayer2(Component c)
    {
        var p1 = SecondDiver.Player1;
        var p2 = SecondDiver.Player2;
        if (c == null || p1 == null || p2 == null)
            return false;
        var at = c.transform.position;
        return Vector2.Distance(at, p2.transform.position) < Vector2.Distance(at, p1.transform.position);
    }

    // A grab is several steps (pull in, then struggle prompt): decide once per
    // fish and keep that answer for the rest of its grab.
    private static readonly System.Collections.Generic.Dictionary<IntPtr, (bool onP2, float until)> _decided = new();

    internal static Component Owner(SkillRunner runner)
    {
        try { return runner?.skillManager?.GetOwner; }
        catch { return null; }
    }

    // Skill runners belong to a fish through their skill manager.
    internal static (string name, bool onP2) RunnerFish(SkillRunner runner, bool start)
    {
        try
        {
            var owner = runner?.skillManager?.GetOwner;
            if (owner == null)
                return (null, false);
            if (!start && _decided.TryGetValue(owner.Pointer, out var known) && known.until >= Time.time)
                return (FishName(owner), known.onP2);
            var onP2 = CloserToPlayer2(owner);
            _decided[owner.Pointer] = (onP2, Time.time + 8f);
            return (FishName(owner), onP2);
        }
        catch
        {
            return (null, false);
        }
    }
}

[HarmonyPatch(typeof(QTEReadyToEnterSkillRunner), nameof(QTEReadyToEnterSkillRunner.Play))]
internal static class NoGrabPull
{
    private static bool Prefix(QTEReadyToEnterSkillRunner __instance)
    {
        var (fish, onP2) = NoSharkGrab.RunnerFish(__instance, true);
        return !NoSharkGrab.Skip("pull into mouth", fish, onP2, NoSharkGrab.Owner(__instance));
    }
}

[HarmonyPatch(typeof(OpenQTEUISkillRunner), nameof(OpenQTEUISkillRunner.Play))]
internal static class NoGrabPrompt
{
    private static bool Prefix(OpenQTEUISkillRunner __instance)
    {
        var (fish, onP2) = NoSharkGrab.RunnerFish(__instance, false);
        return !NoSharkGrab.Skip("struggle prompt", fish, onP2, NoSharkGrab.Owner(__instance));
    }
}
