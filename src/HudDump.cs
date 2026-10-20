using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace DaveTheDiverCoop;

// Diagnostics: write out what the on-screen HUD is made of (especially the
// bottom-right item/weapon area) so a player 2 copy can be built from it.
internal static class HudDump
{
    private static float _autoAt = -1f;

    internal static void ScheduleAfterSpawn() => _autoAt = Time.realtimeSinceStartup + 3f;

    internal static void Tick()
    {
        if (_autoAt > 0f && Time.realtimeSinceStartup >= _autoAt)
        {
            _autoAt = -1f;
            Dump("3 s after player 2 joined");
        }
    }

    internal static void Dump(string reason)
    {
        try
        {
            var text = new StringBuilder();
            text.AppendLine($"HUD dump ({reason}) screen={Screen.width}x{Screen.height}");
            var scripts = new SortedDictionary<string, int>();
            var canvases = Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None);
            foreach (var canvas in canvases)
            {
                if (canvas == null || !canvas.isRootCanvas || !canvas.gameObject.activeInHierarchy)
                    continue;
                text.AppendLine($"CANVAS {canvas.name} mode={canvas.renderMode} order={canvas.sortingOrder} cam={canvas.worldCamera?.name}");
                Walk(canvas.transform, canvas, 1, text, scripts);
            }
            text.AppendLine("SCRIPTS IN BOTTOM-RIGHT AREA:");
            foreach (var pair in scripts)
                text.AppendLine($"  {pair.Key} x{pair.Value}");
            var path = Path.Combine(BepInEx.Paths.BepInExRootPath, "coop_hud.txt");
            File.WriteAllText(path, text.ToString());
            Plugin.Logger.LogInfo($"HUD layout written to {path} ({reason})");
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning($"HUD dump failed: {exception.Message}");
        }
    }

    private static readonly Vector3[] Corners = new Vector3[4];

    private static void Walk(Transform node, Canvas canvas, int depth, StringBuilder text, SortedDictionary<string, int> scripts)
    {
        if (depth > 14)
            return;
        for (var i = 0; i < node.childCount; i++)
        {
            var child = node.GetChild(i);
            if (child == null || !child.gameObject.activeInHierarchy)
                continue;
            var rect = child.TryCast<RectTransform>();
            var where = "";
            var bottomRight = false;
            if (rect != null)
            {
                rect.GetWorldCorners(Corners);
                var cam = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
                Vector2 a = RectTransformUtility.WorldToScreenPoint(cam, Corners[0]);
                Vector2 b = RectTransformUtility.WorldToScreenPoint(cam, Corners[2]);
                var cx = (a.x + b.x) * 0.5f / Screen.width;
                var cy = (a.y + b.y) * 0.5f / Screen.height;
                bottomRight = cx > 0.55f && cy < 0.45f;
                where = $" at=({cx:F2},{cy:F2}) size=({(b.x - a.x):F0}x{(b.y - a.y):F0})";
            }
            var components = new List<string>();
            foreach (var component in child.GetComponents<Component>())
            {
                if (component == null)
                    continue;
                var name = component.GetIl2CppType().Name;
                if (name == "Transform" || name == "RectTransform" || name == "CanvasRenderer")
                    continue;
                if (component.TryCast<Image>() is { } image && image.sprite != null)
                    name += $"[{image.sprite.name}]";
                components.Add(name);
                var full = component.GetIl2CppType().FullName ?? name;
                if (bottomRight && !full.StartsWith("UnityEngine") && !full.StartsWith("TMPro"))
                    scripts[full] = scripts.TryGetValue(full, out var n) ? n + 1 : 1;
            }
            text.AppendLine($"{new string(' ', depth * 2)}{(bottomRight ? "*" : "")}{child.name}{where} [{string.Join(", ", components)}]");
            Walk(child, canvas, depth + 1, text, scripts);
        }
    }
}

[HarmonyPatch(typeof(InstanceItemInventory), nameof(InstanceItemInventory.TryPickupItem))]
internal static class TracePickupWeapon
{
    private static void Prefix(InstanceItemInventory __instance)
    {
        var who = __instance.GetComponent<PlayerCharacter>();
        Plugin.Logger.LogInfo($"Pickup (weapon/item) by {(SecondDiver.IsPlayer2(who) ? "P2" : "P1")}");
    }
}

[HarmonyPatch(typeof(SubHelperItemInventory), nameof(SubHelperItemInventory.TryPickupItem))]
internal static class TracePickupHelper
{
    private static void Prefix(SubHelperItemInventory __instance)
    {
        var who = __instance.GetComponent<PlayerCharacter>();
        Plugin.Logger.LogInfo($"Pickup (helper item) by {(SecondDiver.IsPlayer2(who) ? "P2" : "P1")}");
    }
}
