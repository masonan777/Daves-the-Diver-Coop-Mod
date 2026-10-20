using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.InputSystem;

namespace DaveTheDiverCoop;

// Player 2's keyboard, read straight from Windows so it never reaches the
// game's own input (which belongs to player 1).
internal static class P2Keys
{
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    internal enum Action
    {
        Up, Down, Left, Right,
        AimUp, AimDown, AimLeft, AimRight,
        Interact, Ok, Back, Fire, Dash, ShortDash, Item, SwitchItem, SwitchWeapon,
    }

    private static readonly (Action Action, string Keys, string Label)[] Defaults =
    {
        (Action.Up, "UpArrow", "Swim up"),
        (Action.Down, "DownArrow", "Swim down"),
        (Action.Left, "LeftArrow", "Swim left"),
        (Action.Right, "RightArrow", "Swim right"),
        (Action.AimUp, "", "Aim up (optional)"),
        (Action.AimDown, "", "Aim down (optional)"),
        (Action.AimLeft, "", "Aim left (optional)"),
        (Action.AimRight, "", "Aim right (optional)"),
        (Action.Interact, "RightShift", "Interact / pick up"),
        (Action.Ok, "Enter", "OK / hold to aim"),
        (Action.Back, "Backspace", "Back / melee"),
        (Action.Fire, "Slash|RightAlt", "Fire harpoon"),
        (Action.Dash, "Period", "Long dash"),
        (Action.ShortDash, "Comma", "Short dash"),
        (Action.Item, "U", "Use item"),
        (Action.SwitchItem, "I", "Switch item"),
        (Action.SwitchWeapon, "O", "Switch weapon"),
    };

    private static ConfigEntry<string> _joinKey;
    private static ConfigEntry<bool> _joinedAtStart;
    private static ConfigEntry<bool> _filterPlayerOne;
    internal static bool FilterPlayerOne => _filterPlayerOne?.Value == true;
    private static ConfigEntry<string> _spawnSteps;
    internal static bool Step(string name) =>
        _spawnSteps == null || ("," + _spawnSteps.Value.Replace(" ", "") + ",").Contains("," + name + ",");
    private static readonly Dictionary<Action, ConfigEntry<string>> _entries = new();
    private static readonly Dictionary<Action, int[]> _keys = new();
    private static readonly HashSet<int> _allKeys = new();
    private static readonly HashSet<Action> _down = new();
    private static readonly HashSet<Action> _pressed = new();
    private static readonly HashSet<Action> _released = new();
    private static readonly Dictionary<Action, float> _heldSince = new();
    private static bool _joinWasDown;

    internal static bool Joined { get; private set; }

    internal static void Leave(string why)
    {
        if (!Joined)
            return;
        Joined = false;
        Plugin.Logger.LogInfo($"Player 2 left ({why})");
    }

    internal static void JoinFromController()
    {
        Joined = true;
        Plugin.Logger.LogInfo("Player 2 joined (controller)");
    }
    internal static IReadOnlyCollection<int> AllVirtualKeys => _allKeys;
    internal static IEnumerable<(Action Action, string Keys, string Label)> Bindings
    {
        get
        {
            foreach (var (action, _, label) in Defaults)
                yield return (action, _entries[action].Value, label);
        }
    }
    internal static string JoinKeyName => _joinKey.Value;

    internal static void Bind(ConfigFile config)
    {
        _joinKey = config.Bind("Player2", "JoinKey", "F9", "Key that adds or removes player 2");
        _joinedAtStart = config.Bind("Player2", "JoinedAtStart", false, "Player 2 is already in when the game starts");
        _spawnSteps = config.Bind("Debug", "SpawnSteps", "rewire,animator,harpoon,equip,armInit",
            "Which player 2 setup steps run (for tracking down bugs)");
        _filterPlayerOne = config.Bind("Player2", "HidePlayer2KeysFromPlayer1", false,
            "Replace player 1's keyboard with a filtered copy. Off by default: it broke player 1's mouse aiming, " +
            "and in dives the game does not use player 2's keys for player 1 anyway.");
        foreach (var (action, keys, label) in Defaults)
            _entries[action] = config.Bind("Player2Keys", action.ToString(), keys,
                $"{label}. Unity key names (UpArrow, RightShift, Enter, U, Slash...); use | for several");
        Joined = _joinedAtStart.Value;
        Resolve();
    }

    private static void Resolve()
    {
        _keys.Clear();
        _allKeys.Clear();
        foreach (var pair in _entries)
        {
            var list = new List<int>();
            foreach (var raw in (pair.Value.Value ?? string.Empty).Split('|', ',', ' '))
            {
                var name = raw.Trim();
                if (name.Length == 0)
                    continue;
                if (Enum.TryParse(name, true, out Key key) && TryVirtualKey(key, out var vk))
                {
                    list.Add(vk);
                    _allKeys.Add(vk);
                }
                else
                {
                    Plugin.Logger.LogWarning($"Player 2 key '{name}' for {pair.Key} is not a known key");
                }
            }
            _keys[pair.Key] = list.ToArray();
        }
    }

    internal static void Tick()
    {
        if (Enum.TryParse(_joinKey.Value, true, out Key joinKey) && TryVirtualKey(joinKey, out var joinVk))
        {
            var down = IsDown(joinVk);
            if (down && !_joinWasDown)
            {
                Joined = !Joined;
                Plugin.Logger.LogInfo(Joined ? "Player 2 joined" : "Player 2 left");
            }
            _joinWasDown = down;
        }

        P2Pad.Tick();

        _pressed.Clear();
        _released.Clear();
        foreach (var pair in _keys)
        {
            var isDown = false;
            foreach (var vk in pair.Value)
                isDown |= IsDown(vk);
            isDown |= P2Pad.Down(pair.Key);
            var wasDown = _down.Contains(pair.Key);
            if (isDown && !wasDown)
            {
                _pressed.Add(pair.Key);
                _heldSince[pair.Key] = Time.realtimeSinceStartup;
            }
            if (!isDown && wasDown)
                _released.Add(pair.Key);
            if (isDown)
                _down.Add(pair.Key);
            else
                _down.Remove(pair.Key);
        }
    }

    internal static bool Held(Action action) => _down.Contains(action);
    internal static bool Pressed(Action action) => _pressed.Contains(action);
    internal static bool Released(Action action) => _released.Contains(action);
    internal static float HeldSeconds(Action action) =>
        _down.Contains(action) && _heldSince.TryGetValue(action, out var since)
            ? Time.realtimeSinceStartup - since
            : 0f;

    internal static Vector2 Move
    {
        get
        {
            var stick = P2Pad.LeftStickValue;
            return stick != Vector2.zero ? stick : Axis(Action.Left, Action.Right, Action.Down, Action.Up);
        }
    }

    // Aim keys if any are held; otherwise aim where player 2 is swimming
    // while they hold OK/aim or fire.
    private static Vector2 _lastAim = Vector2.right;

    internal static Vector2 Aim
    {
        get
        {
            var aim = P2Pad.RightStickValue;
            if (aim == Vector2.zero)
                aim = Axis(Action.AimLeft, Action.AimRight, Action.AimDown, Action.AimUp);
            var aiming = Held(Action.Ok) || Held(Action.Fire);
            if (aim == Vector2.zero && aiming)
                aim = Move;
            if (aim != Vector2.zero)
                _lastAim = aim;
            else if (Move != Vector2.zero)
                _lastAim = Move;
            // Aiming without a direction held: keep pointing the last way
            // player 2 aimed or swam (the game needs a direction to spread
            // multi-barrel guns).
            if (aim == Vector2.zero && aiming)
                aim = _lastAim;
            return aim;
        }
    }

    private static Vector2 Axis(Action left, Action right, Action down, Action up)
    {
        var x = (Held(right) ? 1f : 0f) - (Held(left) ? 1f : 0f);
        var y = (Held(up) ? 1f : 0f) - (Held(down) ? 1f : 0f);
        var v = new Vector2(x, y);
        return v.sqrMagnitude > 1f ? v.normalized : v;
    }

    internal static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    // Unity Input System Key -> Windows virtual-key code.
    internal static bool TryVirtualKey(Key key, out int vk)
    {
        var k = (int)key;
        vk = 0;
        if (key >= Key.A && key <= Key.Z) vk = 0x41 + (k - (int)Key.A);
        else if (key >= Key.Digit1 && key <= Key.Digit9) vk = 0x31 + (k - (int)Key.Digit1);
        else if (key >= Key.Numpad0 && key <= Key.Numpad9) vk = 0x60 + (k - (int)Key.Numpad0);
        else if (key >= Key.F1 && key <= Key.F12) vk = 0x70 + (k - (int)Key.F1);
        else
            vk = key switch
            {
                Key.Digit0 => 0x30, Key.Space => 0x20, Key.Enter => 0x0D, Key.Tab => 0x09,
                Key.Backquote => 0xC0, Key.Quote => 0xDE, Key.Semicolon => 0xBA, Key.Comma => 0xBC,
                Key.Period => 0xBE, Key.Slash => 0xBF, Key.Backslash => 0xDC, Key.LeftBracket => 0xDB,
                Key.RightBracket => 0xDD, Key.Minus => 0xBD, Key.Equals => 0xBB,
                Key.LeftShift => 0xA0, Key.RightShift => 0xA1, Key.LeftAlt => 0xA4, Key.RightAlt => 0xA5,
                Key.LeftCtrl => 0xA2, Key.RightCtrl => 0xA3, Key.LeftMeta => 0x5B, Key.RightMeta => 0x5C,
                Key.ContextMenu => 0x5D, Key.Escape => 0x1B, Key.LeftArrow => 0x25, Key.UpArrow => 0x26,
                Key.RightArrow => 0x27, Key.DownArrow => 0x28, Key.Backspace => 0x08, Key.PageDown => 0x22,
                Key.PageUp => 0x21, Key.Home => 0x24, Key.End => 0x23, Key.Insert => 0x2D, Key.Delete => 0x2E,
                Key.CapsLock => 0x14, Key.NumLock => 0x90, Key.PrintScreen => 0x2C, Key.ScrollLock => 0x91,
                Key.Pause => 0x13, Key.NumpadDivide => 0x6F, Key.NumpadMultiply => 0x6A,
                Key.NumpadPlus => 0x6B, Key.NumpadMinus => 0x6D, Key.NumpadPeriod => 0x6E,
                _ => 0
            };
        return vk != 0;
    }
}
