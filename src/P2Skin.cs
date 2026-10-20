using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Il2CppInterop.Runtime;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DaveTheDiverCoop;

// Player 2's diver gets purple flippers and goggles instead of yellow. Dave is
// drawn from a sprite sheet, so a recoloured copy of the sheet is made once
// (yellow pixels turned purple, nothing else changed) and every frame player
// 2's body parts are switched to the matching sprite cut from that copy.
internal static class P2Skin
{
    private static ConfigEntry<float> _hue;
    private static readonly Dictionary<IntPtr, Texture2D> _textures = new();
    private static readonly Dictionary<IntPtr, Sprite> _sprites = new();
    private static readonly HashSet<IntPtr> _failed = new();
    private static SpriteRenderer[] _renderers;
    private static IntPtr _diver;
    private static int _logs;

    internal static void Bind(ConfigFile config) =>
        _hue = config.Bind("Player2", "SuitColourHue", 0.78f,
            "Colour for player 2's flippers and goggles as a hue from 0 to 1 (0.78 = purple, 0.55 = blue, 0.33 = green, 0 = red). Set below 0 to keep yellow.");

    internal static void Apply(PlayerCharacter p2)
    {
        if (p2 == null || (_hue != null && _hue.Value < 0f))
            return;
        try
        {
            if (p2.Pointer != _diver)
            {
                _diver = p2.Pointer;
                _renderers = FindBodyRenderers(p2);
            }
            if (_renderers == null)
                return;
            foreach (var renderer in _renderers)
            {
                if (renderer == null)
                    continue;
                var sprite = renderer.sprite;
                if (sprite == null)
                    continue;
                var swapped = Recolored(sprite);
                if (swapped != null && renderer.sprite.Pointer != swapped.Pointer)
                    renderer.sprite = swapped;
            }
        }
        catch (Exception exception)
        {
            if (_logs++ < 3)
                Plugin.Logger.LogWarning($"Player 2 suit colour: {exception.Message}");
        }
    }

    // The diver's own body parts: everything drawn from the same sheet as the
    // main body sprite. Their purple tint is removed (only the yellow changes).
    private static SpriteRenderer[] FindBodyRenderers(PlayerCharacter p2)
    {
        var body = p2.transform.Find("CharacterBody")?.GetComponent<SpriteRenderer>();
        var sheet = body?.sprite?.texture;
        if (sheet == null)
        {
            Plugin.Logger.LogWarning("Player 2 suit colour: body sprite not found");
            return null;
        }
        var list = new List<SpriteRenderer>();
        var all = p2.GetComponentsInChildren(Il2CppType.Of<SpriteRenderer>(), true);
        for (var i = 0; i < all.Length; i++)
        {
            var renderer = all[i]?.TryCast<SpriteRenderer>();
            var texture = renderer?.sprite?.texture;
            if (renderer == null || texture == null || texture.Pointer != sheet.Pointer)
                continue;
            renderer.color = Color.white;
            list.Add(renderer);
        }
        Plugin.Logger.LogInfo($"Player 2 suit colour: {list.Count} body part(s) on sheet {sheet.name} ({sheet.width}x{sheet.height})");
        return list.ToArray();
    }

    internal static bool Enabled => _hue == null || _hue.Value >= 0f;

    private static readonly Dictionary<IntPtr, Sprite> _uiSprites = new();

    // For menu pictures (UI images). A sprite packed tightly in an atlas has
    // its empty border trimmed; menu images still size themselves by the full
    // untrimmed frame, so the trimmed copy came out stretched. Build a small
    // texture of the full frame with the recoloured pixels at the right spot.
    internal static Sprite RecoloredForMenu(Sprite original)
    {
        if (!original.packed)
            return Recolored(original);
        if (_uiSprites.TryGetValue(original.Pointer, out var done))
            return done;
        var source = original.texture;
        if (source == null || _failed.Contains(source.Pointer))
            return null;
        var sheet = RecoloredSheet(source);
        if (sheet == null)
            return null;
        var full = original.rect;
        var trimmed = original.textureRect;
        var offset = original.textureRectOffset;
        var width = Mathf.Max(1, Mathf.RoundToInt(full.width));
        var height = Mathf.Max(1, Mathf.RoundToInt(full.height));
        var frame = new Texture2D(width, height, TextureFormat.RGBA32, false);
        var clear = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Color32>(width * height);
        frame.SetPixels32(clear);
        var w = Mathf.RoundToInt(trimmed.width);
        var h = Mathf.RoundToInt(trimmed.height);
        var block = sheet.GetPixels(Mathf.RoundToInt(trimmed.x), Mathf.RoundToInt(trimmed.y), w, h);
        frame.SetPixels(Mathf.RoundToInt(offset.x), Mathf.RoundToInt(offset.y), w, h, block);
        frame.filterMode = source.filterMode;
        frame.Apply(false, false);
        Object.DontDestroyOnLoad(frame);
        var pivot = new Vector2(original.pivot.x / full.width, original.pivot.y / full.height);
        var sprite = Sprite.Create(frame, new Rect(0, 0, width, height), pivot, original.pixelsPerUnit, 0, SpriteMeshType.FullRect);
        sprite.name = original.name + " (Player 2 menu)";
        _uiSprites[original.Pointer] = sprite;
        _uiSprites[sprite.Pointer] = sprite;
        return sprite;
    }

    internal static Sprite Recolored(Sprite original)
    {
        if (_sprites.TryGetValue(original.Pointer, out var done))
            return done;
        var texture = original.texture;
        if (texture == null || _failed.Contains(texture.Pointer))
            return null;
        var sheet = RecoloredSheet(texture);
        if (sheet == null)
            return null;
        // Sprites packed into an atlas sit at textureRect, trimmed by
        // textureRectOffset; keep the pivot at the same spot on the body.
        var rect = original.rect;
        var offset = Vector2.zero;
        if (original.packed)
        {
            rect = original.textureRect;
            offset = original.textureRectOffset;
        }
        var pivot = new Vector2((original.pivot.x - offset.x) / rect.width, (original.pivot.y - offset.y) / rect.height);
        var sprite = Sprite.Create(sheet, rect, pivot, original.pixelsPerUnit, 0, SpriteMeshType.FullRect);
        sprite.name = original.name + " (Player 2)";
        _sprites[original.Pointer] = sprite;
        _sprites[sprite.Pointer] = sprite; // already swapped
        return sprite;
    }

    private static Texture2D RecoloredSheet(Texture2D source)
    {
        if (_textures.TryGetValue(source.Pointer, out var done))
            return done;
        try
        {
            var started = Time.realtimeSinceStartup;
            var copy = Readable(source);
            var pixels = copy.GetPixels32();
            var hue = _hue?.Value ?? 0.78f;
            var changed = 0;
            for (var i = 0; i < pixels.Length; i++)
            {
                var p = pixels[i];
                if (p.a < 8)
                    continue;
                Color.RGBToHSV(new Color(p.r / 255f, p.g / 255f, p.b / 255f, 1f), out var h, out var s, out var v);
                // Yellow / golden (not skin tones, which are redder and paler).
                if (h < 0.10f || h > 0.19f || s < 0.45f || v < 0.25f)
                    continue;
                var c = Color.HSVToRGB(hue, s, v);
                pixels[i] = new Color32((byte)(c.r * 255f), (byte)(c.g * 255f), (byte)(c.b * 255f), p.a);
                changed++;
            }
            copy.SetPixels32(pixels);
            copy.Apply(false, false);
            copy.filterMode = source.filterMode;
            copy.wrapMode = source.wrapMode;
            copy.name = source.name + " (Player 2)";
            Object.DontDestroyOnLoad(copy);
            _textures[source.Pointer] = copy;
            Plugin.Logger.LogInfo($"Player 2 suit colour: recoloured {changed} pixel(s) of {source.name} in {Time.realtimeSinceStartup - started:F2}s");
            return copy;
        }
        catch (Exception exception)
        {
            _failed.Add(source.Pointer);
            Plugin.Logger.LogWarning($"Player 2 suit colour: could not copy {source.name}: {exception.Message}");
            return null;
        }
    }

    // Game textures are usually not readable from code; draw the sheet into a
    // temporary render target and read that back.
    internal static Texture2D Readable(Texture2D source)
    {
        var target = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        var previous = RenderTexture.active;
        try
        {
            Graphics.Blit(source, target);
            RenderTexture.active = target;
            var copy = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
            copy.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
            copy.Apply(false, false);
            return copy;
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(target);
        }
    }
}
