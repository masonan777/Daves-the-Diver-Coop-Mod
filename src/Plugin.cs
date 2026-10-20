using System.Linq;
using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace DaveTheDiverCoop;

[BepInPlugin("dev.davethedivercoop.local", "Dave the Diver Local Coop", "0.1.0")]
public sealed class Plugin : BasePlugin
{
    internal static ManualLogSource Logger;

    public override void Load()
    {
        Logger = Log;
        P2Keys.Bind(Config);
        P2Pad.Bind(Config);
        P2Skin.Bind(Config);
        P2Loadout.Bind(Config);
        FishSpawner.Bind(Config);
        NoSharkGrab.Bind(Config);
        P2Grab.Bind(Config);
        CoopTest.Bind(Config);
        KeyboardFilter.SelfTest();
        ClassInjector.RegisterTypeInIl2Cpp<CoopBehaviour>();
        // Patch class by class so one failure (e.g. a generic input method the
        // runtime cannot hook) does not stop the others.
        RvaDump.Write(System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "coop_functions.txt"));
        var disabled = Config.Bind("Debug", "DisabledPatches", "",
            "Comma-separated patch names to leave off (for tracking down crashes)").Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            // Always off: these hook generic-based game classes and crash the
            // game during loading (found by bisecting; kept for reference).
            .Concat(new[] { "BiteMashUpdate", "BiteMashInit", "BiteMashFinish", "BiteStateEnter", "BiteStateUpdate", "BiteStateExit" })
            .ToArray();
        var traces = Config.Bind("Debug", "EnableTraceHooks", false,
            "Turn on the diagnostic trace hooks (only for tracking down a problem)").Value;
        var harmony = new Harmony("dev.davethedivercoop.local");
        foreach (var type in typeof(Plugin).Assembly.GetTypes())
        {
            if (type.GetCustomAttributes(typeof(HarmonyPatch), false).Length == 0)
                continue;
            // Diagnostic trace hooks stay off unless asked for (some hooks
            // into damage handling crashed the game).
            if (!traces && type.Name != "TraceAimAxisMousePatch" && type.Name.Contains("Trace"))
                continue;
            if (Array.IndexOf(disabled, type.Name) >= 0)
            {
                Log.LogInfo($"Skipped {type.Name} (DisabledPatches)");
                continue;
            }
            try
            {
                harmony.CreateClassProcessor(type).Patch();
                Log.LogInfo($"Patched {type.Name}");
            }
            catch (Exception exception)
            {
                Log.LogError($"Patch {type.Name} failed: {exception.Message}");
            }
        }
        AddComponent<CoopBehaviour>();
        Log.LogInfo("Local coop loaded. Press F9 in game to add/remove player 2.");
    }
}

public sealed class CoopBehaviour : MonoBehaviour
{
    public CoopBehaviour(IntPtr pointer) : base(pointer)
    {
    }

    private static bool _f11WasDown;
    private static bool _f10WasDown;
    private static bool _f8WasDown;
    private static bool _f12WasDown;
    private static bool _f6WasDown;
    private static bool _f7WasDown;

    private static bool Edge(int vk, ref bool wasDown)
    {
        var down = P2Keys.IsDown(vk);
        var pressed = down && !wasDown;
        wasDown = down;
        return pressed;
    }

    private void Update()
    {
        P2Keys.Tick();
        // Also on while player 2 is in a sushi mini-game: player 1's keys then
        // drive player 1 through the mod (SushiP1Drive) and must not reach it.
        KeyboardFilter.Tick(P2Keys.Joined && (P2Keys.FilterPlayerOne || SushiCoop.Player2ScreenOpen));
        SecondDiver.Tick(P2Keys.Joined);
        SushiCoop.Tick();
        BoatDiver.Tick();
        ReloadWithoutP2.Tick();
        TitleLogo.Tick();
        HudDump.Tick();
        CoopOverlay.Tick();
        P2PauseGear.Tick();
        P2Loadout.Tick();
        FishSpawner.Tick();
        NoSharkGrab.Tick();
        try { P2Grab.Tick(); } catch (System.Exception e) { Plugin.Logger.LogWarning($"P2 grab: {e.Message}"); P2Grab.Clear(); }
        CoopTest.Tick();
        DiveEntryRecorder.Tick();
        if (Edge(0x7B, ref _f12WasDown))
            HudDump.Dump("F12 pressed");
        if (Edge(0x76, ref _f7WasDown))
            SecondDiver.ToggleCameraTarget();
        var combo = ResetComboHeld();
        if (Edge(0x75, ref _f6WasDown) | (combo && !_comboWasDown))
        {
            RefillOxygen();
            ResetHud();
        }
        _comboWasDown = combo;
        // X + Y on any controller: just bring the HUD back (works without player 2).
        var hudCombo = HudComboHeld();
        if (hudCombo && !_hudComboWasDown)
            ResetHud();
        _hudComboWasDown = hudCombo;
        if (P2Keys.IsDown(0x77))
        {
            if (!_f8WasDown)
                SecondDiver.ToggleManualAnimator();
            _f8WasDown = true;
        }
        else
        {
            _f8WasDown = false;
        }
        if (P2Keys.IsDown(0x79))
        {
            if (!_f10WasDown)
                SecondDiver.ReequipPlayerOne();
            _f10WasDown = true;
        }
        else
        {
            _f10WasDown = false;
        }
        if (P2Keys.IsDown(0x7A))
        {
            if (!_f11WasDown)
                SecondDiver.ToggleRootMotionFix();
            _f11WasDown = true;
        }
        else
        {
            _f11WasDown = false;
        }
    }

    private void LateUpdate()
    {
        SecondDiver.TracePhase(SecondDiver.Player2, "late-update");
        SecondDiver.LateSync();
        P2Skin.Apply(SecondDiver.Player2);
        P2PauseGear.LateTick();
        P2Loadout.LateTick();
        try { P2Grab.LateTick(); } catch { }
        BoatDiver.LateTick();
        SecondDiver.SyncWeaponVisuals();
        CoopCamera.Drive();
        CoopCamera.Leash(SecondDiver.Player2);
        SecondDiver.TracePhase(SecondDiver.Player2, "late-synced");
    }

    private static bool _comboWasDown, _hudComboWasDown;

    private static bool HudComboHeld()
    {
        try
        {
            if (P2Pad.Connected && P2Pad.Down(P2Keys.Action.Interact) && P2Pad.Down(P2Keys.Action.Item))
                return true;
            var pads = UnityEngine.InputSystem.Gamepad.all;
            for (var i = 0; i < pads.Count; i++)
            {
                var pad = pads[i];
                if (pad != null && pad.enabled && pad.buttonWest.isPressed && pad.buttonNorth.isPressed)
                    return true;
            }
        }
        catch
        {
        }
        return false;
    }

    // Y + B + A held together on any controller (player 1's or player 2's).
    private static bool ResetComboHeld()
    {
        try
        {
            if (P2Pad.Connected && P2Pad.Down(P2Keys.Action.Item) && P2Pad.Down(P2Keys.Action.Back) && P2Pad.Down(P2Keys.Action.Ok))
                return true;
            var pads = UnityEngine.InputSystem.Gamepad.all;
            for (var i = 0; i < pads.Count; i++)
            {
                var pad = pads[i];
                if (pad != null && pad.enabled && pad.buttonNorth.isPressed && pad.buttonEast.isPressed && pad.buttonSouth.isPressed)
                    return true;
            }
        }
        catch
        {
        }
        return false;
    }

    // Bring the in-game HUD back (a grab can leave it hidden).
    internal static void ResetHud()
    {
        try
        {
            var canvas = UnityEngine.Object.FindFirstObjectByType<MainCanvasManager>();
            if (canvas == null)
            {
                Plugin.Logger.LogInfo("HUD reset: no HUD in this scene");
                return;
            }
            var wasOn = canvas.IsHudOn;
            try { canvas.ClearAllHUDLocksByRevive(); } catch (System.Exception e) { Plugin.Logger.LogWarning($"HUD reset (locks): {e.Message}"); }
            try { canvas.SetVisibleCanvasUI(true); } catch (System.Exception e) { Plugin.Logger.LogWarning($"HUD reset (canvas): {e.Message}"); }
            try { canvas.ShowHUD(); } catch (System.Exception e) { Plugin.Logger.LogWarning($"HUD reset (show): {e.Message}"); }
            Plugin.Logger.LogInfo($"HUD reset: HUD was {(wasOn ? "on" : "off")}, now {(canvas.IsHudOn ? "on" : "off")}");
        }
        catch (System.Exception exception)
        {
            Plugin.Logger.LogWarning($"HUD reset: {exception.Message}");
        }
    }

    // Test key F6: fill both divers' oxygen tanks.
    private static void RefillOxygen()
    {
        foreach (var (diver, label) in new[] { (SecondDiver.Player1 ?? UnityEngine.Object.FindFirstObjectByType<PlayerCharacter>(), "player 1"), (SecondDiver.Player2, "player 2") })
        {
            try
            {
                var tank = diver?.BreathHandler;
                if (tank == null)
                    continue;
                tank.HP = tank.MaxHP;
                var locked = diver.IsActionLock;
                var moveLocked = diver.IsMoveLocked;
                string state = "?";
                try { state = $"{diver.PlayerActionState}"; } catch { }
                Plugin.Logger.LogInfo($"F6: {label} before reset: actionLock={locked} moveLocked={moveLocked} action={state}");
                // Undo whatever a broken grab left behind.
                try { diver.ForceReleaseCommonQTE(); } catch (System.Exception e) { Plugin.Logger.LogWarning($"F6 {label} release QTE: {e.Message}"); }
                try { diver.DetachFollowing(); } catch (System.Exception e) { Plugin.Logger.LogWarning($"F6 {label} detach: {e.Message}"); }
                try { if (diver.IsMoveLocked) diver.UnlockMove(); } catch (System.Exception e) { Plugin.Logger.LogWarning($"F6 {label} unlock move: {e.Message}"); }
                try { if (diver.IsActionLock) diver.SetActionLock(false); } catch (System.Exception e) { Plugin.Logger.LogWarning($"F6 {label} unlock action: {e.Message}"); }
                Plugin.Logger.LogInfo($"F6: {label} oxygen refilled to {tank.HP:F0}; now actionLock={diver.IsActionLock} moveLocked={diver.IsMoveLocked}");
            }
            catch (System.Exception exception)
            {
                Plugin.Logger.LogWarning($"F6 refill {label}: {exception.Message}");
            }
        }
    }

    private void OnGUI()
    {
        CoopOverlay.Draw();
        P2Grab.Draw();
        CoopTest.Draw();
        ReloadWithoutP2.Draw();
    }
}
