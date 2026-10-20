using System;
using BepInEx.Configuration;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace DaveTheDiverCoop;

// Player 2's own oxygen tank and oxygen HUD. The game's oxygen panel (top-left)
// is copied for player 2, put in the bottom-right and tinted purple, and player
// 2's breathing is pointed at it. If either diver runs out, the dive ends the
// normal way (player 1's tank is emptied).
internal static class P2Oxygen
{
    // The old shared-tank mode needed hooks that crashed the game on damage;
    // each diver now always has their own tank.
    internal static bool SharedTank => false;

    private static GameObject _panel;
    private static bool _ending;
    private static float _nextLog;
    private static float _refreshAt = -1f;
    private static GameObject _source;
    private static readonly Color Tint = new(0.85f, 0.72f, 1f, 1f);


    internal static void Setup(PlayerCharacter p1, PlayerCharacter p2)
    {
        Destroy();
        _ending = false;
        if (SharedTank)
            return;
        var h1 = p1?.BreathHandler;
        var h2 = p2?.BreathHandler;
        if (h1 == null || h2 == null)
        {
            Plugin.Logger.LogWarning("Player 2 oxygen: breath handler missing");
            return;
        }
        // Hook player 2's breathing up to player 2 (it was copied unconnected).
        SecondDiver.BeginPlayer2Turn(p2);
        try
        {
            Try("breath init", () => h2.Init(p2));
        }
        finally
        {
            SecondDiver.EndPlayer2Turn(p2);
        }
        Try("copy tank settings", () =>
        {
            h2.maxHPParamDic ??= h1.maxHPParamDic;
            h2.breathTimePramDic ??= h1.breathTimePramDic;
            h2.MaxHPBuffParameter = h1.MaxHPBuffParameter;
            h2.BreathTimeParameter = h1.BreathTimeParameter;
            h2.m_MaxHPParameter = h1.m_MaxHPParameter;
            h2.m_CustomMaxHP = h1.m_CustomMaxHP;
            if (h2.m_InGameWarningPanel == null)
                h2.m_InGameWarningPanel = h1.m_InGameWarningPanel;
            // Player 2 never runs the game-over itself; the mod ends the dive.
            h2.Cheat_NoDie = true;
        });
        Try("copy breathing state", () => CopySimpleState(h1, h2));
        Try("oxygen HUD", () =>
        {
            var source = h1.PlayerStateInfoUI;
            if (source == null)
                throw new InvalidOperationException("player 1 has no oxygen panel");
            _panel = Object.Instantiate(source.gameObject, source.transform.parent, false);
            _panel.name = source.gameObject.name + " (Player 2)";
            _source = source.gameObject;
            CloneRepair.Fill(_source, _panel, "Player 2 oxygen HUD");
            MirrorAcrossScreen(source.GetComponent<RectTransform>(), _panel.GetComponent<RectTransform>());
            var images = _panel.GetComponentsInChildren(Il2CppType.Of<Image>(), true);
            for (var i = 0; i < images.Length; i++)
            {
                var image = images[i]?.TryCast<Image>();
                if (image != null)
                    image.color *= Tint;
            }
            SharedWeight.Attach(_panel);
            var ui = _panel.GetComponent<PlayerStateInfoPanelUI>();
            h2.SetInfoUI(ui);
        });
        Try("fill tank", () => h2.HP = h1.HP);
        _refreshAt = Time.realtimeSinceStartup + 0.5f;
        Plugin.Logger.LogInfo(
            $"Player 2 oxygen: own tank {h2.HP:F0}/{h2.MaxHP:F0} (player 1 {h1.HP:F0}/{h1.MaxHP:F0}), panel={(_panel != null)}, enabled={h2.enabled}");
    }

    private static readonly string[] Skip = { "HP", "m_HP", "Cheat_NoDie", "_Cheat_NoDie_k__BackingField", "enabled", "isActiveAndEnabled", "useGUILayout", "runInEditMode", "hideFlags" };

    // Player 2's breathing was copied mid-dive without its running state
    // (whether oxygen drains, warning levels...). Copy those simple values
    // from player 1, leaving the oxygen amount itself alone.
    private static void CopySimpleState(PlayerBreathHandler from, PlayerBreathHandler to)
    {
        var copied = new System.Collections.Generic.List<string>();
        foreach (var property in typeof(PlayerBreathHandler).GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
        {
            if (property.DeclaringType != typeof(PlayerBreathHandler) || !property.CanRead || !property.CanWrite)
                continue;
            if (property.PropertyType != typeof(bool) && property.PropertyType != typeof(float))
                continue;
            if (Array.IndexOf(Skip, property.Name) >= 0)
                continue;
            try
            {
                var a = property.GetValue(from);
                var b = property.GetValue(to);
                if (Equals(a, b))
                    continue;
                property.SetValue(to, a);
                copied.Add($"{property.Name}={a}");
            }
            catch
            {
            }
        }
        Plugin.Logger.LogInfo($"Player 2 oxygen: copied breathing state: {string.Join(", ", copied)}");
    }

    // Mirror each piece left-to-right across the screen: player 1's gauge is
    // bottom-left, so player 2's lands bottom-right at the same height.
    private static void MirrorAcrossScreen(RectTransform from, RectTransform to)
    {
        if (from == null || to == null)
            return;
        var canvas = from.GetComponentInParent<Canvas>()?.rootCanvas;
        var canvasRect = canvas?.GetComponent<RectTransform>();
        if (canvasRect == null)
            return;
        var corners = new Vector3[4];
        canvasRect.GetWorldCorners(corners);
        var left = corners[0].x;
        var right = corners[2].x;
        for (var i = 0; i < to.childCount && i < from.childCount; i++)
        {
            var child = to.GetChild(i)?.TryCast<RectTransform>();
            var original = from.GetChild(i)?.TryCast<RectTransform>();
            if (child == null || original == null)
                continue;
            child.pivot = new Vector2(1f - original.pivot.x, original.pivot.y);
            var p = original.position;
            child.position = new Vector3(left + right - p.x, p.y, p.z);
            Plugin.Logger.LogInfo($"   placed {child.name}: x {p.x:F2} -> {child.position.x:F2} (screen {left:F2}..{right:F2})");
        }
    }

    // The oxygen panel's root may cover the whole screen with the gauge
    // anchored top-left inside it; then flip each corner-anchored child.
    private static void PlaceBottomRight(RectTransform from, RectTransform to)
    {
        if (from == null || to == null)
            return;
        var stretched = from.anchorMin != from.anchorMax;
        Plugin.Logger.LogInfo($"Player 2 oxygen HUD: panel anchors {from.anchorMin}-{from.anchorMax} pos={from.anchoredPosition} stretched={stretched}");
        if (!stretched)
        {
            Flip(from, to);
            return;
        }
        for (var i = 0; i < to.childCount; i++)
        {
            var child = to.GetChild(i)?.TryCast<RectTransform>();
            var original = i < from.childCount ? from.GetChild(i)?.TryCast<RectTransform>() : null;
            if (child == null || original == null)
                continue;
            Flip(original, child);
            Plugin.Logger.LogInfo($"   moved {child.name}: anchors {original.anchorMin}-{original.anchorMax} -> {child.anchorMin}-{child.anchorMax} pos {original.anchoredPosition} -> {child.anchoredPosition}");
        }
    }

    // The original sits top-left; player 2's goes bottom-right.
    private static void Flip(RectTransform from, RectTransform to)
    {
        if (from == null || to == null)
            return;
        to.anchorMin = new Vector2(1f - from.anchorMax.x, 1f - from.anchorMax.y);
        to.anchorMax = new Vector2(1f - from.anchorMin.x, 1f - from.anchorMin.y);
        to.pivot = new Vector2(1f - from.pivot.x, 1f - from.pivot.y);
        to.anchoredPosition = -from.anchoredPosition;
        to.sizeDelta = from.sizeDelta;
    }

    internal static void Tick(PlayerCharacter p1, PlayerCharacter p2)
    {
        if (SharedTank || p1 == null || p2 == null)
            return;
        try
        {
            var h1 = p1.BreathHandler;
            var h2 = p2.BreathHandler;
            if (h1 == null || h2 == null)
                return;
            if (_refreshAt > 0f && Time.realtimeSinceStartup >= _refreshAt)
            {
                _refreshAt = -1f;
                if (_source != null && _panel != null)
                    CloneRepair.Fill(_source, _panel, "Player 2 oxygen HUD (after start-up)");
                try
                {
                    h2.UpdateUIInfo(h2.HP);
                }
                catch (Exception exception)
                {
                    Plugin.Logger.LogWarning($"Player 2 oxygen HUD refresh failed: {exception.Message}");
                }
            }
            if (Time.realtimeSinceStartup >= _nextLog)
            {
                _nextLog = Time.realtimeSinceStartup + 10f;
                Plugin.Logger.LogInfo($"Oxygen: P1 {h1.HP:F0}/{h1.MaxHP:F0}  P2 {h2.HP:F0}/{h2.MaxHP:F0}");
            }
            if (!_ending && h2.HP <= 0f && h1.HP > 0f)
            {
                _ending = true;
                Plugin.Logger.LogInfo("Player 2 ran out of oxygen; ending the dive");
                h1.SetHitHP(h1.HP + 10f, new Il2CppSystem.Nullable<Color>());
            }
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning($"Player 2 oxygen: {exception.Message}");
        }
    }

    private static float _lastGauge = -1f;
    private static int _gaugeLogs;

    // Player 2's short-dash gauge on his panel: empty right after a dash,
    // refilling as the dash cooldown runs, full when ready.
    private static void UpdateShortDashGauge(PlayerCharacter p2, PlayerBreathHandler h2)
    {
        var ui = h2.PlayerStateInfoUI;
        var cooldown = p2.GetComponent<ShortDashAbility>()?.dashCooldown;
        if (ui == null || cooldown == null || _panel == null || ui.gameObject.Pointer != _panel.Pointer)
            return;
        var duration = cooldown.Duration;
        var fill = cooldown.IsActive && duration > 0f ? Mathf.Clamp01(cooldown.Timer / duration) : 1f;
        if (Mathf.Abs(fill - _lastGauge) < 0.01f)
            return;
        if (cooldown.IsActive && _gaugeLogs++ < 3)
            Plugin.Logger.LogInfo($"P2 short-dash gauge: timer={cooldown.Timer:F2}/{duration:F2} -> fill {fill:F2}");
        _lastGauge = fill;
        ui.SetShortDashSliderValue(fill);
    }

    internal static GameObject Panel => _panel;

    internal static void Destroy()
    {
        _lastGauge = -1f;
        SharedWeight.Detach();
        if (_panel != null)
        {
            try
            {
                Object.Destroy(_panel);
            }
            catch
            {
            }
        }
        _panel = null;
    }

    private static void Try(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning($"Player 2 oxygen ({what}) failed: {exception.Message}");
        }
    }
}
