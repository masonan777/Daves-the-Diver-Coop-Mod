using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace DaveTheDiverCoop;

// Pause menu escape hatch: hold Delete (keyboard) or View / Back (controller)
// for 2 seconds to drop player 2 and load the most recent save, through the
// same steps the game's own Load screen uses (load slot -> new save -> boat).
internal static class ReloadWithoutP2
{
    private const float HoldTime = 2f;
    private static float _held;
    private static bool _pending;
    private static int _waitFrames;
    private static GUIStyle _style;

    internal static void Tick()
    {
        if (_pending)
        {
            // Wait until player 2 is gone, then load.
            if (SecondDiver.Player2 != null && _waitFrames++ < 120)
                return;
            _pending = false;
            Load();
            return;
        }
        if (!P2PauseGear.IsOpen)
        {
            _held = 0f;
            return;
        }
        if (HeldNow())
        {
            _held += Time.unscaledDeltaTime;
            if (_held >= HoldTime)
            {
                _held = 0f;
                Plugin.Logger.LogInfo("Reload without player 2: requested from the pause menu");
                P2Keys.Leave("reload last save");
                _pending = true;
                _waitFrames = 0;
            }
        }
        else
        {
            _held = 0f;
        }
    }

    private static bool HeldNow()
    {
        if (P2Keys.IsDown(0x2E))
            return true;
        try
        {
            var pads = Gamepad.all;
            for (var i = 0; i < pads.Count; i++)
                if (pads[i] != null && pads[i].enabled && pads[i].selectButton.isPressed)
                    return true;
        }
        catch
        {
        }
        return false;
    }

    internal static void Draw()
    {
        if (!P2PauseGear.IsOpen)
            return;
        _style ??= CoopOverlay.DarkBox(15, TextAnchor.MiddleCenter);
        var text = _held > 0f
            ? $"Reloading last save without Player 2... {Mathf.CeilToInt(HoldTime - _held)}"
            : "Hold <b>View / Back</b> (2 s): reload last save without Player 2";
        var content = new GUIContent(text);
        var size = _style.CalcSize(content);
        GUI.Box(new Rect(Screen.width - size.x - 30f, Screen.height - size.y - 50f, size.x + 20f, size.y + 8f), content, _style);
    }

    private static object Instance(Type type)
    {
        for (var t = type; t != null; t = t.BaseType)
        {
            var property = t.GetProperty("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (property != null)
                return property.GetValue(null);
        }
        return null;
    }

    private static void Load()
    {
        try
        {
            var system = Instance(typeof(DR.Save.SaveSystem)) as DR.Save.SaveSystem;
            var data = system?.GameDataManager;
            if (system == null || data == null)
            {
                Plugin.Logger.LogWarning("Reload without player 2: save system not found");
                return;
            }
            // Newest save across auto and manual slots.
            var bestTime = DateTime.MinValue;
            var bestSlot = -1;
            var bestType = DR.Save.SaveSlotType.Auto;
            foreach (var (type, count) in new[] { (DR.Save.SaveSlotType.Auto, data.MaxAutoSaveSlotCount), (DR.Save.SaveSlotType.Manual, data.MaxManualSaveCount) })
            {
                for (var slot = 0; slot < count; slot++)
                {
                    try
                    {
                        var saved = data.GetSavedData(slot, type);
                        if (saved == null || string.IsNullOrEmpty(saved.DataString))
                            continue;
                        var when = new DateTime(saved.UpdatedTime.Ticks);
                        if (when > bestTime)
                        {
                            bestTime = when;
                            bestSlot = slot;
                            bestType = type;
                        }
                    }
                    catch
                    {
                    }
                }
            }
            if (bestSlot < 0)
            {
                Plugin.Logger.LogWarning("Reload without player 2: no save found");
                return;
            }
            Plugin.Logger.LogInfo($"Reload without player 2: loading {bestType} slot {bestSlot} (saved {bestTime})");
            if (!system.LoadGameDataFromSlot(bestSlot, bestType))
            {
                Plugin.Logger.LogWarning("Reload without player 2: the game could not load that save");
                return;
            }
            var game = Instance(typeof(GameBase)) as GameBase;
            game?.OnLoadNewSave();
            Time.timeScale = 1f;
            var loader = (Instance(typeof(GlobalSceneLoader)) as GlobalSceneLoader)?.sceneLoader
                         ?? UnityEngine.Object.FindFirstObjectByType<SceneLoader>();
            if (loader == null)
            {
                Plugin.Logger.LogWarning("Reload without player 2: scene loader not found");
                return;
            }
            loader.GoToLobbyEntry((SceneTransitionType)0, true, true, true, true);
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning($"Reload without player 2 failed: {exception}");
        }
    }
}
