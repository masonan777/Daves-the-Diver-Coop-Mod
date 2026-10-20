using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.InputSystem.Utilities;
using UnityEngine.SceneManagement;

namespace DaveTheDiverCoop;

// While player 2 is in, player 1's keyboard is replaced by a virtual keyboard
// that mirrors every key except player 2's, so arrows/Enter/etc. only move
// player 2. Same approach as the tested split-keyboard code.
internal static class KeyboardFilter
{
    private const int StateEventHeaderSize = 24; // InputEvent (20) + state format (4)
    private static readonly int StateFormatEvent = MakeFourCC('S', 'T', 'A', 'T');
    private static readonly int KeyboardFormat = MakeFourCC('K', 'E', 'Y', 'S');

    private static bool _active;
    private static InputDevice _virtualKeyboard;
    private static readonly List<InputDevice> _disabled = new();
    private static IntPtr _buffer;
    private static int _stateSize;

    internal static bool Active => _active;

    // Sushi bar: while player 2 is in a mini-game, the mod drives player 1
    // (SushiP1Drive) and player 1's keys must not reach the game.
    internal static bool MuteP1;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    // Only pass keys while the game window is in front, so typing in other
    // programs never moves Dave.
    internal static bool GameIsForeground
    {
        get
        {
            try
            {
                GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
                return pid == (uint)Environment.ProcessId;
            }
            catch
            {
                return true;
            }
        }
    }

    internal static void SelfTest()
    {
        if (MakeFourCC('K', 'E', 'Y', 'S') != 0x4B455953 ||
            !P2Keys.TryVirtualKey(Key.UpArrow, out var up) || up != 0x26)
            throw new InvalidOperationException("Keyboard filter self-test failed");
    }

    internal static void Tick(bool joined)
    {
        var scene = SceneManager.GetActiveScene().name;
        var want = joined && scene != "DR_Title" && scene != "DR_Start" && scene != "DR_Logo";
        try
        {
            if (_active && (!want || _virtualKeyboard == null || !_virtualKeyboard.added))
                Deactivate();
            if (!_active && want)
                Activate();
            if (!_active)
                return;
            DisableRealKeyboards();
            Feed();
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogError($"Keyboard filter failed, turning it off: {exception}");
            Deactivate();
        }
    }

    private static void Activate()
    {
        _stateSize = KeyboardState.kSizeInBytes;
        if (_buffer == IntPtr.Zero)
            _buffer = Marshal.AllocHGlobal(StateEventHeaderSize + 64);
        _virtualKeyboard = InputSystem.AddDevice("Keyboard", "Coop P1 Keyboard", null);
        _active = true;
        DisableRealKeyboards();
        Plugin.Logger.LogInfo($"Keyboard filter on: player 2's keys no longer reach player 1 (device {_virtualKeyboard.deviceId})");
    }

    private static void Deactivate()
    {
        _active = false;
        try
        {
            foreach (var device in _disabled)
                if (device != null && device.added && !device.enabled)
                    InputSystem.EnableDevice(device);
            _disabled.Clear();
            if (_virtualKeyboard != null && _virtualKeyboard.added)
                InputSystem.RemoveDevice(_virtualKeyboard);
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning($"Keyboard filter off: {exception.Message}");
        }
        _virtualKeyboard = null;
        Plugin.Logger.LogInfo("Keyboard filter off");
    }

    private static void DisableRealKeyboards()
    {
        var devices = InputSystem.devices.ToArray();
        for (var i = 0; i < devices.Length; i++)
        {
            var device = devices[i];
            if (device == null || device.deviceId == _virtualKeyboard.deviceId || !device.enabled ||
                device.TryCast<Keyboard>() == null)
                continue;
            InputSystem.DisableDevice(device, false);
            if (!_disabled.Contains(device))
                _disabled.Add(device);
        }
    }

    private static unsafe void Feed()
    {
        var size = _stateSize;
        var state = stackalloc byte[size];
        for (var i = 0; i < size; i++)
            state[i] = 0;
        if (GameIsForeground && !MuteP1)
        {
            for (var key = 1; key < size * 8; key++)
            {
                if (!P2Keys.TryVirtualKey((Key)key, out var vk) || IsPlayer2Key(vk) || !P2Keys.IsDown(vk))
                    continue;
                state[key >> 3] |= (byte)(1 << (key & 7));
            }
        }

        var buffer = (byte*)_buffer;
        var total = StateEventHeaderSize + size;
        for (var i = 0; i < total; i++)
            buffer[i] = 0;
        var header = (InputEvent*)buffer;
        header->type = new FourCC(StateFormatEvent);
        header->sizeInBytes = (uint)total;
        header->deviceId = _virtualKeyboard.deviceId;
        header->time = InputState.currentTime;
        *(int*)(buffer + 20) = KeyboardFormat;
        Buffer.MemoryCopy(state, buffer + StateEventHeaderSize, size, size);
        InputSystem.QueueEvent(new InputEventPtr(header));
    }

    private static bool IsPlayer2Key(int vk)
    {
        foreach (var key in P2Keys.AllVirtualKeys)
            if (key == vk)
                return true;
        return false;
    }

    private static int MakeFourCC(char a, char b, char c, char d) => (a << 24) | (b << 16) | (c << 8) | d;
}
