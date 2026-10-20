using System;
using System.Runtime.InteropServices;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.InputSystem;

namespace DaveTheDiverCoop;

// Player 2's Xbox controller, read straight from Windows (XInput) so player 2
// does not need the keyboard. The same controller is switched off for the
// game's own input, so it never also moves player 1. Player 1 keeps the game's
// normal controls: keyboard/mouse or the other controller.
internal static class P2Pad
{
    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short LeftX, LeftY, RightX, RightY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint Packet;
        public XInputGamepad Pad;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint GetState(uint user, out XInputState state);

    private const ushort DpadUp = 0x0001, DpadDown = 0x0002, DpadLeft = 0x0004, DpadRight = 0x0008;
    private const ushort Start = 0x0010, View = 0x0020, LeftStick = 0x0040;
    private const ushort LB = 0x0100, RB = 0x0200, A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000;
    private const float Deadzone = 0.3f;

    private static ConfigEntry<string> _setting;
    private static ConfigEntry<bool> _p1Keyboard;
    private static int _slot = -1;
    private static XInputState _state;
    private static bool _connected;
    private static ushort _previousButtons;
    private static float _nextScan;
    private static bool _available = true;

    internal static bool Connected => _connected;
    internal static int Slot => _slot;

    internal static void Bind(ConfigFile config)
    {
        _setting = config.Bind("Player2", "Controller", "auto",
            "Player 2's Xbox controller: auto, off, or a controller number 1-4");
        _p1Keyboard = config.Bind("Player2", "Player1UsesKeyboard", true,
            "With only one controller plugged in: true = it belongs to player 2 (player 1 plays on keyboard/mouse), " +
            "false = it belongs to player 1");
    }

    private static bool Read(int slot, out XInputState state)
    {
        state = default;
        if (!_available)
            return false;
        try
        {
            return GetState((uint)slot, out state) == 0;
        }
        catch (Exception exception)
        {
            _available = false;
            Plugin.Logger.LogWarning($"Controller support unavailable: {exception.Message}");
            return false;
        }
    }

    // Which controller is player 2's.
    private static int ChooseSlot()
    {
        var setting = (_setting?.Value ?? "auto").Trim().ToLowerInvariant();
        if (setting == "off")
            return -1;
        if (int.TryParse(setting, out var number) && number >= 1 && number <= 4)
            return Read(number - 1, out _) ? number - 1 : -1;
        int first = -1, last = -1, count = 0;
        for (var i = 0; i < 4; i++)
        {
            if (!Read(i, out _))
                continue;
            count++;
            if (first < 0)
                first = i;
            last = i;
        }
        if (count >= 2)
            return last; // player 1 keeps the first controller
        if (count == 1 && _p1Keyboard.Value)
            return first;
        return -1;
    }

    private static readonly ushort[] _lastButtons = new ushort[4];
    private static readonly System.Collections.Generic.List<InputDevice> _hiddenPads = new();

    internal static void Tick()
    {
        var setting = (_setting?.Value ?? "auto").Trim().ToLowerInvariant();
        if (setting == "off")
        {
            Release("controller turned off");
            return;
        }
        if (_slot < 0)
            FindSlot(setting);
        _previousButtons = _connected ? _state.Pad.Buttons : (ushort)0;
        _connected = _slot >= 0 && Read(_slot, out _state);
        if (_slot >= 0 && !_connected)
        {
            Release($"controller {_slot + 1} disconnected");
            return;
        }
        if (_connected)
            HideMatchingPads();
    }

    // auto: player 2 claims a controller by pressing LB + RB together on it.
    // A fixed number in the config is used directly.
    private static void FindSlot(string setting)
    {
        if (int.TryParse(setting, out var number) && number >= 1 && number <= 4)
        {
            if (Time.realtimeSinceStartup < _nextScan)
                return;
            _nextScan = Time.realtimeSinceStartup + 2f;
            if (Read(number - 1, out _))
                Claim(number - 1, "set in config");
            return;
        }
        for (var i = 0; i < 4; i++)
        {
            if (!Read(i, out var state))
            {
                _lastButtons[i] = 0;
                continue;
            }
            var both = (ushort)(LB | RB);
            var now = (state.Pad.Buttons & both) == both;
            var before = (_lastButtons[i] & both) == both;
            _lastButtons[i] = state.Pad.Buttons;
            if (now && !before)
            {
                Claim(i, "LB + RB pressed");
                return;
            }
        }
    }

    private static float _claimedAt;

    private static void Claim(int slot, string why)
    {
        _slot = slot;
        _claimedAt = Time.realtimeSinceStartup;
        _previousButtons = 0xFFFF; // ignore buttons already held while claiming
        Plugin.Logger.LogInfo($"Player 2 controller: number {slot + 1} ({why})");
        LogDevices("claimed");
        if (!P2Keys.Joined)
            P2Keys.JoinFromController();
    }

    // Restaurant mini-games read "the" controller directly: while player 2
    // plays one, hand player 2's (hidden) controller to the game, then hide
    // it again.
    private static bool _lent;
    internal static bool Lent => _lent;

    internal static void Lend(bool lend, string why)
    {
        if (lend == _lent || _hiddenPads.Count == 0)
            return;
        _lent = lend;
        foreach (var pad in _hiddenPads)
        {
            try
            {
                if (lend)
                    InputSystem.EnableDevice(pad);
                else
                    InputSystem.DisableDevice(pad, false);
            }
            catch
            {
            }
        }
        Plugin.Logger.LogInfo($"Player 2 controller {(lend ? "lent to the game" : "taken back")}: {why}");
    }

    private static void Release(string why)
    {
        _lent = false;
        if (_slot >= 0)
            Plugin.Logger.LogInfo($"Player 2 controller released: {why}");
        _slot = -1;
        _connected = false;
        _state = default;
        foreach (var pad in _hiddenPads)
        {
            try
            {
                InputSystem.EnableDevice(pad);
            }
            catch
            {
            }
        }
        if (_hiddenPads.Count > 0)
            Plugin.Logger.LogInfo($"Gave {_hiddenPads.Count} controller(s) back to player 1");
        _hiddenPads.Clear();
    }

    // Windows can show one physical controller to the game more than once
    // (Steam Input adds a virtual copy). Whenever player 2 presses face or
    // shoulder buttons, switch off every game controller showing exactly
    // those buttons, so player 2's presses never reach player 1.
    private const ushort Watched = A | B | X | Y | LB | RB;

    private static float _nextDeviceLog;

    // Diagnostics: every device the game sees, and which ones currently show
    // player 2's stick movement.
    private static void LogDevices(string why)
    {
        try
        {
            var stick = LeftStickValue;
            var text = new System.Text.StringBuilder($"Controllers ({why}); player 2 = XInput {_slot + 1}, stick {stick}:");
            var all = InputSystem.devices;
            for (var i = 0; i < all.Count; i++)
            {
                var device = all[i];
                if (device == null)
                    continue;
                var pad = device.TryCast<Gamepad>();
                var extra = pad != null ? $" leftStick={pad.leftStick.ReadValue()}" : "";
                var caps = device.description.capabilities ?? "";
                var index = caps.IndexOf("\"userIndex\":", StringComparison.Ordinal);
                var user = index >= 0 ? caps.Substring(index + 12, 1) : "-";
                text.Append($"\n   #{device.deviceId} {device.displayName} [{device.layout}] enabled={device.enabled} userIndex={user}{extra}");
            }
            Plugin.Logger.LogInfo(text.ToString());
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning($"Controller list failed: {exception.Message}");
        }
    }

    private static void HideMatchingPads()
    {
        var stick = LeftStickValue;
        if (stick.magnitude > 0.6f)
        {
            if (Time.realtimeSinceStartup >= _nextDeviceLog)
            {
                _nextDeviceLog = Time.realtimeSinceStartup + 20f;
                LogDevices("player 2 moving");
            }
        }
        // Match only until player 2's controller has been found (plus a short
        // window right after claiming, for duplicate copies of it). Matching
        // later risked catching player 1 pressing the same buttons.
        if (_hiddenPads.Count > 0 && Time.realtimeSinceStartup > _claimedAt + 1.5f)
            return;
        var pressed = (ushort)(_state.Pad.Buttons & Watched);
        if (pressed == 0)
            return;
        try
        {
            var pads = Gamepad.all;
            for (var i = 0; i < pads.Count; i++)
            {
                var pad = pads[i];
                if (pad == null || !pad.enabled)
                    continue;
                ushort shown = 0;
                if (pad.buttonSouth.isPressed) shown |= A;
                if (pad.buttonEast.isPressed) shown |= B;
                if (pad.buttonWest.isPressed) shown |= X;
                if (pad.buttonNorth.isPressed) shown |= Y;
                if (pad.leftShoulder.isPressed) shown |= LB;
                if (pad.rightShoulder.isPressed) shown |= RB;
                if (shown != pressed)
                    continue;
                InputSystem.DisableDevice(pad, false);
                _hiddenPads.Add(pad);
                Plugin.Logger.LogInfo($"Player 2's controller hidden from player 1: {pad.displayName} ({pad.description.capabilities})");
            }
        }
        catch (Exception exception)
        {
            if (_hideLogs++ < 3)
                Plugin.Logger.LogWarning($"Could not hide player 2's controller from player 1: {exception.Message}");
        }
    }

    private static int _hideLogs;

    // Also switch off any game controller whose left stick is pushed the same
    // way as player 2's right now.
    private static void HideByStick(Vector2 stick)
    {
        try
        {
            var pads = Gamepad.all;
            for (var i = 0; i < pads.Count; i++)
            {
                var pad = pads[i];
                if (pad == null || !pad.enabled)
                    continue;
                // Same physical stick read twice gives (almost) identical
                // numbers; another player's stick practically never does.
                var value = pad.leftStick.ReadValue();
                var raw = new Vector2(_state.Pad.LeftX / 32767f, _state.Pad.LeftY / 32767f);
                if (value.magnitude < 0.5f || (value.normalized - raw.normalized).magnitude > 0.03f)
                    continue;
                InputSystem.DisableDevice(pad, false);
                _hiddenPads.Add(pad);
                Plugin.Logger.LogInfo($"Player 2's controller hidden from player 1 (matched stick): {pad.displayName} #{pad.deviceId}");
            }
        }
        catch (Exception exception)
        {
            if (_hideLogs++ < 3)
                Plugin.Logger.LogWarning($"Stick match failed: {exception.Message}");
        }
    }

    internal static void ShowToGame() => Release("player 2 left");

    private static bool Button(ushort mask) => _connected && (_state.Pad.Buttons & mask) != 0;

    internal static bool StartPressed => _connected && (_state.Pad.Buttons & Start) != 0 && (_previousButtons & Start) == 0;

    internal static bool Down(P2Keys.Action action)
    {
        if (!_connected)
            return false;
        return action switch
        {
            P2Keys.Action.Up => Button(DpadUp),
            P2Keys.Action.Down => Button(DpadDown),
            P2Keys.Action.Left => Button(DpadLeft),
            P2Keys.Action.Right => Button(DpadRight),
            P2Keys.Action.Interact => Button(X),
            P2Keys.Action.Ok => Button(A),
            P2Keys.Action.Back => Button(B),
            P2Keys.Action.Fire => _state.Pad.RightTrigger > 60,
            P2Keys.Action.Dash => _state.Pad.LeftTrigger > 60,
            P2Keys.Action.ShortDash => Button(LeftStick),
            P2Keys.Action.Item => Button(Y),
            P2Keys.Action.SwitchItem => Button(LB),
            P2Keys.Action.SwitchWeapon => Button(RB),
            _ => false,
        };
    }

    private static Vector2 Stick(short x, short y)
    {
        var v = new Vector2(x / 32767f, y / 32767f);
        var m = v.magnitude;
        if (m < Deadzone)
            return Vector2.zero;
        // Rescale so movement starts gently just outside the dead zone.
        return v / m * Mathf.Clamp01((m - Deadzone) / (1f - Deadzone));
    }

    internal static Vector2 LeftStickValue => _connected ? Stick(_state.Pad.LeftX, _state.Pad.LeftY) : Vector2.zero;
    internal static Vector2 RightStickValue => _connected ? Stick(_state.Pad.RightX, _state.Pad.RightY) : Vector2.zero;
}
