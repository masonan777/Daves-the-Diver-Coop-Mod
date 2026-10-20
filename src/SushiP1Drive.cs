using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using Object = UnityEngine.Object;

namespace DaveTheDiverCoop;

// Sushi bar: keep player 1 working while player 2 is in a mini-game.
//
// A mini-game (tea, beer, wasabi...) or talk screen takes over the game's input,
// and player 1's restaurant Dave only gets his controls through the game's
// input (StaffDave.OnDirectHandler, from the Cpp2IL dump), so while player 2
// played one, player 1 could not move, serve or clean. Player 2 never had that
// problem because the mod drives player 2's Dave directly. This does the same
// for player 1 while player 2's screen is open: player 1's keys (read from his
// own bindings in the game's "Dave" input map) move his Dave and run serve /
// trash / clean through StaffDave.OnInteraction_Impl, like the game's handler
// does. Meanwhile player 1's keys are kept away from the game
// (KeyboardFilter.MuteP1) so they can't press buttons in player 2's mini-game.
// Mini-games / talking are still one at a time, so those are not offered.
internal static class SushiP1Drive
{
    // Dave map action -> restaurant interaction (StaffDave.OnDirectHandler).
    private static readonly (string action, SushiBarInteraction kind)[] ActionMap =
    {
        ("Sushi_Serve", SushiBarInteraction.Serve),
        ("Sushi_Trash", SushiBarInteraction.Trash),
        ("Sushi_CleanLeft", SushiBarInteraction.CleanLeft),
        ("Sushi_CleanRight", SushiBarInteraction.CleanRight),
    };

    private static Dictionary<string, int[]> _keys;
    private static int[] _up, _down, _left, _right;
    private static readonly HashSet<string> _wasDown = new();
    private static readonly Dictionary<string, SushiBarInteraction> _held = new();
    private static SushiBarPlayerHanlder _handler;
    private static StaffDave _for;
    private static Vector2 _lastMove;
    private static bool _driving;
    private static int _logs;

    internal static bool Driving => _driving;

    internal static bool MoveKeysHeld => _driving && Move().sqrMagnitude > 0.04f;

    // Player 2's screen is open (or not): called every frame from SushiCoop.
    internal static void Tick(StaffDave p1, bool player2ScreenOpen)
    {
        if (p1 == null)
        {
            Stop("no player 1");
            return;
        }
        if (player2ScreenOpen && !_driving)
            Start(p1);
        else if (!player2ScreenOpen && _driving)
            Stop("player 2's screen closed");
        if (_driving)
            Drive(p1);
        else if (SushiCoop.Player1Cleaning)
            CleanKeys(); // finish a clean started during player 2's screen
    }

    private static void Start(StaffDave p1)
    {
        if (_keys == null)
            LoadKeys();
        if (_for == null || _for.Pointer != p1.Pointer)
        {
            _for = p1;
            _handler = p1.GetComponentInChildren<SushiBarPlayerHanlder>(true);
        }
        _driving = true;
        _wasDown.Clear();
        _held.Clear();
        _lastMove = Vector2.zero;
        // Keys already down when the screen opened are not new presses.
        foreach (var (action, _) in ActionMap)
            if (Down(action))
                _wasDown.Add(action);
        KeyboardFilter.MuteP1 = true;
        if (!KeyboardFilter.Active)
            Plugin.Logger.LogInfo("Sushi bar: keyboard filter is off, so player 1's keys may also reach player 2's mini-game");
        Plugin.Logger.LogInfo($"Sushi bar: player 1 driven by the mod while player 2's screen is open (move handler={(_handler != null)})");
    }

    private static void Stop(string why)
    {
        if (!_driving)
            return;
        _driving = false;
        KeyboardFilter.MuteP1 = false;
        try
        {
            if (_handler != null && _lastMove.sqrMagnitude > 0.04f)
                _handler.OnIdle(Vector2.zero);
            // Let go of anything still held (e.g. hold-to-trash).
            foreach (var kind in _held.Values)
                _for?.OnInteraction_Impl(kind, false);
        }
        catch
        {
        }
        _held.Clear();
        _lastMove = Vector2.zero;
        Plugin.Logger.LogInfo($"Sushi bar: player 1 back on the game's own controls ({why})");
    }

    private static void Drive(StaffDave p1)
    {
        if (!KeyboardFilter.GameIsForeground)
            return;
        var move = SushiCoop.Player1Cleaning ? Vector2.zero : Move();
        try
        {
            if (_handler != null)
            {
                if (move.sqrMagnitude > 0.04f)
                    _handler.OnMove(move);
                else if (_lastMove.sqrMagnitude > 0.04f)
                    _handler.OnIdle(Vector2.zero);
            }
        }
        catch (Exception exception)
        {
            if (_logs++ < 10)
                Plugin.Logger.LogWarning($"Sushi bar: player 1 move: {exception.Message}");
        }
        _lastMove = move;

        if (SushiCoop.Player1Cleaning)
        {
            CleanKeys();
            return;
        }
        foreach (var (action, kind) in ActionMap)
        {
            var down = Down(action);
            var was = _wasDown.Contains(action);
            if (down && !was)
            {
                _wasDown.Add(action);
                var ok = false;
                try { ok = p1.OnInteraction_Impl(kind, true); }
                catch (Exception exception) { Plugin.Logger.LogWarning($"Sushi bar: player 1 {kind}: {exception.Message}"); }
                if (ok)
                    _held[action] = kind;
                if (_logs++ < 100)
                    Plugin.Logger.LogInfo($"Sushi bar: player 1 {action} -> {kind} press (mod): {(ok ? "done" : "nothing to do")}");
                if (SushiCoop.Player1Cleaning)
                    return;
            }
            else if (!down && was)
            {
                _wasDown.Remove(action);
                if (_held.TryGetValue(action, out var heldKind))
                {
                    _held.Remove(action);
                    try { p1.OnInteraction_Impl(heldKind, false); }
                    catch (Exception exception) { Plugin.Logger.LogWarning($"Sushi bar: player 1 {kind} release: {exception.Message}"); }
                }
            }
        }
    }

    // Player 1's cleaning run by the mod (the game's clean screen would take
    // everyone's controls): alternate clean-left / clean-right, trash stops.
    private static void CleanKeys()
    {
        var left = Edge("Sushi_CleanLeft");
        var right = Edge("Sushi_CleanRight");
        var stop = Edge("Sushi_Trash");
        SushiCoop.Player1CleanStep(left, right, stop);
    }

    private static bool Edge(string action)
    {
        var down = Down(action);
        var was = _wasDown.Contains(action);
        if (down) _wasDown.Add(action); else _wasDown.Remove(action);
        return down && !was;
    }

    private static bool Down(string action) =>
        _keys != null && _keys.TryGetValue(action, out var vks) && AnyDown(vks);

    private static bool AnyDown(int[] vks)
    {
        if (vks == null)
            return false;
        foreach (var vk in vks)
            if (P2Keys.IsDown(vk))
                return true;
        return false;
    }

    private static Vector2 Move()
    {
        var x = (AnyDown(_right) ? 1f : 0f) - (AnyDown(_left) ? 1f : 0f);
        var y = (AnyDown(_up) ? 1f : 0f) - (AnyDown(_down) ? 1f : 0f);
        var v = new Vector2(x, y);
        return v.sqrMagnitude > 1f ? v.normalized : v;
    }

    // Player 1's keyboard keys for the restaurant actions, from the game's
    // "Dave" input map (so rebinding in the game's options carries over).
    // Player 2's keys are left out.
    private static void LoadKeys()
    {
        _keys = new Dictionary<string, int[]>();
        var up = new List<int>();
        var down = new List<int>();
        var left = new List<int>();
        var right = new List<int>();
        var report = new System.Text.StringBuilder();
        try
        {
            InputActionMap map = null;
            foreach (var asset in Resources.FindObjectsOfTypeAll<InputActionAsset>())
            {
                var maps = asset.actionMaps.ToArray();
                for (var i = 0; i < maps.Length && map == null; i++)
                    if (maps[i] != null && maps[i].name == "Dave" && maps[i].FindAction("Sushi_Serve", false) != null)
                        map = maps[i];
                if (map != null)
                    break;
            }
            if (map == null)
            {
                Plugin.Logger.LogWarning("Sushi bar: the game's Dave input map was not found; player 1 can't be driven during player 2's mini-games");
                return;
            }
            foreach (var (action, _) in ActionMap)
            {
                var vks = new List<int>();
                var a = map.FindAction(action, false);
                if (a != null)
                {
                    var bindings = a.bindings.ToArray();
                    for (var i = 0; i < bindings.Length; i++)
                        AddKey(bindings[i].effectivePath, vks);
                }
                _keys[action] = vks.ToArray();
                report.Append($" {action}=[{string.Join(",", vks.ConvertAll(v => v.ToString("X2")))}]");
            }
            var moveAction = map.FindAction("Move", false);
            if (moveAction != null)
            {
                var bindings = moveAction.bindings.ToArray();
                for (var i = 0; i < bindings.Length; i++)
                {
                    var b = bindings[i];
                    if (!b.isPartOfComposite)
                        continue;
                    switch ((b.name ?? "").ToLowerInvariant())
                    {
                        case "up": AddKey(b.effectivePath, up); break;
                        case "down": AddKey(b.effectivePath, down); break;
                        case "left": AddKey(b.effectivePath, left); break;
                        case "right": AddKey(b.effectivePath, right); break;
                    }
                }
            }
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning($"Sushi bar: reading player 1's keys failed: {exception.Message}");
        }
        _up = up.ToArray();
        _down = down.ToArray();
        _left = left.ToArray();
        _right = right.ToArray();
        Plugin.Logger.LogInfo($"Sushi bar: player 1 keys (virtual-key codes):{report} " +
                              $"move up=[{string.Join(",", up.ConvertAll(v => v.ToString("X2")))}] down=[{string.Join(",", down.ConvertAll(v => v.ToString("X2")))}] " +
                              $"left=[{string.Join(",", left.ConvertAll(v => v.ToString("X2")))}] right=[{string.Join(",", right.ConvertAll(v => v.ToString("X2")))}]");
    }

    private static void AddKey(string path, List<int> into)
    {
        if (string.IsNullOrEmpty(path) || !path.StartsWith("<Keyboard>/", StringComparison.OrdinalIgnoreCase))
            return;
        var name = path.Substring("<Keyboard>/".Length);
        var devices = InputSystem.devices.ToArray();
        for (var i = 0; i < devices.Length; i++)
        {
            var keyboard = devices[i]?.TryCast<Keyboard>();
            if (keyboard == null)
                continue;
            var key = keyboard.TryGetChildControl(name)?.TryCast<KeyControl>();
            if (key == null)
                continue;
            if (P2Keys.TryVirtualKey(key.keyCode, out var vk) && !IsPlayer2Key(vk) && !into.Contains(vk))
                into.Add(vk);
            return;
        }
    }

    private static bool IsPlayer2Key(int vk)
    {
        foreach (var key in P2Keys.AllVirtualKeys)
            if (key == vk)
                return true;
        return false;
    }

    internal static void Reset()
    {
        Stop("reset");
        _for = null;
        _handler = null;
    }
}
