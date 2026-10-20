using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace DaveTheDiverCoop;

// When Dave finishes any action (a knife swing, a pickup) the game resets his
// harpoon. On player 2's copied harpoon bolt that reset crashes, the crash
// aborts the "action finished" step, and player 2 stays stuck "attacking"
// forever. Catch the crash so the rest of the step completes, and log which
// part of player 2's bolt is missing so it can be fixed properly.
internal static class HarpoonResetGuard
{
    private static int _caught;
    private static bool _compared;

    private static void Prefix() => HitTrace.Gated("enter harpoon reset");

    private static Exception Finalizer(HarpoonProjectile __instance, Exception __exception)
    {
        HitTrace.Gated("leave harpoon reset");
        if (__exception == null)
            return null;
        if (_caught++ < 5)
            Plugin.Logger.LogWarning($"Harpoon reset crashed on {__instance?.name}; skipped the rest so the action can finish ({__exception.GetType().Name})");
        if (!_compared)
        {
            _compared = true;
            CompareWithPlayerOne(__instance);
        }
        return null;
    }

    private static void CompareWithPlayerOne(HarpoonProjectile broken)
    {
        try
        {
            var p1 = SecondDiver.Player1;
            var good = p1?.GetComponent<InstanceItemInventory>()?.harpoonHandler?.harpoonProjectile;
            if (good == null || broken == null || good.Pointer == broken.Pointer)
            {
                Plugin.Logger.LogInfo("Harpoon compare: no healthy bolt to compare with");
                return;
            }
            var missing = new List<string>();
            foreach (var property in typeof(HarpoonProjectile).GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!typeof(Il2CppObjectBase).IsAssignableFrom(property.PropertyType) || property.GetIndexParameters().Length > 0)
                    continue;
                object a, b;
                try
                {
                    a = property.GetValue(good);
                    b = property.GetValue(broken);
                }
                catch
                {
                    continue;
                }
                if (a != null && b == null)
                    missing.Add(property.Name);
            }
            Plugin.Logger.LogInfo($"Harpoon compare: player 2's bolt is missing {missing.Count} thing(s) player 1's has: {string.Join(", ", missing)}");
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning($"Harpoon compare failed: {exception.Message}");
        }
    }

    // Player 2's copied bolt is missing a few of its own parts (rigidbody,
    // head collider, attach constraint) because the game fills those in at
    // start-up. Point each one at the matching part of the copy.
    internal static void FillMissing(HarpoonProjectile good, HarpoonProjectile copy)
    {
        if (good == null || copy == null)
            return;
        var filled = new List<string>();
        foreach (var property in typeof(HarpoonProjectile).GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!typeof(Component).IsAssignableFrom(property.PropertyType) || !property.CanWrite || property.GetIndexParameters().Length > 0)
                continue;
            try
            {
                if (property.GetValue(copy) != null)
                    continue;
                if (property.GetValue(good) is not Component part || !part.transform.IsChildOf(good.transform))
                    continue;
                var path = RelativePath(good.transform, part.transform);
                var target = path.Length == 0 ? copy.transform : copy.transform.Find(path);
                if (target == null)
                    continue;
                var match = target.GetComponent(part.GetIl2CppType());
                if (match == null)
                    continue;
                property.SetValue(copy, Activator.CreateInstance(property.PropertyType, match.Pointer));
                filled.Add(property.Name);
            }
            catch (Exception exception)
            {
                filled.Add($"{property.Name}(failed: {exception.Message})");
            }
        }
        Plugin.Logger.LogInfo($"Own harpoon: filled in {filled.Count} missing part(s) on player 2's bolt: {string.Join(", ", filled)}");
    }

    private static string RelativePath(Transform root, Transform child)
    {
        var parts = new List<string>();
        for (var t = child; t != null && t != root; t = t.parent)
            parts.Insert(0, t.name);
        return string.Join("/", parts);
    }
}
