using System;
using BepInEx.Configuration;
using CodeStage.AntiCheat.ObscuredTypes;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace DaveTheDiverCoop;

// Player 2's own starting loadout.
//
// The game keeps one loadout in the save (EquipSlotSave). It is read when a
// diver equips (InstanceItemInventory.EquipSavedSlotData) and written by the
// boat's equipment page. Player 2's loadout lives in our config; while the
// boat page shows player 2 (RB / E), and while player 2 equips on a dive, the
// save temporarily holds player 2's slots and then gets player 1's back.
internal static class P2Loadout
{
    private sealed class Slots
    {
        internal readonly int[] Ids = new int[9];
        internal string GunGuid = string.Empty;

        public override string ToString() => string.Join(",", Ids) + "|" + GunGuid;

        internal static Slots Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;
            var halves = text.Split('|');
            var parts = halves[0].Split(',');
            if (parts.Length != 9)
                return null;
            var slots = new Slots();
            for (var i = 0; i < 9; i++)
                if (!int.TryParse(parts[i], out slots.Ids[i]))
                    return null;
            slots.GunGuid = halves.Length > 1 ? halves[1] : string.Empty;
            return slots;
        }
    }

    private static ConfigEntry<string> _saved;
    private static LobbyDiverBoxPage _page;
    private static Slots _p1WhileEditing;
    private static bool _editingP2, _eWasDown;

    internal static bool EditingPlayer2 => _editingP2;

    internal static void Bind(ConfigFile config) =>
        _saved = config.Bind("Player2", "Loadout", string.Empty,
            "Player 2's starting gear, chosen on the boat's equipment page with RB / E. Empty = same as player 1.");

    private static Slots P2Slots => Slots.Parse(_saved?.Value);

    private static EquipSlotSave Save()
    {
        try
        {
            return DR.Save.SaveSystem.GetGameSave()?.EquipSlotSave;
        }
        catch
        {
            return null;
        }
    }

    private static Slots Read(EquipSlotSave save)
    {
        var s = new Slots();
        s.Ids[0] = save.MeleeSlotItemID.GetDecrypted();
        s.Ids[1] = save.GunSlotItemID.GetDecrypted();
        s.Ids[2] = save.HarpoonSlotItemID.GetDecrypted();
        s.Ids[3] = save.HarpoonHeadSlotItemID.GetDecrypted();
        s.Ids[4] = save.Charm1SlotItemID.GetDecrypted();
        s.Ids[5] = save.Charm2SlotItemID.GetDecrypted();
        s.Ids[6] = save.Charm3SlotItemID.GetDecrypted();
        s.Ids[7] = save.SubHelper1SlotItemID.GetDecrypted();
        s.Ids[8] = save.SubHelper2SlotItemID.GetDecrypted();
        s.GunGuid = save.GunSlotGUID?.GetDecrypted() ?? string.Empty;
        return s;
    }

    private static void Write(EquipSlotSave save, Slots s)
    {
        save.MeleeSlotItemID = s.Ids[0];
        save.GunSlotItemID = s.Ids[1];
        save.HarpoonSlotItemID = s.Ids[2];
        save.HarpoonHeadSlotItemID = s.Ids[3];
        save.Charm1SlotItemID = s.Ids[4];
        save.Charm2SlotItemID = s.Ids[5];
        save.Charm3SlotItemID = s.Ids[6];
        save.SubHelper1SlotItemID = s.Ids[7];
        save.SubHelper2SlotItemID = s.Ids[8];
        save.GunSlotGUID = new ObscuredString(s.GunGuid ?? string.Empty);
    }

    // Player 2's saved loadout can name items this save doesn't have (it was
    // chosen in another save, or the item was sold). Any such slot falls back
    // to player 1's item for that slot.
    private static Slots Validated(Slots mine, Slots p1)
    {
        SaveData data = null;
        try { data = DR.Save.SaveSystem.GetGameSave(); } catch { }
        if (data == null || p1 == null)
            return mine;
        var fixedUp = new Slots();
        var changed = new System.Collections.Generic.List<string>();
        for (var i = 0; i < 9; i++)
        {
            var id = mine.Ids[i];
            var ok = id == 0 || id == p1.Ids[i];
            if (!ok)
            {
                try { ok = data.HasInventoryItemSaveData(id); } catch { ok = false; }
            }
            fixedUp.Ids[i] = ok ? id : p1.Ids[i];
            if (!ok)
                changed.Add($"slot {i}: {id} -> {p1.Ids[i]}");
        }
        fixedUp.GunGuid = mine.GunGuid;
        if (!string.IsNullOrEmpty(mine.GunGuid) && mine.GunGuid != p1.GunGuid)
        {
            var have = false;
            try { have = data.GetInventoryItemData(mine.GunGuid) != null; } catch { }
            if (!have)
            {
                fixedUp.Ids[1] = p1.Ids[1];
                fixedUp.GunGuid = p1.GunGuid;
                changed.Add("gun -> player 1's");
            }
        }
        if (changed.Count > 0)
        {
            Plugin.Logger.LogInfo($"Player 2 loadout: items this save doesn't have were swapped for player 1's ({string.Join(", ", changed)})");
            if (_saved != null)
                _saved.Value = fixedUp.ToString();
        }
        return fixedUp;
    }

    // Dive: run player 2's equip with player 2's slots in the save.
    internal static void WithPlayer2Slots(string what, System.Action equip)
    {
        var mine = P2Slots;
        var save = mine == null ? null : Save();
        if (save == null)
        {
            equip();
            return;
        }
        Slots p1 = null;
        try
        {
            p1 = Read(save);
            mine = Validated(mine, p1);
            Write(save, mine);
            Plugin.Logger.LogInfo($"{what}: using player 2's loadout {mine}");
            equip();
        }
        finally
        {
            if (p1 != null)
                try { Write(save, p1); }
                catch (Exception exception) { Plugin.Logger.LogError($"Could not put player 1's loadout back: {exception}"); }
        }
    }

    // ---- Boat equipment page ----

    internal static void Opened(LobbyDiverBoxPage page)
    {
        _page = page;
        _label = FindLabel(page);
        Plugin.Logger.LogInfo($"Boat equipment page opened (RB / E: player 2's loadout); title={(_label != null ? _label.name : "not found")}");
    }

    internal static void Closing()
    {
        if (_editingP2)
            StopEditing("page closed");
        RestorePreview();
        if (_label != null && _labelText != null)
            try { _label.text = _labelText; } catch { }
        _label = null;
        _labelText = null;
        _page = null;
    }

    internal static void Tick()
    {
        try
        {
            if (_page == null)
                return;
            if (!_page.gameObject.activeInHierarchy)
            {
                Closing();
                return;
            }
            if (SwapPressed())
            {
                if (_editingP2)
                    StopEditing("swap");
                else
                    StartEditing();
                Refresh();
                if (!_editingP2)
                    RestorePreview();
            }
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning($"Boat loadout: {exception.Message}");
            _page = null;
        }
    }

    private static void StartEditing()
    {
        var save = Save();
        if (save == null)
        {
            Plugin.Logger.LogWarning("Boat loadout: no save data");
            return;
        }
        _p1WhileEditing = Read(save);
        var mine = P2Slots == null ? _p1WhileEditing : Validated(P2Slots, _p1WhileEditing); // first time: start from player 1's gear
        Write(save, mine);
        _editingP2 = true;
        Plugin.Logger.LogInfo($"Boat loadout: editing player 2 ({mine}); player 1 kept as {_p1WhileEditing}");
    }

    private static void StopEditing(string why)
    {
        var save = Save();
        _editingP2 = false;
        if (save == null || _p1WhileEditing == null)
            return;
        var mine = Read(save);
        _saved.Value = mine.ToString();
        Write(save, _p1WhileEditing);
        Plugin.Logger.LogInfo($"Boat loadout ({why}): player 2 saved as {mine}; player 1 back to {_p1WhileEditing}");
        _p1WhileEditing = null;
    }

    // Redraw the equipment slots and the item list's "equipped" marks.
    private static void Refresh()
    {
        try
        {
            var nodes = _page._invenNode;
            if (nodes != null)
                for (var i = 0; i < nodes.Length; i++)
                    try { nodes[i]?.SetUIData(); }
                    catch (Exception exception) { Plugin.Logger.LogWarning($"Boat loadout slot {i}: {exception.Message}"); }
            _page._inventoryScroll?.RefreshActiveCellViews();
            var charms = _page.GetComponentsInChildren(Il2CppType.Of<LobbyCharmInfoPanel>(), true);
            for (var i = 0; i < charms.Length; i++)
                try { charms[i]?.TryCast<LobbyCharmInfoPanel>()?.RefreshUIData(); }
                catch (Exception exception) { Plugin.Logger.LogWarning($"Boat loadout charms: {exception.Message}"); }
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning($"Boat loadout refresh: {exception.Message}");
        }
    }

    private static bool SwapPressed()
    {
        var pressed = false;
        var e = P2Keys.IsDown(0x45);
        if (e && !_eWasDown)
            pressed = true;
        _eWasDown = e;
        if (P2Pad.Connected && P2Keys.Pressed(P2Keys.Action.SwitchWeapon))
            pressed = true;
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


    // ---- Title ("Equipment" -> "Player 1 / Player 2") and Dave's picture ----

    private static TMP_Text _label;
    private static string _labelText;
    private static readonly Dictionary<IntPtr, Sprite> _previewOriginals = new();
    private static int _lateLogs;

    private static TMP_Text FindLabel(LobbyDiverBoxPage page)
    {
        var roots = new List<Transform> { page.transform };
        var canvas = page.GetComponentInParent<Canvas>();
        if (canvas != null)
            roots.Add(canvas.rootCanvas.transform);
        foreach (var root in roots)
        {
            var texts = root.GetComponentsInChildren(Il2CppType.Of<TMP_Text>(), true);
            for (var i = 0; i < texts.Length; i++)
            {
                var text = texts[i]?.TryCast<TMP_Text>();
                if (text != null && text.text != null && text.text.Trim().Equals("Equipment", StringComparison.OrdinalIgnoreCase)
                    && text.gameObject.activeInHierarchy)
                    return text;
            }
        }
        // Not found: list what is there, so it can be pinned down from the log.
        var all = page.transform.GetComponentsInChildren(Il2CppType.Of<TMP_Text>(), true);
        var seen = new List<string>();
        for (var i = 0; i < all.Length && seen.Count < 40; i++)
        {
            var text = all[i]?.TryCast<TMP_Text>();
            if (text != null)
                seen.Add($"{text.name}='{text.text}'");
        }
        Plugin.Logger.LogInfo("Boat page texts: " + string.Join("; ", seen));
        return null;
    }

    internal static void LateTick()
    {
        if (_page == null)
            return;
        try
        {
            if (_label != null)
            {
                var want = _editingP2
                    ? "Player 2  <size=70%>(RB / E: Player 1)</size>"
                    : "Player 1  <size=70%>(RB / E: Player 2)</size>";
                var current = _label.text;
                if (current != want)
                {
                    if (current != null && !current.StartsWith("Player "))
                        _labelText = current;
                    _label.text = want;
                }
            }
            if (_editingP2 && P2Skin.Enabled)
                RecolorPreview();
        }
        catch (Exception exception)
        {
            if (_lateLogs++ < 3)
                Plugin.Logger.LogWarning($"Boat loadout title/picture: {exception.Message}");
        }
    }

    // Dave's picture on the page is drawn by the page's costume handler.
    private static Il2CppReferenceArray<Image> PreviewImages()
    {
        var handler = _page?._costumeHandler;
        return handler == null ? null : handler.targetUIImages;
    }

    private static void RecolorPreview()
    {
        var images = PreviewImages();
        if (images == null)
            return;
        for (var i = 0; i < images.Length; i++)
        {
            var image = images[i];
            var sprite = image?.sprite;
            if (sprite == null)
                continue;
            var swapped = P2Skin.RecoloredForMenu(sprite);
            if (swapped == null || swapped.Pointer == sprite.Pointer)
                continue;
            _previewOriginals[swapped.Pointer] = sprite;
            image.sprite = swapped;
            if (_lateLogs < 1)
            {
                _lateLogs++;
                Plugin.Logger.LogInfo($"Boat page picture: {image.name} {sprite.name} -> purple");
            }
        }
    }

    private static void RestorePreview()
    {
        try
        {
            var images = PreviewImages();
            if (images == null)
                return;
            for (var i = 0; i < images.Length; i++)
            {
                var image = images[i];
                var sprite = image?.sprite;
                if (sprite != null && _previewOriginals.TryGetValue(sprite.Pointer, out var original) && original != null)
                    image.sprite = original;
            }
        }
        catch
        {
        }
    }

}

[HarmonyPatch(typeof(LobbyDiverBoxPage), nameof(LobbyDiverBoxPage.Show))]
internal static class BoatLoadoutShowPatch
{
    private static void Postfix(LobbyDiverBoxPage __instance) => P2Loadout.Opened(__instance);
}

[HarmonyPatch(typeof(LobbyDiverBoxPage), nameof(LobbyDiverBoxPage.Hide))]
internal static class BoatLoadoutHidePatch
{
    private static void Prefix() => P2Loadout.Closing();
}
