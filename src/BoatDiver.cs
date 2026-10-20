using System;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DaveTheDiverCoop;

// On the boat (and other walk-around scenes outside dives and the sushi bar):
// while player 2 is joined, show a purple-flippered copy of Dave standing
// next to him. It's only a picture: the copy has no scripts, physics or input.
internal static class BoatDiver
{
    private static GameObject _copy;
    private static Transform _source;
    private static string _scene = string.Empty;
    private static float _nextSearch;
    private static int _logs;

    internal static void Tick()
    {
        try
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            if (scene != _scene)
            {
                _scene = scene;
                Remove("scene changed");
                _source = null;
            }
            var wanted = P2Keys.Joined && SecondDiver.Player2 == null && !SushiCoop.InRestaurant;
            if (!wanted)
            {
                Remove(P2Keys.Joined ? "not a boat scene" : "player 2 left");
                return;
            }
            if (_copy != null && _source == null)
                Remove("Dave was reloaded"); // find him again below
            if (_copy != null)
            {
                if (!_source.gameObject.activeInHierarchy)
                    _copy.SetActive(false);
                else
                {
                    _copy.SetActive(true);
                    // Stand beside Dave, on the same floor.
                    _copy.transform.position = _source.position + new Vector3(-1.1f, 0f, 0.01f);
                }
                return;
            }
            if (Time.realtimeSinceStartup < _nextSearch)
                return;
            _nextSearch = Time.realtimeSinceStartup + 2f;
            _source = FindWorldDave();
            if (_source != null)
                Spawn(scene);
        }
        catch (Exception exception)
        {
            if (_logs++ < 5)
                Plugin.Logger.LogWarning($"Boat player 2: {exception.Message}");
        }
    }

    // The walking Dave in the scene (not a UI picture, not the diver).
    private static Transform FindWorldDave()
    {
        var handlers = Object.FindObjectsByType<DaveCostumeSwapHandler>(FindObjectsSortMode.None);
        foreach (var handler in handlers)
        {
            if (handler == null || !handler.gameObject.activeInHierarchy)
                continue;
            var go = handler.gameObject;
            if (go.GetComponent<SpriteRenderer>() == null && go.GetComponentInChildren<SpriteRenderer>() == null)
                continue;
            if (go.GetComponentInParent<Canvas>() != null || go.GetComponent<PlayerCharacter>() != null
                || go.GetComponent<StaffDave>() != null || go.name.Contains("(Player 2)"))
                continue;
            return go.transform;
        }
        return null;
    }

    private static void Spawn(string scene)
    {
        _from = _to = null;
        _copy = Object.Instantiate(_source.gameObject, _source.parent);
        _copy.name = _source.name + " (Player 2)";
        // Strip everything but the picture and its animation.
        var behaviours = _copy.GetComponentsInChildren(Il2CppType.Of<MonoBehaviour>(), true);
        for (var i = 0; i < behaviours.Length; i++)
            if (behaviours[i] != null)
                Object.Destroy(behaviours[i]);
        foreach (var type in new[] { Il2CppType.Of<Collider2D>(), Il2CppType.Of<Rigidbody2D>(), Il2CppType.Of<Animator>() })
        {
            var parts = _copy.GetComponentsInChildren(type, true);
            for (var i = 0; i < parts.Length; i++)
                if (parts[i] != null)
                    Object.Destroy(parts[i]);
        }
        _copy.transform.position = _source.position + new Vector3(-1.1f, 0f, 0.01f);
        Plugin.Logger.LogInfo($"Boat player 2: purple Dave shown next to {_source.name} in scene {scene}");
    }

    // After Dave's animation has picked this frame's pictures: show the same
    // pictures (in purple) on the copy, so it walks, turns and idles with him.
    private static Il2CppArrayBase<Component> _from, _to;

    internal static void LateTick()
    {
        if (_copy == null || _source == null || !_copy.activeSelf)
            return;
        try
        {
            if (_from != null && _from.Length > 0 && _from[0] == null)
                _from = _to = null; // Dave was replaced
            if (_from == null || _to == null)
            {
                _from = _source.GetComponentsInChildren(Il2CppType.Of<SpriteRenderer>(), true);
                _to = _copy.GetComponentsInChildren(Il2CppType.Of<SpriteRenderer>(), true);
                if (_from.Length != _to.Length)
                    Plugin.Logger.LogWarning($"Boat player 2: Dave has {_from.Length} pictures, the copy {_to.Length}");
            }
            _copy.transform.localScale = _source.localScale;

            var count = Math.Min(_from.Length, _to.Length);
            for (var i = 0; i < count; i++)
            {
                var a = _from[i]?.TryCast<SpriteRenderer>();
                var b = _to[i]?.TryCast<SpriteRenderer>();
                if (a == null || b == null)
                    continue;
                b.enabled = a.enabled;
                b.flipX = a.flipX;
                b.flipY = a.flipY;
                // Parts inside Dave follow his pose; Dave himself is placed below.
                if (a.transform.Pointer != _source.Pointer)
                {
                    b.transform.localPosition = a.transform.localPosition;
                    b.transform.localRotation = a.transform.localRotation;
                    b.transform.localScale = a.transform.localScale;
                }
                var sprite = a.sprite;
                if (sprite == null)
                {
                    b.sprite = null;
                    continue;
                }
                Sprite purple = null;
                if (P2Skin.Enabled)
                    purple = P2Skin.Recolored(sprite);
                b.sprite = purple ?? sprite;
            }
            // Stand one body-width to Dave's left.
            var width = 1.1f;
            var first = _from.Length > 0 ? _from[0]?.TryCast<SpriteRenderer>() : null;
            if (first != null && first.bounds.size.x > 0.1f)
                width = first.bounds.size.x * 0.9f;
            _copy.transform.position = _source.position + new Vector3(-width, 0f, 0.01f);
        }
        catch (Exception exception)
        {
            _from = _to = null;
            if (_logs++ < 5)
                Plugin.Logger.LogWarning($"Boat player 2 mirror: {exception.Message}");
        }
    }

    private static void Remove(string why)
    {
        // The scene change may already have destroyed the copy: forget the
        // picture lists either way, or a new copy would keep mirroring the
        // old (gone) Dave and stand frozen.
        _from = _to = null;
        if (_copy == null)
            return;
        try { Object.Destroy(_copy); } catch { }
        _copy = null;
        _from = _to = null;
        Plugin.Logger.LogInfo($"Boat player 2: purple Dave removed ({why})");
    }
}
