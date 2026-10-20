using System;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace DaveTheDiverCoop;

// Player 2's own weapon/item HUD: a copy of the bottom-right "action switch"
// panel (knife, helper items, harpoon/gun), mirrored to the bottom-left and
// tinted purple. While the game is running player 2's code, anything it sends
// to the HUD goes to player 2's copy instead of player 1's.
internal static class P2Hud
{
    private static ActionSwitchUI _original;
    private static ActionSwitchUI _copy;
    private static readonly Color Tint = new(0.85f, 0.72f, 1f, 1f);

    internal static ActionSwitchUI Copy => _copy;

    internal static bool IsOriginal(ActionSwitchUI ui) =>
        ui != null && _original != null && _copy != null && ui.Pointer == _original.Pointer;

    internal static bool RouteToCopy(ActionSwitchUI ui) => IsOriginal(ui) && SecondDiver.InPlayer2Turn;

    internal static void Create(PlayerCharacter p2)
    {
        Destroy();
        try
        {
            var all = Object.FindObjectsOfType(Il2CppType.Of<ActionSwitchUI>(), true);
            for (var i = 0; i < all.Length; i++)
            {
                var ui = all[i]?.TryCast<ActionSwitchUI>();
                if (ui != null && ui.gameObject.activeInHierarchy)
                {
                    _original = ui;
                    break;
                }
            }
            if (_original == null)
            {
                Plugin.Logger.LogWarning("Player 2 HUD: the weapon/item HUD was not found");
                return;
            }
            var source = _original.gameObject;
            var copyObject = Object.Instantiate(source, source.transform.parent, false);
            copyObject.name = source.name + " (Player 2)";
            _copy = copyObject.GetComponent<ActionSwitchUI>();
            var originalRect = source.GetComponent<RectTransform>();
            var copyRect = copyObject.GetComponent<RectTransform>();
            // Player 2: top-right (copy still has the original bottom-right layout).
            Place(originalRect, copyRect, false, true);
            // Player 1: top-left.
            MovePlayerOnePanel(originalRect, originalRect);
            var images = copyObject.GetComponentsInChildren(Il2CppType.Of<Image>(), true);
            for (var i = 0; i < images.Length; i++)
            {
                var image = images[i]?.TryCast<Image>();
                if (image != null)
                    image.color *= Tint;
            }
            // The button hints show player 1's keys; hide them on the copy.
            var nodes = copyObject.GetComponentsInChildren(Il2CppType.Of<Transform>(), true);
            for (var i = 0; i < nodes.Length; i++)
            {
                var node = nodes[i]?.TryCast<Transform>();
                if (node != null && node.name == "Guide")
                    node.gameObject.SetActive(false);
            }
            SecondDiver.BeginPlayer2Turn(p2);
            try
            {
                _copy.Init(p2);
            }
            finally
            {
                SecondDiver.EndPlayer2Turn(p2);
            }
            Plugin.Logger.LogInfo($"Player 2 HUD created (copy of {source.name}, at {copyObject.GetComponent<RectTransform>()?.anchoredPosition})");
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning($"Player 2 HUD failed: {exception.Message}");
        }
    }

    // Flip a panel's corner: same distance from the opposite edge(s).
    private static void Place(RectTransform from, RectTransform to, bool flipX, bool flipY)
    {
        if (from == null || to == null)
            return;
        var aMin = from.anchorMin;
        var aMax = from.anchorMax;
        var pivot = from.pivot;
        var pos = from.anchoredPosition;
        var size = from.sizeDelta;
        if (flipX)
        {
            (aMin.x, aMax.x) = (1f - aMax.x, 1f - aMin.x);
            pivot.x = 1f - pivot.x;
            pos.x = -pos.x;
        }
        if (flipY)
        {
            (aMin.y, aMax.y) = (1f - aMax.y, 1f - aMin.y);
            pivot.y = 1f - pivot.y;
            pos.y = -pos.y;
        }
        to.anchorMin = aMin;
        to.anchorMax = aMax;
        to.pivot = pivot;
        to.anchoredPosition = pos;
        to.sizeDelta = size;
    }

    // Player 1's panel normally sits bottom-right; remember where, so it can
    // be put back when player 2 leaves.
    private static Vector2 _savedMin, _savedMax, _savedPivot, _savedPos;
    private static bool _moved;

    private static void MovePlayerOnePanel(RectTransform rect, RectTransform copyOfOriginalLayout)
    {
        if (rect == null)
            return;
        _savedMin = rect.anchorMin;
        _savedMax = rect.anchorMax;
        _savedPivot = rect.pivot;
        _savedPos = rect.anchoredPosition;
        _moved = true;
        Place(copyOfOriginalLayout, rect, true, true);
    }

    private static void RestorePlayerOnePanel()
    {
        if (!_moved || _original == null)
            return;
        _moved = false;
        var rect = _original.GetComponent<RectTransform>();
        if (rect == null)
            return;
        rect.anchorMin = _savedMin;
        rect.anchorMax = _savedMax;
        rect.pivot = _savedPivot;
        rect.anchoredPosition = _savedPos;
    }

    internal static void Destroy()
    {
        RestorePlayerOnePanel();
        if (_copy != null)
        {
            try
            {
                Object.Destroy(_copy.gameObject);
            }
            catch
            {
            }
        }
        _copy = null;
        _original = null;
    }

    private static int _logs;

    internal static void Note(string what)
    {
        if (_logs++ < 25)
            Plugin.Logger.LogInfo($"Player 2 HUD: {what} sent to player 2's panel");
    }
}

// --- Data for one player: during player 2's turn send it to player 2's panel.
[HarmonyPatch(typeof(ActionSwitchUI), nameof(ActionSwitchUI.SetBulletCount))]
internal static class P2HudBullets
{
    private static bool Prefix(ActionSwitchUI __instance, int __0)
    {
        if (P2Hud.RouteToCopy(__instance))
        {
            P2Hud.Copy.SetBulletCount(__0);
            return false;
        }
        if (!P2Hud.IsOriginal(__instance))
            return true;
        // Ammo updates can arrive outside player 2's turn (from the firing
        // animation). Tell them apart by matching each diver's current ammo.
        var p1Ammo = SecondDiver.Player1?.GetComponent<InstanceItemInventory>()?.gunHandler?.m_CurrentAmmo;
        var p2Ammo = SecondDiver.Player2?.GetComponent<InstanceItemInventory>()?.gunHandler?.m_CurrentAmmo;
        if (p2Ammo == __0)
            P2Hud.Copy.SetBulletCount(__0);
        return p1Ammo == __0 || p2Ammo != __0;
    }
}

[HarmonyPatch(typeof(ActionSwitchUI), nameof(ActionSwitchUI.SetMeleeData))]
internal static class P2HudMelee
{
    private static bool Prefix(ActionSwitchUI __instance, int __0)
    {
        if (!P2Hud.RouteToCopy(__instance)) return true;
        P2Hud.Note("knife");
        P2Hud.Copy.SetMeleeData(__0);
        return false;
    }
}

[HarmonyPatch(typeof(ActionSwitchUI), nameof(ActionSwitchUI.UpdateMeleeCount))]
internal static class P2HudMeleeCount
{
    private static bool Prefix(ActionSwitchUI __instance, int __0)
    {
        if (!P2Hud.RouteToCopy(__instance)) return true;
        P2Hud.Copy.UpdateMeleeCount(__0);
        return false;
    }
}

[HarmonyPatch(typeof(ActionSwitchUI), nameof(ActionSwitchUI.SetGunData))]
internal static class P2HudGun
{
    private static bool Prefix(ActionSwitchUI __instance, GunSpecData __0)
    {
        if (!P2Hud.RouteToCopy(__instance)) return true;
        P2Hud.Note("gun");
        P2Hud.Copy.SetGunData(__0);
        return false;
    }
}

[HarmonyPatch(typeof(ActionSwitchUI), nameof(ActionSwitchUI.SetHarpoonData))]
internal static class P2HudHarpoon
{
    private static bool Prefix(ActionSwitchUI __instance, DR.EquipmentItem __0)
    {
        if (!P2Hud.RouteToCopy(__instance)) return true;
        P2Hud.Note("harpoon");
        P2Hud.Copy.SetHarpoonData(__0);
        return false;
    }
}

[HarmonyPatch(typeof(ActionSwitchUI), nameof(ActionSwitchUI.SetSubHelperData))]
internal static class P2HudSubHelper
{
    private static bool Prefix(ActionSwitchUI __instance, DR.EquipmentItem __0, int __1, int __2, bool __3)
    {
        if (!P2Hud.RouteToCopy(__instance)) return true;
        P2Hud.Note("helper item");
        P2Hud.Copy.SetSubHelperData(__0, __1, __2, __3);
        return false;
    }
}

[HarmonyPatch(typeof(ActionSwitchUI), nameof(ActionSwitchUI.RefreshSubHelperAvailable))]
internal static class P2HudSubHelperAvailable
{
    private static bool Prefix(ActionSwitchUI __instance, DR.EquipmentItem __0, int __1, bool __2)
    {
        if (!P2Hud.RouteToCopy(__instance)) return true;
        P2Hud.Copy.RefreshSubHelperAvailable(__0, __1, __2);
        return false;
    }
}

[HarmonyPatch(typeof(ActionSwitchUI), nameof(ActionSwitchUI.SetSubHelperGuideActive))]
internal static class P2HudSubHelperGuide
{
    private static bool Prefix(ActionSwitchUI __instance, bool __0)
    {
        if (!P2Hud.RouteToCopy(__instance)) return true;
        P2Hud.Copy.SetSubHelperGuideActive(__0);
        return false;
    }
}

[HarmonyPatch(typeof(ActionSwitchUI), nameof(ActionSwitchUI.OnClickSubHelperSwapButton))]
internal static class P2HudSubHelperSwap
{
    private static bool Prefix(ActionSwitchUI __instance, ref bool __result)
    {
        if (!P2Hud.RouteToCopy(__instance)) return true;
        P2Hud.Note("item switch");
        __result = P2Hud.Copy.OnClickSubHelperSwapButton();
        return false;
    }
}

[HarmonyPatch(typeof(ActionSwitchUI), nameof(ActionSwitchUI.OnClickWeaponSwapButton))]
internal static class P2HudWeaponSwap
{
    private static bool Prefix(ActionSwitchUI __instance, Il2CppSystem.Action __0)
    {
        if (!P2Hud.RouteToCopy(__instance)) return true;
        P2Hud.Note("weapon switch");
        P2Hud.Copy.OnClickWeaponSwapButton(__0);
        return false;
    }
}

[HarmonyPatch(typeof(ActionSwitchUI), nameof(ActionSwitchUI.ResyncWeaponVisualToState))]
internal static class P2HudResync
{
    private static bool Prefix(ActionSwitchUI __instance)
    {
        if (!P2Hud.RouteToCopy(__instance)) return true;
        P2Hud.Copy.ResyncWeaponVisualToState();
        return false;
    }
}

// --- Showing/hiding the HUD: player 1's (the game's) calls apply to both
// panels; player 2's own calls only to player 2's panel.
[HarmonyPatch(typeof(ActionSwitchUI), nameof(ActionSwitchUI.ShowActionSwitch))]
internal static class P2HudShow
{
    private static bool Prefix(ActionSwitchUI __instance)
    {
        if (!P2Hud.IsOriginal(__instance)) return true;
        P2Hud.Copy.ShowActionSwitch();
        return !SecondDiver.InPlayer2Turn;
    }
}

[HarmonyPatch(typeof(ActionSwitchUI), nameof(ActionSwitchUI.HideActionSwitch))]
internal static class P2HudHide
{
    private static bool Prefix(ActionSwitchUI __instance)
    {
        if (!P2Hud.IsOriginal(__instance)) return true;
        P2Hud.Copy.HideActionSwitch();
        return !SecondDiver.InPlayer2Turn;
    }
}

[HarmonyPatch(typeof(ActionSwitchUI), nameof(ActionSwitchUI.ShowGroupAll))]
internal static class P2HudShowAll
{
    private static bool Prefix(ActionSwitchUI __instance, bool __0)
    {
        if (!P2Hud.IsOriginal(__instance)) return true;
        P2Hud.Copy.ShowGroupAll(__0);
        return !SecondDiver.InPlayer2Turn;
    }
}
