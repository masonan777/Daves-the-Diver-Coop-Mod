using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace DaveTheDiverCoop;

// Night dives: Dave carries a big "darkness around the diver" picture. The
// copy for player 2 carried one too, which drew a dark box around player 2.
// Hide any picture on player 2 that is far bigger than a diver (the small
// light glow stays).
internal static class NightBox
{
    private const float MaxSize = 6f; // world units; a diver is ~1-2
    private static readonly HashSet<IntPtr> _hidden = new();
    private static IntPtr _diver;
    private static float _next;

    internal static void Tick(PlayerCharacter p2)
    {
        if (p2 == null)
        {
            _diver = IntPtr.Zero;
            _hidden.Clear();
            return;
        }
        if (p2.Pointer != _diver)
        {
            _diver = p2.Pointer;
            _hidden.Clear();
            _next = 0f;
        }
        if (Time.realtimeSinceStartup < _next)
            return;
        _next = Time.realtimeSinceStartup + 1f;
        try
        {
            var renderers = p2.GetComponentsInChildren(Il2CppType.Of<Renderer>(), true);
            for (var i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i]?.TryCast<Renderer>();
                if (r == null || !r.enabled || _hidden.Contains(r.Pointer))
                    continue;
                // Only flat pictures; ropes, trails and particles can be long.
                var kind = r.GetIl2CppType().Name;
                if (kind != "SpriteRenderer" && kind != "MeshRenderer")
                    continue;
                var size = r.bounds.size;
                if (Mathf.Max(size.x, size.y) < MaxSize)
                    continue;
                r.enabled = false;
                _hidden.Add(r.Pointer);
                Plugin.Logger.LogInfo($"Player 2: hid oversized picture '{r.name}' ({r.GetIl2CppType().Name}, {size.x:F1} x {size.y:F1}) - night darkness copy");
            }
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning($"Player 2 night box: {exception.Message}");
            _next = Time.realtimeSinceStartup + 10f;
        }
    }
}
