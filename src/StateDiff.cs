using System;
using System.Collections.Generic;
using System.Reflection;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace DaveTheDiverCoop;

// Diagnostics: read every simple value (bool/number/enum) on player 2's diver
// and knife, so the state before the first swing can be compared with the
// state before the second one. Whatever differs is what blocks the knife.
internal static class StateDiff
{
    private static Dictionary<string, string> _first;

    private static void Read(object target, string prefix, Dictionary<string, string> into)
    {
        if (target == null)
            return;
        foreach (var property in target.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            var declaring = property.DeclaringType?.Namespace ?? "";
            if (declaring.StartsWith("UnityEngine") || declaring.StartsWith("Il2Cpp") || declaring.StartsWith("System"))
                continue;
            if (!property.CanRead || property.GetIndexParameters().Length > 0)
                continue;
            var type = property.PropertyType;
            if (!(type.IsPrimitive || type.IsEnum))
                continue;
            if (property.Name.Contains("NativeMethodInfo") || property.Name.Contains("NativeField"))
                continue;
            try
            {
                var value = property.GetValue(target);
                into[prefix + property.Name] = value is float f ? f.ToString("F2") : value?.ToString();
            }
            catch
            {
            }
        }
    }

    internal static Dictionary<string, string> Snapshot(PlayerCharacter p)
    {
        var values = new Dictionary<string, string>();
        Read(p, "", values);
        try
        {
            var handlers = p.GetComponentsInChildren(Il2CppType.Of<MeleeWeaponHandler>(), true);
            for (var i = 0; i < handlers.Length; i++)
                Read(handlers[i].TryCast<MeleeWeaponHandler>(), $"knife{i}.", values);
            var inventory = p.GetComponent<InstanceItemInventory>();
            Read(inventory?.meleeHandler, "meleeSlot.", values);
        }
        catch (Exception exception)
        {
            values["(read error)"] = exception.Message;
        }
        return values;
    }

    internal static void OnPress(PlayerCharacter p2, int press)
    {
        try
        {
            var now = Snapshot(p2);
            if (press == 1 || _first == null)
            {
                _first = now;
                Plugin.Logger.LogInfo($"StateDiff: saved {now.Count} values before swing #1");
                // Also compare player 2 with player 1 (who can swing fine).
                var p1 = SecondDiver.Player1;
                if (p1 != null && !p1.IsPlayMeleeAttack)
                    LogDiff("P2 vs P1 (before swing #1)", Snapshot(p1), now, "P1", "P2");
                return;
            }
            LogDiff($"before swing #{press} vs before swing #1", _first, now, "#1", $"#{press}");
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning($"StateDiff failed: {exception.Message}");
        }
    }

    private static readonly string[] Noise = { "Position", "Velocity", "Time", "Timer", "Angle", "Speed", "HP", "Depth", "Count", "Input", "Axis", "Direction" };

    private static void LogDiff(string title, Dictionary<string, string> a, Dictionary<string, string> b, string aName, string bName)
    {
        var lines = new List<string>();
        foreach (var pair in b)
        {
            a.TryGetValue(pair.Key, out var before);
            if (before == pair.Value)
                continue;
            var noisy = false;
            foreach (var word in Noise)
                if (pair.Key.Contains(word)) { noisy = true; break; }
            lines.Add($"{(noisy ? "~" : "")}{pair.Key}: {aName}={before ?? "missing"} {bName}={pair.Value}");
        }
        foreach (var key in a.Keys)
            if (!b.ContainsKey(key))
                lines.Add($"{key}: {aName}={a[key]} {bName}=missing");
        Plugin.Logger.LogInfo($"StateDiff {title}: {lines.Count} differences");
        foreach (var line in lines)
            Plugin.Logger.LogInfo("   " + line);
    }
}
