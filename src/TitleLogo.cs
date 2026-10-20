using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace DaveTheDiverCoop;

// Title screen logo -> "Daves the Divers".
// 1. Finds pictures whose name mentions "logo" and saves each one once to
//    BepInEx/coop_logo_dump/<name>.png (so the edited version can be made).
// 2. If BepInEx/config/coop_logo/<name>.png exists, shows that instead.
internal static class TitleLogo
{
    private static readonly string DumpDir = System.IO.Path.Combine(Paths.BepInExRootPath, "coop_logo_dump");
    private static readonly string ReplaceDir = System.IO.Path.Combine(Paths.ConfigPath, "coop_logo");
    private static readonly HashSet<string> _dumped = new();
    private static readonly Dictionary<string, Sprite> _replacements = new();
    private static readonly HashSet<string> _missing = new();
    private static readonly HashSet<IntPtr> _widened = new();
    private static float _next;
    private static string _scene = "";

    internal static void Tick()
    {
        if (Time.realtimeSinceStartup < _next)
            return;
        _next = Time.realtimeSinceStartup + 1f;
        try
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            var newScene = scene != _scene;
            _scene = scene;
            var images = Object.FindObjectsByType<Image>(FindObjectsSortMode.None);
            foreach (var image in images)
            {
                var sprite = image?.sprite;
                if (!IsLogo(image?.name, sprite))
                    continue;
                if (newScene)
                    Plugin.Logger.LogInfo($"Title logo: {PathOf(image.transform)} sprite={sprite?.name} rect={sprite?.rect} scene={scene}");
                var swap = Replacement(sprite);
                if (swap != null)
                {
                    if (image.sprite.Pointer != swap.Pointer)
                    {
                        // The new logo is wider: widen its box by the same
                        // amount so the letters keep their shape.
                        var rt = image.rectTransform;
                        var ratio = swap.rect.width / swap.rect.height / (sprite.rect.width / sprite.rect.height);
                        if (_widened.Add(image.Pointer) && Mathf.Abs(ratio - 1f) > 0.01f)
                            rt.sizeDelta = new Vector2(rt.rect.width * ratio - (rt.rect.width - rt.sizeDelta.x), rt.sizeDelta.y);
                        image.sprite = swap;
                    }
                    continue;
                }
                Dump(sprite);
            }
            var renderers = Object.FindObjectsByType<SpriteRenderer>(FindObjectsSortMode.None);
            foreach (var r in renderers)
            {
                var sprite = r?.sprite;
                if (!IsLogo(r?.name, sprite))
                    continue;
                if (newScene)
                    Plugin.Logger.LogInfo($"Title logo: {PathOf(r.transform)} sprite={sprite?.name} rect={sprite?.rect} scene={scene}");
                var swap = Replacement(sprite);
                if (swap != null)
                {
                    if (r.sprite.Pointer != swap.Pointer)
                        r.sprite = swap;
                    continue;
                }
                Dump(sprite);
            }
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning($"Title logo: {exception.Message}");
            _next = Time.realtimeSinceStartup + 10f;
        }
    }

    private static bool IsLogo(string objectName, Sprite sprite)
    {
        if (sprite == null)
            return false;
        bool Has(string s) => s != null && s.IndexOf("logo", StringComparison.OrdinalIgnoreCase) >= 0;
        return (Has(objectName) || Has(sprite.name)) && !sprite.name.EndsWith("(Daves)");
    }

    private static string PathOf(Transform t)
    {
        var path = t.name;
        for (var p = t.parent; p != null; p = p.parent)
            path = p.name + "/" + path;
        return path;
    }

    private static string FileName(Sprite sprite)
    {
        var name = sprite.name;
        foreach (var c in System.IO.Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name + ".png";
    }

    private static void Dump(Sprite sprite)
    {
        var file = FileName(sprite);
        if (!_dumped.Add(file))
            return;
        try
        {
            Directory.CreateDirectory(DumpDir);
            var sheet = P2Skin.Readable(sprite.texture);
            var rect = sprite.packed ? sprite.textureRect : sprite.rect;
            var w = Mathf.RoundToInt(rect.width);
            var h = Mathf.RoundToInt(rect.height);
            var cut = new Texture2D(w, h, TextureFormat.RGBA32, false);
            cut.SetPixels(sheet.GetPixels(Mathf.RoundToInt(rect.x), Mathf.RoundToInt(rect.y), w, h));
            cut.Apply();
            File.WriteAllBytes(System.IO.Path.Combine(DumpDir, file), ImageConversion.EncodeToPNG(cut));
            Plugin.Logger.LogInfo($"Title logo: saved {file} ({w}x{h}, pivot {sprite.pivot}, {sprite.pixelsPerUnit} px/unit, packed={sprite.packed})");
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning($"Title logo: could not save {file}: {exception.Message}");
        }
    }

    private static Sprite Replacement(Sprite original)
    {
        var file = FileName(original);
        if (_replacements.TryGetValue(file, out var done))
            return done;
        if (_missing.Contains(file))
            return null;
        var path = System.IO.Path.Combine(ReplaceDir, file);
        if (!File.Exists(path))
        {
            _missing.Add(file);
            return null;
        }
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        ImageConversion.LoadImage(texture, File.ReadAllBytes(path));
        texture.hideFlags = HideFlags.HideAndDontSave;
        // Keep the same on-screen size and anchor: scale pixels-per-unit with
        // the new width, and keep the pivot at the same relative spot.
        var oldRect = original.rect;
        var pivot = new Vector2(original.pivot.x / oldRect.width, original.pivot.y / oldRect.height);
        var ppu = original.pixelsPerUnit * texture.width / oldRect.width;
        var sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), pivot, ppu, 0, SpriteMeshType.FullRect);
        sprite.name = original.name + " (Daves)";
        sprite.hideFlags = HideFlags.HideAndDontSave;
        _replacements[file] = sprite;
        _replacements[FileName(sprite)] = sprite;
        Plugin.Logger.LogInfo($"Title logo: showing {path} instead of {original.name}");
        return sprite;
    }
}
