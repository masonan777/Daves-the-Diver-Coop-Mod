using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DaveTheDiverCoop;

// Camera spots (photo zones) and other InteractionGimmicks register themselves
// with "the player" (player 1) when he enters their trigger: OnPlayerEnter(true)
// takes InGameManager.playerCharacter and calls EnqueueInteractObject on it, so
// player 2 never gets them. Here the mod checks each gimmick's trigger area
// against player 2 and adds / removes it in player 2's interaction queue.
// Player 2's interact press runs as player 2's turn (P2Dispatch), so the
// gimmick's SuccessInteract -> OnSuccess -> PhotoZone.StartPhotoZoneMiniGame
// then starts from player 2.
// (Older version scanned colliders near player 2 and only found fish bodies:
// the gimmick component isn't on the collider's own object. It also swapped
// turns 4x a second, a suspect for "player 2's keys drove both divers".)
internal static class P2Interact
{
    private const float Reach = 1.8f; // only for gimmicks without a trigger collider
    private static readonly Dictionary<IntPtr, InteractionGimmick> _mine = new();
    private static readonly HashSet<string> _logged = new();
    private static InteractionGimmick[] _all = Array.Empty<InteractionGimmick>();
    private static float _next, _nextFind;
    private static int _logs;

    internal static bool Enabled = true;

    internal static void Tick()
    {
        var p2 = SecondDiver.Player2;
        if (!Enabled || p2 == null)
        {
            _mine.Clear();
            return;
        }
        PhotoP2.Tick();
        if (Time.time < _next)
            return;
        _next = Time.time + 0.25f;
        if (Time.time >= _nextFind)
        {
            _nextFind = Time.time + 3f;
            try { _all = Object.FindObjectsOfType<InteractionGimmick>(); }
            catch { _all = Array.Empty<InteractionGimmick>(); }
        }
        var p1 = SecondDiver.Player1;
        Vector2 at2 = p2.transform.position;
        var found = new HashSet<IntPtr>();
        foreach (var gimmick in _all)
        {
            try
            {
                if (gimmick == null || !gimmick.isActiveAndEnabled)
                    continue;
                if (!Inside(gimmick, at2))
                    continue;
                found.Add(gimmick.Pointer);
                if (_mine.ContainsKey(gimmick.Pointer))
                    continue;
                _mine[gimmick.Pointer] = gimmick;
                p2.EnqueueInteractObject(gimmick.Cast<IInteractionObject>());
                try { gimmick.SetActive(true); } catch { }
                if (_logged.Add(gimmick.name) && _logs++ < 40)
                    Plugin.Logger.LogInfo($"P2 interact: {gimmick.name} ({gimmick.GetIl2CppType().Name}) added for player 2");
            }
            catch (Exception exception)
            {
                if (_logs++ < 40)
                    Plugin.Logger.LogInfo($"P2 interact: {exception.Message}");
            }
        }
        var gone = new List<IntPtr>();
        foreach (var pair in _mine)
            if (!found.Contains(pair.Key))
                gone.Add(pair.Key);
        foreach (var key in gone)
        {
            var gimmick = _mine[key];
            _mine.Remove(key);
            try
            {
                if (gimmick == null)
                    continue;
                p2.DequeueInteractObject(gimmick.Cast<IInteractionObject>());
                // Hide the button icon unless player 1 is in there too.
                if (p1 == null || !Inside(gimmick, p1.transform.position))
                    gimmick.SetActive(false);
            }
            catch
            {
            }
        }
    }

    private static bool Inside(InteractionGimmick gimmick, Vector2 at)
    {
        var any = false;
        foreach (var collider in gimmick.GetComponentsInChildren<Collider2D>())
        {
            if (collider == null || !collider.isTrigger || !collider.enabled)
                continue;
            any = true;
            if (collider.OverlapPoint(at))
                return true;
        }
        var parent = gimmick.transform.parent;
        if (!any && parent != null)
            foreach (var collider in parent.GetComponents<Collider2D>())
            {
                if (collider == null || !collider.isTrigger || !collider.enabled)
                    continue;
                any = true;
                if (collider.OverlapPoint(at))
                    return true;
            }
        return !any && Vector2.Distance(gimmick.Center, at) < Reach;
    }
}

// A photo zone player 2 started: the mini-game reads "the" controller, so lend
// player 2's controller (or the keyboard pad) until it closes.
internal static class PhotoP2
{
    private static bool _on;
    private static float _since;

    internal static bool Open => _on;

    internal static void Started(PhotoZone zone)
    {
        if (_on)
            return;
        _on = true;
        _since = Time.realtimeSinceStartup;
        var why = $"player 2's photo zone {zone?.name}";
        Plugin.Logger.LogInfo($"Photo zone {zone?.name} started by player 2");
        if (P2Pad.Connected)
            P2Pad.Lend(true, why);
        else
            P2VirtualPad.Begin(why);
    }

    internal static void Tick()
    {
        if (!_on)
            return;
        var age = Time.realtimeSinceStartup - _since;
        var why = "";
        try
        {
            var zone = PhotoZone.Current;
            if (age > 1.5f && (zone == null || !zone.IsOpened))
                why = "photo zone closed";
        }
        catch (Exception exception)
        {
            why = exception.Message;
        }
        if (why == "" && age > 180f)
            why = "time limit";
        if (why == "")
            return;
        _on = false;
        Plugin.Logger.LogInfo($"Player 2's photo zone over ({why}) after {age:F1} s");
        P2Pad.Lend(false, why);
        P2VirtualPad.End(why);
    }
}

[HarmonyPatch(typeof(PhotoZone), nameof(PhotoZone.StartPhotoZoneMiniGame))]
internal static class PhotoZoneStartPatch
{
    private static void Postfix(PhotoZone __instance)
    {
        try
        {
            var p2 = SecondDiver.InPlayer2Turn;
            Plugin.Logger.LogInfo($"Photo zone {__instance?.name} starting (player 2's turn={p2})");
            if (p2)
                PhotoP2.Started(__instance);
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning($"Photo zone start: {exception.Message}");
        }
    }
}

// While player 2 is aiming, player 1's aim button is sometimes ignored (the
// game allows one aim at a time). If player 1 holds aim and isn't aiming a
// few frames later, start player 1's aim directly.
internal static class P1AimFix
{
    private static int _heldFrames;

    internal static void Tick()
    {
        var p1 = SecondDiver.Player1;
        var p2 = SecondDiver.Player2;
        if (p1 == null || p2 == null || !P2Dispatch.P1AimHeld())
        {
            _heldFrames = 0;
            return;
        }
        if (++_heldFrames != 4)
            return;
        try
        {
            if (p1.GetRangeActionState.ToString() == "Aim" || p1.IsActionLock)
                return;
            p1.OnStartRangeAttack();
            Plugin.Logger.LogInfo($"P1 aim: started directly (player 2 {p2.GetRangeActionState}); now {p1.GetRangeActionState}");
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning($"P1 aim start failed: {exception.Message}");
        }
    }
}
