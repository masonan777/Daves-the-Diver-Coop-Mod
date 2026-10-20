using System;
using System.Text;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DaveTheDiverCoop;

// Player 2's mash prompt, using the game's own mash bar (the one player 1
// sees when a shark grabs him).
//
// The game's mash bar (PushButtonQTE) is wired to "the player": while it runs
// it takes player 1's controls and hides the HUD. So this makes a copy of it
// with the PushButtonQTE script removed - only the picture is kept - places it
// over player 2, and fills its bar the same way the game does
// (CurrentBar width = progress x MaxSize).
internal static class P2MashUI
{
    private static GameObject _ui;
    private static RectTransform _root, _bar;
    private static Canvas _canvas;
    private static float _maxSize;
    private static bool _failed;
    private static int _logs;

    internal static bool Showing => _ui != null;

    private static string PathOf(Transform t, Transform root)
    {
        var path = t.name;
        for (var p = t.parent; p != null && p != root; p = p.parent)
            path = p.name + "/" + path;
        return path;
    }

    private static PushButtonQTE Template()
    {
        PushButtonQTE best = null;
        foreach (var q in Resources.FindObjectsOfTypeAll<PushButtonQTE>())
        {
            if (q == null)
                continue;
            if (_logs < 3)
                Plugin.Logger.LogInfo($"P2 mash UI: found game mash bar '{q.gameObject.name}' scene='{q.gameObject.scene.name}' active={q.gameObject.activeSelf}");
            // Prefer the prefab (not one the game is using right now).
            if (best == null || !q.gameObject.scene.IsValid())
                best = q;
        }
        return best;
    }

    internal static bool Show()
    {
        if (_ui != null)
            return true;
        if (_failed)
            return false;
        try
        {
            var template = Template();
            if (template == null)
            {
                if (_logs++ < 5)
                    Plugin.Logger.LogInfo("P2 mash UI: the game's mash bar is not loaded; using the mod's bar");
                return false;
            }
            var hud = Object.FindFirstObjectByType<MainCanvasManager>();
            _canvas = hud != null ? hud.GetComponentInParent<Canvas>() ?? hud.GetComponentInChildren<Canvas>() : null;
            _canvas ??= Object.FindFirstObjectByType<Canvas>();
            if (_canvas == null)
            {
                Plugin.Logger.LogWarning("P2 mash UI: no canvas found");
                _failed = true;
                return false;
            }
            var barPath = template.CurrentBar != null ? PathOf(template.CurrentBar, template.transform) : null;
            _maxSize = template.MaxSize;

            // Copy it switched off, so none of its scripts wake up.
            var wasActive = template.gameObject.activeSelf;
            template.gameObject.SetActive(false);
            try { _ui = Object.Instantiate(template.gameObject, _canvas.rootCanvas.transform, false); }
            finally { template.gameObject.SetActive(wasActive); }
            _ui.name = "P2 Mash Bar";
            var removed = new StringBuilder();
            foreach (var mb in _ui.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null)
                    continue;
                var type = mb.GetIl2CppType();
                var typeName = type.FullName ?? type.Name;
                // Keep plain UI pieces (images, text, layout); drop game logic.
                if (typeName.StartsWith("UnityEngine.UI.") || typeName.StartsWith("TMPro."))
                    continue;
                removed.Append(type.Name).Append(' ');
                Object.DestroyImmediate(mb);
            }
            _root = _ui.GetComponent<RectTransform>();
            _bar = barPath != null ? _ui.transform.Find(barPath)?.GetComponent<RectTransform>() : null;
            _ui.SetActive(true);
            _ui.transform.SetAsLastSibling();
            if (_logs++ < 5)
            {
                Plugin.Logger.LogInfo($"P2 mash UI: copied '{template.gameObject.name}' into canvas '{_canvas.rootCanvas.name}' ({_canvas.rootCanvas.renderMode}); bar={barPath} maxSize={_maxSize}; removed scripts: {removed}");
                Describe();
            }
            Set(0f);
            return true;
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning($"P2 mash UI: could not copy the game's mash bar ({exception.Message}); using the mod's bar");
            Hide();
            _failed = true;
            return false;
        }
    }

    private static void Describe()
    {
        try
        {
            var text = new StringBuilder();
            foreach (var t in _ui.GetComponentsInChildren<Transform>(true))
                text.Append(PathOf(t, _ui.transform)).Append(t.gameObject.activeSelf ? "" : "(off)").Append("; ");
            Plugin.Logger.LogInfo($"P2 mash UI parts: {text}");
            var animator = _ui.GetComponent<Animator>();
            if (animator != null)
            {
                var ps = new StringBuilder();
                foreach (var p in animator.parameters)
                    ps.Append(p.name).Append('(').Append(p.type).Append(") ");
                Plugin.Logger.LogInfo($"P2 mash UI animator parameters: {ps}");
            }
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogInfo($"P2 mash UI describe: {exception.Message}");
        }
    }

    // progress 0..1
    internal static void Set(float progress)
    {
        if (_bar == null)
            return;
        // The game sets the bar's right anchor to progress x MaxSize (MaxSize is 1).
        var max = _bar.anchorMax;
        _bar.anchorMax = new Vector2(_maxSize * Mathf.Clamp01(progress), max.y);
    }

    // Keep it over player 2.
    internal static void Follow(Vector3 world)
    {
        if (_root == null || _canvas == null)
            return;
        var cam = Camera.main;
        if (cam == null)
            return;
        var screen = (Vector2)cam.WorldToScreenPoint(world) + new Vector2(0f, Screen.height * 0.12f);
        var canvas = _canvas.rootCanvas;
        if (canvas.renderMode == RenderMode.ScreenSpaceOverlay)
            _root.position = screen;
        else if (RectTransformUtility.ScreenPointToWorldPointInRectangle(canvas.GetComponent<RectTransform>(), screen, canvas.worldCamera, out var at))
            _root.position = at;
    }

    internal static void Hide()
    {
        if (_ui != null)
            Object.Destroy(_ui);
        _ui = null;
        _root = null;
        _bar = null;
    }
}
