using System;
using System.Collections.Generic;
using InputSystemWrapper;

namespace DaveTheDiverCoop;

// Each game input action has one callback slot (Action.onCallback). Dave fills
// some of them (mouse aiming, etc.) with his own methods when he starts up, so
// a cloned Dave overwrites player 1's: mouse movement then goes to the clone
// and player 1's aim freezes, even after the clone is gone. Save every slot
// before the clone starts and put player 1's back afterwards.
internal static class ActionCallbacks
{
    private sealed class Saved
    {
        internal InputSystemWrapper.Action Action;
        internal Il2CppSystem.Action<UnityEngine.InputSystem.InputAction.CallbackContext> Callback;
        internal string Name;
    }

    private static readonly List<Saved> _saved = new();

    internal static void Snapshot(InputAsset asset)
    {
        _saved.Clear();
        if (asset == null)
            return;
        try
        {
            foreach (var map in Maps(asset))
            {
                var unityMap = map?.inputActionMap;
                if (unityMap == null)
                    continue;
                var actions = unityMap.actions.ToArray();
                for (var i = 0; i < actions.Length; i++)
                {
                    var name = actions[i]?.name;
                    if (string.IsNullOrEmpty(name))
                        continue;
                    var action = map[name];
                    if (action == null)
                        continue;
                    _saved.Add(new Saved { Action = action, Callback = action.onCallback, Name = unityMap.name + "/" + name });
                }
            }
            Plugin.Logger.LogInfo($"Input callbacks saved: {_saved.Count} actions");
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning($"Input callback snapshot failed: {exception.Message}");
        }
    }

    internal static void Restore(string when)
    {
        var restored = 0;
        foreach (var saved in _saved)
        {
            try
            {
                var current = saved.Action.onCallback;
                var currentPointer = current?.Pointer ?? IntPtr.Zero;
                var savedPointer = saved.Callback?.Pointer ?? IntPtr.Zero;
                if (currentPointer == savedPointer)
                    continue;
                saved.Action.onCallback = saved.Callback;
                restored++;
                if (restored <= 10)
                    Plugin.Logger.LogInfo($"Input callback given back to player 1: {saved.Name}");
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning($"Input callback restore failed for {saved.Name}: {exception.Message}");
            }
        }
        if (restored > 0 || when != "periodic")
            Plugin.Logger.LogInfo($"Input callbacks checked ({when}): {restored} of {_saved.Count} given back to player 1");
    }

    private static IEnumerable<ActionMap> Maps(InputAsset asset)
    {
        var drAsset = asset.TryCast<DRInput.DRInputAsset>();
        var list = drAsset?._actionMaps;
        if (list == null)
            yield break;
        for (var i = 0; i < list.Count; i++)
            yield return list[i];
    }
}
