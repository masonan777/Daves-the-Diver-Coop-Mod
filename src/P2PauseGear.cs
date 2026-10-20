using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace DaveTheDiverCoop;

// Pause menu: the Equipment box can show either diver's gear. Press RB (any
// controller) or E to swap between player 1 and player 2. Player 2's view is
// tinted purple and titled "Player 2".
internal static class P2PauseGear
{
    private static readonly Color Tint = new(0.85f, 0.72f, 1f, 1f);

    private static PausePlayInfoPanel _panel;
    private static Transform _equip;
    private static TextMeshProUGUI _label;
    private static string _labelText;
    private static readonly Dictionary<IntPtr, Color> _colors = new();
    private static bool _wasOpen, _showingP2, _eWasDown;
    internal static bool IsOpen => _wasOpen;
    private static float _nextSearch;
    private static int _hintAtFrame = -1;

    internal static void Tick()
    {
        try
        {
            var p2 = SecondDiver.Player2;
            if (_panel == null && Time.realtimeSinceStartup >= _nextSearch)
            {
                _nextSearch = Time.realtimeSinceStartup + 1f;
                _panel = Object.FindFirstObjectByType<PausePlayInfoPanel>(FindObjectsInactive.Include);
            }
            var open = _panel != null && _panel.gameObject.activeInHierarchy && p2 != null;
            if (open && !_wasOpen)
            {
                _equip = FindChild(_panel.transform, "EquipGroup");
                _label = FindChild(_equip, "EquipLabel")?.GetComponent<TextMeshProUGUI>();
                _showingP2 = false;
                _hintAtFrame = Time.frameCount + 2; // after the game fills in its own title
            }
            if (!open && _wasOpen && _showingP2)
                Show(SecondDiver.Player1, false);
            _wasOpen = open;
            if (!open)
                return;
            if (_hintAtFrame > 0 && Time.frameCount >= _hintAtFrame)
            {
                _hintAtFrame = -1;
                if (_label != null)
                {
                    _labelText = _label.text;
                    _label.text = "Player 1  <size=70%>(RB / E: Player 2)</size>";
                }
            }
            if (SwapPressed())
                Show(_showingP2 ? SecondDiver.Player1 : p2, !_showingP2);
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning($"Pause menu gear swap: {exception.Message}");
            _wasOpen = true;
        }
    }

    private static Image _preview;

    // After the menu's animation has picked this frame's picture: show the
    // purple-flippered Dave while player 2's gear is up.
    internal static void LateTick()
    {
        if (!_wasOpen || !_showingP2 || !P2Skin.Enabled || _equip == null)
            return;
        try
        {
            if (_preview == null)
                _preview = FindChild(_equip, "PreviewImage")?.GetComponent<Image>();
            var sprite = _preview?.sprite;
            if (sprite == null)
                return;
            var swapped = P2Skin.RecoloredForMenu(sprite);
            if (_previewLogs < 1 && swapped != null && swapped.Pointer != sprite.Pointer)
            {
                _previewLogs++;
                Plugin.Logger.LogInfo($"Pause menu picture: {sprite.name} packed={sprite.packed} rect={sprite.rect} trimmed={sprite.textureRect} -> {swapped.rect}; image size {_preview.rectTransform.rect.size} preserveAspect={_preview.preserveAspect} type={_preview.type}");
            }
            if (swapped != null && swapped.Pointer != sprite.Pointer)
                _preview.sprite = swapped;
        }
        catch (Exception exception)
        {
            if (_previewLogs++ < 3)
                Plugin.Logger.LogWarning($"Pause menu player 2 picture: {exception.Message}");
        }
    }

    private static int _previewLogs;

    private static bool SwapPressed()
    {
        var pressed = false;
        var e = P2Keys.IsDown(0x45);
        if (e && !_eWasDown)
            pressed = true;
        _eWasDown = e;
        if (P2Keys.Pressed(P2Keys.Action.SwitchWeapon) && P2Pad.Connected)
            pressed = true; // player 2's controller (hidden from the game)
        try
        {
            var pads = Gamepad.all;
            for (var i = 0; i < pads.Count; i++)
                if (pads[i] != null && pads[i].enabled && pads[i].rightShoulder.wasPressedThisFrame)
                    pressed = true;
        }
        catch
        {
        }
        return pressed;
    }

    private static void Show(PlayerCharacter who, bool player2)
    {
        if (_equip == null || who == null)
            return;
        // The game's own fill: weapons from "the player", charms from the
        // save's charm slots (so player 2's charm slots go in for player 2).
        System.Action fill = () =>
        {
            if (player2)
                SecondDiver.BeginPlayer2Turn(who);
            try
            {
                _panel.InitEquipElements();
            }
            finally
            {
                if (player2)
                    SecondDiver.EndPlayer2Turn(who);
            }
        };
        try
        {
            if (player2)
                P2Loadout.WithPlayer2Slots("Pause menu", fill);
            else
                fill();
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning($"Pause menu gear fill: {exception.Message}");
        }
        if (_label != null && _labelText != null)
            _label.text = player2
                ? "Player 2  <size=70%>(RB / E: Player 1)</size>"
                : "Player 1  <size=70%>(RB / E: Player 2)</size>";
        _showingP2 = player2;
        Plugin.Logger.LogInfo($"Pause menu: showing player {(player2 ? 2 : 1)}'s gear");
    }

    private static void SetTint(bool on)
    {
        var images = _equip.GetComponentsInChildren(Il2CppType.Of<Image>(), true);
        for (var i = 0; i < images.Length; i++)
        {
            var image = images[i]?.TryCast<Image>();
            if (image == null)
                continue;
            if (!_colors.TryGetValue(image.Pointer, out var original))
            {
                original = image.color;
                _colors[image.Pointer] = original;
            }
            image.color = on ? original * Tint : original;
        }
    }

    private static Transform FindChild(Transform root, string name)
    {
        if (root == null)
            return null;
        if (root.name == name)
            return root;
        for (var i = 0; i < root.childCount; i++)
        {
            var found = FindChild(root.GetChild(i), name);
            if (found != null)
                return found;
        }
        return null;
    }
}
