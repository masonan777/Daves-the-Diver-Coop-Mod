using System;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DaveTheDiverCoop;

// Each gun muzzle (the Volley Jet has 3) is an "aim" the gun builds when it is
// equipped, and firing shoots one bullet per aim. Check player 2's gun has as
// many aims as its spec says (and as player 1's same gun); rebuild them if not.
internal static class GunAimFix
{
    private static IntPtr _checkedSpec;
    private static float _checkAt = -1f;

    internal static void Reset()
    {
        _checkedSpec = IntPtr.Zero;
        _checkAt = Time.realtimeSinceStartup + 1f;
    }

    private static string Describe(GunWeaponHandler gun)
    {
        var aim = gun?.aim;
        if (aim == null)
            return "no aim";
        var owner = aim.transform.IsChildOf(SecondDiver.Player2?.transform) ? "P2" :
            SecondDiver.Player1 != null && aim.transform.IsChildOf(SecondDiver.Player1.transform) ? "P1" : "elsewhere";
        return $"aims={aim.m_AimCount} list={aim.m_AimTransforms?.Count} under={owner} muzzles={gun.GunSpec?.MuzzleCount}";
    }

    // Rebuilding makes a fresh set of aim arrows; the old set would otherwise
    // stay on screen where it was last shown. Remove the ones not reused.
    private static void Rebuild(GunAim aim, GunSpecData spec)
    {
        var old = new System.Collections.Generic.List<GameObject>();
        void Collect(Il2CppSystem.Collections.Generic.List<Transform> list)
        {
            if (list == null)
                return;
            for (var i = 0; i < list.Count; i++)
                if (list[i] != null)
                    old.Add(list[i].gameObject);
        }
        Collect(aim.m_AimTransforms);
        Collect(aim.m_AimTrajectoryNodes);
        aim.Make(spec, spec.BulletPrefab);
        var keep = new System.Collections.Generic.HashSet<IntPtr>();
        void Keep(Il2CppSystem.Collections.Generic.List<Transform> list)
        {
            if (list == null)
                return;
            for (var i = 0; i < list.Count; i++)
                if (list[i] != null)
                    keep.Add(list[i].gameObject.Pointer);
        }
        Keep(aim.m_AimTransforms);
        Keep(aim.m_AimTrajectoryNodes);
        var removed = 0;
        foreach (var go in old)
        {
            if (go == null || keep.Contains(go.Pointer))
                continue;
            Object.Destroy(go);
            removed++;
        }
        Plugin.Logger.LogInfo($"Gun aims: removed {removed} leftover aim marker(s)");
    }

    // Player 2 was copied from player 1 including player 1's already-made
    // barrel arrows; player 2's gun then made its own. The copied ones are
    // not driven by anything and sit frozen pointing forward. Remove them.
    private static void RemoveCopiedArrows(GunAim aim)
    {
        if (aim == null)
            return;
        var keep = new System.Collections.Generic.HashSet<IntPtr>();
        var list = aim.m_AimTransforms;
        if (list != null)
            for (var i = 0; i < list.Count; i++)
                if (list[i] != null)
                    keep.Add(list[i].Pointer);
        var nodes = aim.m_AimTrajectoryNodes;
        if (nodes != null)
            for (var i = 0; i < nodes.Count; i++)
                if (nodes[i] != null)
                    keep.Add(nodes[i].Pointer);
        var root = aim.aimRoot != null ? aim.aimRoot : aim.transform;
        var removed = 0;
        for (var i = root.childCount - 1; i >= 0; i--)
        {
            var child = root.GetChild(i);
            if (child == null || !child.name.EndsWith("(Clone)") || keep.Contains(child.Pointer))
                continue;
            Object.Destroy(child.gameObject);
            removed++;
        }
        Plugin.Logger.LogInfo($"Gun aims: removed {removed} arrow(s) copied over from player 1");
    }

    internal static void Tick(PlayerCharacter p1, PlayerCharacter p2)
    {
        if (p1 == null || p2 == null)
            return;
        try
        {
            var gun2 = p2.GetComponent<InstanceItemInventory>()?.gunHandler;
            var spec = gun2?.GunSpec;
            if (spec == null)
                return;
            if (spec.Pointer == _checkedSpec)
                return;
            if (_checkAt > 0f && Time.realtimeSinceStartup < _checkAt)
                return;
            _checkedSpec = spec.Pointer;
            RemoveCopiedArrows(gun2.aim);
            var gun1 = p1.GetComponent<InstanceItemInventory>()?.gunHandler;
            var before = Describe(gun2);
            Plugin.Logger.LogInfo($"Gun aims: P2[{before}] P1[{Describe(gun1)}]");
            var aim = gun2.aim;
            var count = aim?.m_AimTransforms?.Count ?? 0;
            // The spread between the barrels comes from each aim's starting
            // angle. Player 2's list of starting angles is empty, so all three
            // bullets fly the same way and look like one. Copy player 1's.
            var mine = aim?.m_InitAimRotations;
            var theirs = gun1?.aim?.m_InitAimRotations;
            var p1Angles = "";
            if (theirs != null)
                for (var i = 0; i < theirs.Count; i++)
                    p1Angles += $"{theirs[i].eulerAngles.z:F0} ";
            Plugin.Logger.LogInfo($"Gun spread: P2 has {mine?.Count ?? -1} starting angles, P1 has {theirs?.Count ?? -1} [{p1Angles}] (P1 gun same={gun1?.GunSpec?.Pointer == spec.Pointer})");
            // Rebuilding the aims made duplicate arrows; the spread actually
            // comes from having an aim direction, so only report from here on.
            return;
#pragma warning disable CS0162
            if (aim != null && mine != null && mine.Count == 0 && theirs != null && theirs.Count > 0 && gun1.GunSpec?.Pointer == spec.Pointer)
            {
                for (var i = 0; i < theirs.Count; i++)
                    mine.Add(theirs[i]);
                Plugin.Logger.LogInfo($"Gun spread copied to player 2 ({mine.Count} angles)");
            }
            else if (aim != null && mine != null && mine.Count == 0 && count > 1)
            {
                SecondDiver.BeginPlayer2Turn(p2);
                try
                {
                    Rebuild(aim, spec);
                }
                finally
                {
                    SecondDiver.EndPlayer2Turn(p2);
                }
                Plugin.Logger.LogInfo($"Gun aims rebuilt for player 2 (no spread to copy): now {Describe(gun2)}, starting angles {aim.m_InitAimRotations?.Count}");
                return;
            }
            if (aim != null && spec.MuzzleCount > 1 && count < spec.MuzzleCount)
            {
                SecondDiver.BeginPlayer2Turn(p2);
                try
                {
                    Rebuild(aim, spec);
                }
                finally
                {
                    SecondDiver.EndPlayer2Turn(p2);
                }
                Plugin.Logger.LogInfo($"Gun aims rebuilt for player 2: now {Describe(gun2)}");
            }
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning($"Gun aim check failed: {exception.Message}");
            _checkedSpec = IntPtr.Zero;
            _checkAt = Time.realtimeSinceStartup + 5f;
        }
    }
}
