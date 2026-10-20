using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.InputSystem.Utilities;

namespace DaveTheDiverCoop;

// Sushi bar mini-games for player 2 on the keyboard. The mini-games read the
// game's own input, and player 1's keyboard is player 1's (it now drives player
// 1's Dave meanwhile, see SushiP1Drive). So while player 2 plays one without a
// controller, a virtual gamepad is added and fed from player 2's keys:
//   arrows = stick / d-pad, Enter = A, Backspace = B, Right Shift = X, U = Y,
//   Period = LT, Slash = RT, Comma = LB, I = RB.
// It is removed again as soon as the mini-game is over. Same raw-event approach
// as KeyboardFilter.
internal static class P2VirtualPad
{
    private const int HeaderSize = 24; // InputEvent (20) + state format (4)
    private const int StateSize = 28;  // GamepadState: buttons, 2 sticks, 2 triggers
    private static readonly int StateEvent = FourCC('S', 'T', 'A', 'T');
    private static readonly int PadFormat = FourCC('G', 'P', 'A', 'D');

    // GamepadState button bits (UnityEngine.InputSystem.LowLevel.GamepadButton).
    private const int DpadUp = 0, DpadDown = 1, DpadLeft = 2, DpadRight = 3;
    private const int North = 4, East = 5, South = 6, West = 7;
    private const int LeftShoulder = 10, RightShoulder = 11;

    private static InputDevice _pad;
    private static IntPtr _buffer;

    internal static bool Active => _pad != null;

    internal static void Begin(string why)
    {
        if (_pad != null)
            return;
        try
        {
            if (_buffer == IntPtr.Zero)
                _buffer = Marshal.AllocHGlobal(HeaderSize + 64);
            _pad = InputSystem.AddDevice("Gamepad", "Coop P2 Keyboard Pad", null);
            Plugin.Logger.LogInfo($"Player 2 keyboard pad added for {why} (device {_pad.deviceId}): " +
                                  "arrows = stick, Enter = A, Backspace = B, Right Shift = X, U = Y, Period = LT, Slash = RT");
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning($"Player 2 keyboard pad could not be added: {exception.Message}");
            _pad = null;
        }
    }

    internal static void End(string why)
    {
        if (_pad == null)
            return;
        try
        {
            Feed(true);
            if (_pad.added)
                InputSystem.RemoveDevice(_pad);
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning($"Player 2 keyboard pad removal: {exception.Message}");
        }
        _pad = null;
        Plugin.Logger.LogInfo($"Player 2 keyboard pad removed ({why})");
    }

    internal static void Tick()
    {
        if (_pad == null)
            return;
        if (!_pad.added)
        {
            _pad = null;
            return;
        }
        try
        {
            Feed(!KeyboardFilter.GameIsForeground);
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning($"Player 2 keyboard pad failed, removing it: {exception.Message}");
            End("error");
        }
    }

    private static unsafe void Feed(bool neutral)
    {
        uint buttons = 0;
        Vector2 stick = Vector2.zero;
        float lt = 0f, rt = 0f;
        if (!neutral)
        {
            void Bit(P2Keys.Action action, int bit)
            {
                if (P2Keys.Held(action))
                    buttons |= 1u << bit;
            }
            Bit(P2Keys.Action.Up, DpadUp);
            Bit(P2Keys.Action.Down, DpadDown);
            Bit(P2Keys.Action.Left, DpadLeft);
            Bit(P2Keys.Action.Right, DpadRight);
            Bit(P2Keys.Action.Ok, South);
            Bit(P2Keys.Action.Back, East);
            Bit(P2Keys.Action.Interact, West);
            Bit(P2Keys.Action.Item, North);
            Bit(P2Keys.Action.ShortDash, LeftShoulder);
            Bit(P2Keys.Action.SwitchItem, RightShoulder);
            stick = P2Keys.Move;
            lt = P2Keys.Held(P2Keys.Action.Dash) ? 1f : 0f;
            rt = P2Keys.Held(P2Keys.Action.Fire) ? 1f : 0f;
        }

        var buffer = (byte*)_buffer;
        var total = HeaderSize + StateSize;
        for (var i = 0; i < total; i++)
            buffer[i] = 0;
        var header = (InputEvent*)buffer;
        header->type = new FourCC(StateEvent);
        header->sizeInBytes = (uint)total;
        header->deviceId = _pad.deviceId;
        header->time = InputState.currentTime;
        *(int*)(buffer + 20) = PadFormat;
        var state = buffer + HeaderSize;
        *(uint*)(state + 0) = buttons;
        *(float*)(state + 4) = stick.x;
        *(float*)(state + 8) = stick.y;
        // right stick (12, 16) stays 0
        *(float*)(state + 20) = lt;
        *(float*)(state + 24) = rt;
        InputSystem.QueueEvent(new InputEventPtr(header));
    }

    private static int FourCC(char a, char b, char c, char d) => (a << 24) | (b << 16) | (c << 8) | d;
}
