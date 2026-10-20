using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace DaveTheDiverCoop;

// The game keeps "which controls are in use" (keyboard+mouse or controller)
// in one slot on the input asset. Once player 2 joins, that slot ends up empty.
// The game then thinks nobody uses a mouse (player 1's mouse aim breaks) and
// helper items such as the booster crash when they read it.
// The mod's access to that slot through the normal wrappers fails, so read and
// write it directly, after checking the layout on a known-good value.
internal static class SchemeGuard
{
    private const int HasValueOffset = 0x108;
    private const int ValueOffset = 0x10C;
    private const int KeyboardMouse = 0;
    private const int GamePad = 1;

    private static bool _verified;
    private static bool _verifyFailed;
    private static int _lastGood = KeyboardMouse;
    private static int _logs;

    private static IntPtr AssetPointer(PlayerCharacter p1)
    {
        try
        {
            return p1?.inputAsset?.Pointer ?? IntPtr.Zero;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static bool Read(IntPtr asset, out bool hasValue, out int value)
    {
        hasValue = Marshal.ReadByte(asset, HasValueOffset) != 0;
        value = Marshal.ReadInt32(asset, ValueOffset);
        return true;
    }

    // Before player 2 exists: check the slot holds a sensible value and
    // agrees with what the game itself reports, so writes are safe later.
    internal static void Verify(PlayerCharacter p1)
    {
        if (_verified || _verifyFailed)
            return;
        var asset = AssetPointer(p1);
        if (asset == IntPtr.Zero)
            return;
        try
        {
            var raw = Marshal.ReadByte(asset, HasValueOffset);
            Read(asset, out var hasValue, out var value);
            var keyboard = p1.IsKeyboardMouse();
            var ok = raw <= 1 && hasValue && value >= 0 && value <= 8 && keyboard == (value == KeyboardMouse);
            if (ok)
            {
                _verified = true;
                _lastGood = value;
                Plugin.Logger.LogInfo($"Control mode slot verified (mode {value}, keyboard={keyboard})");
            }
            else if (_logs++ < 5)
            {
                Plugin.Logger.LogWarning($"Control mode slot not as expected yet: raw={raw} value={value} keyboard={keyboard}");
            }
        }
        catch (Exception exception)
        {
            _verifyFailed = true;
            Plugin.Logger.LogWarning($"Control mode slot check failed: {exception.Message}");
        }
    }

    // Every frame: if the slot went empty, refill it with the last real mode.
    internal static void Repair(PlayerCharacter p1)
    {
        if (!_verified)
            return;
        var asset = AssetPointer(p1);
        if (asset == IntPtr.Zero)
            return;
        Read(asset, out var hasValue, out var value);
        if (hasValue)
        {
            if (!_swapped)
                _lastGood = value;
            return;
        }
        Write(asset, _lastGood);
        if (_logs++ < 10)
            Plugin.Logger.LogInfo($"Control mode slot was empty; restored mode {_lastGood}");
    }

    private static void Write(IntPtr asset, int value)
    {
        Marshal.WriteInt32(asset, ValueOffset, value);
        Marshal.WriteByte(asset, HasValueOffset, 1);
    }

    private static bool _swapped;
    private static bool _savedHasValue;
    private static int _savedValue;

    // During player 2's turn the game should see a controller (stick aiming).
    internal static void BeginPlayer2(PlayerCharacter p1)
    {
        if (!_verified || _swapped)
            return;
        var asset = AssetPointer(p1);
        if (asset == IntPtr.Zero)
            return;
        Read(asset, out _savedHasValue, out _savedValue);
        Write(asset, GamePad);
        _swapped = true;
    }

    internal static void EndPlayer2(PlayerCharacter p1)
    {
        if (!_swapped)
            return;
        _swapped = false;
        var asset = AssetPointer(p1);
        if (asset == IntPtr.Zero)
            return;
        if (_savedHasValue)
            Write(asset, _savedValue);
        else
            Write(asset, _lastGood);
    }
}
