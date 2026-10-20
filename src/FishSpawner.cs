using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DaveTheDiverCoop;

// Test keys F3 / F4 / F5: spawn a fish next to player 1 (default: sawtooth
// shark, tiger shark, shortfin mako). Each looks through the level's fish
// spawners for a fish whose name contains its [Debug] setting. All
// spawnable names are written to the log.
internal static class FishSpawner
{
    private static readonly (int vk, string key)[] Keys = { (0x72, "F3"), (0x73, "F4"), (0x74, "F5") };
    private static readonly ConfigEntry<string>[] _names = new ConfigEntry<string>[3];
    private static readonly bool[] _wasDown = new bool[3];
    private static bool _listed;
    private static readonly Dictionary<string, string> _qte = new();
    private static int _mashIndex;

    // What QTE parts a fish prefab has ("" = none), cached by name.
    private static string QteParts(GameObject prefab)
    {
        if (_qte.TryGetValue(prefab.name, out var parts))
            return parts;
        var found = new SortedSet<string>();
        try
        {
            var components = prefab.GetComponentsInChildren(Il2CppType.Of<Component>(), true);
            for (var i = 0; i < components.Length; i++)
            {
                var type = components[i]?.GetIl2CppType()?.Name;
                if (type != null && (type.IndexOf("QTE", StringComparison.OrdinalIgnoreCase) >= 0
                                     || type.IndexOf("Biteable", StringComparison.OrdinalIgnoreCase) >= 0))
                    found.Add(type);
            }
        }
        catch
        {
        }
        parts = string.Join("+", found);
        _qte[prefab.name] = parts;
        return parts;
    }

    internal static void Bind(ConfigFile config)
    {
        _names[0] = config.Bind("Debug", "SpawnFishName", "Saw",
            "F3 spawns the first fish whose name contains this (e.g. Saw, Shark). Names are listed in the log on the first press.");
        _names[1] = config.Bind("Debug", "SpawnFishNameF4", "Tiger_Shark", "Same, for F4.");
        _names[2] = config.Bind("Debug", "SpawnFishNameF5", "mash",
            "Same, for F5. \"mash\" = cycle through the fish that have a button-mash (QTE) part.");
    }

    internal static void Tick()
    {
        for (var i = 0; i < Keys.Length; i++)
        {
            // Shift + F3 / F4 belong to the test bot.
            var down = P2Keys.IsDown(Keys[i].vk) && !P2Keys.IsDown(0xA0);
            if (down && !_wasDown[i])
            {
                try { Spawn(Keys[i].key, _names[i]?.Value); }
                catch (Exception exception) { Plugin.Logger.LogWarning($"{Keys[i].key} fish spawn: {exception}"); }
            }
            _wasDown[i] = down;
        }
    }

    // Also used by the test bot: spawn at a given spot and get the fish back.
    internal static GameObject SpawnAt(string name, Vector3 at) => Spawn("Test", name, at);

    private static GameObject Spawn(string key, string name, Vector3? at = null)
    {
        var p1 = SecondDiver.Player1 ?? Object.FindFirstObjectByType<PlayerCharacter>();
        if (p1 == null)
        {
            Plugin.Logger.LogInfo($"{key}: no diver in this scene");
            return null;
        }
        var want = (name ?? "Saw").Trim();
        var found = Resources.FindObjectsOfTypeAll(Il2CppType.Of<FishAllocator>());
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        FishAllocator pickAllocator = null;
        GameObject pickPrefab = null;
        var mash = want.Equals("mash", StringComparison.OrdinalIgnoreCase);
        var mashFish = new SortedDictionary<string, (FishAllocator allocator, GameObject prefab)>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < found.Length; i++)
        {
            var allocator = found[i]?.TryCast<FishAllocator>();
            if (allocator == null || !allocator.gameObject.scene.IsValid())
                continue; // skip prefab assets
            foreach (var prefab in Prefabs(allocator))
            {
                names.Add(prefab.name);
                if (mash)
                {
                    if (QteParts(prefab).Length > 0 && !mashFish.ContainsKey(prefab.name))
                        mashFish[prefab.name] = (allocator, prefab);
                    continue;
                }
                // Several can match (e.g. Tiger_Shark and Tiger_Shark_Night_V2,
                // which vanishes in daytime): take the shortest name.
                if (prefab.name.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0
                    && (pickPrefab == null || prefab.name.Length < pickPrefab.name.Length))
                {
                    pickAllocator = allocator;
                    pickPrefab = prefab;
                }
            }
        }
        if (mash)
        {
            if (mashFish.Count == 0)
            {
                Plugin.Logger.LogInfo($"{key}: no fish with a button-mash part in this level");
                return null;
            }
            var list = new List<string>(mashFish.Keys);
            Plugin.Logger.LogInfo($"{key}: fish with a button-mash part: " +
                string.Join(", ", list.ConvertAll(n => $"{n} [{_qte[n]}]")));
            var pick = mashFish[list[_mashIndex++ % list.Count]];
            pickAllocator = pick.allocator;
            pickPrefab = pick.prefab;
        }
        if (!_listed || pickPrefab == null)
        {
            _listed = true;
            Plugin.Logger.LogInfo($"{key}: {names.Count} spawnable fish in this level: {string.Join(", ", names)}");
        }
        if (pickPrefab == null)
        {
            // Not in this level's spawners: any loaded fish (e.g. another area's) with that name?
            var loaded = Resources.FindObjectsOfTypeAll(Il2CppType.Of<DR.AI.FishAISystem>());
            for (var i = 0; i < loaded.Length && pickPrefab == null; i++)
            {
                var fish = loaded[i]?.TryCast<DR.AI.FishAISystem>();
                if (fish != null && fish.gameObject.name.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0)
                    pickPrefab = fish.gameObject;
            }
            if (pickPrefab == null && UseGameFiles)
            {
                pickPrefab = FromGameFiles(want);
                if (pickPrefab != null && pickAllocator == null)
                    pickAllocator = AnyAllocator(found);
            }
            if (pickPrefab == null)
            {
                Plugin.Logger.LogInfo($"{key}: nothing in this level matches \"{want}\" (change it in the [Debug] config to one of the names above)");
                return null;
            }
        }

        var facing = p1.transform.lossyScale.x < 0f ? -1f : 1f;
        var position = at ?? p1.transform.position + new Vector3(7f * facing, 1f, 0f);
        GameObject instance = null;
        try
        {
            if (pickAllocator != null)
                instance = pickAllocator.DoInstanceFishOrGroup(pickPrefab, null, false);
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogInfo($"{key}: spawner refused ({exception.Message}); copying the fish directly");
        }
        if (instance == null)
            instance = Object.Instantiate(pickPrefab);
        if (!instance.activeInHierarchy)
            instance.transform.SetParent(null, true);
        instance.SetActive(true);
        instance.transform.position = new Vector3(position.x, position.y, pickPrefab.transform.position.z);
        Plugin.Logger.LogInfo($"{key}: spawned {instance.name} at {instance.transform.position} (from {(pickAllocator != null ? "spawner " + pickAllocator.name : "a loaded copy")})");
        return instance;
    }

    // Every creature whose spawner is loaded right now, with where that spawner is.
    internal static List<(string name, Vector3 at)> LoadedHabitats()
    {
        var list = new List<(string, Vector3)>();
        var found = Resources.FindObjectsOfTypeAll(Il2CppType.Of<FishAllocator>());
        for (var i = 0; i < found.Length; i++)
        {
            var allocator = found[i]?.TryCast<FishAllocator>();
            if (allocator == null || !allocator.gameObject.scene.IsValid())
                continue;
            foreach (var prefab in Prefabs(allocator))
                list.Add((prefab.name, allocator.transform.position));
        }
        return list;
    }

    private static FishAllocator AnyAllocator(Il2CppReferenceArray<Object> found)
    {
        for (var i = 0; i < found.Length; i++)
        {
            var allocator = found[i]?.TryCast<FishAllocator>();
            if (allocator != null && allocator.gameObject.scene.IsValid())
                return allocator;
        }
        return null;
    }

    // Fish that live in other areas aren't loaded here, but the game's
    // asset catalogue (Addressables) can load any of them by name.
    private static List<string> _catalogue;
    internal static bool UseGameFiles; // off: reading the catalogue crashed the game once

    private static GameObject FromGameFiles(string want)
    {
        try
        {
            if (_catalogue == null)
            {
                Plugin.Logger.LogInfo("Fish catalogue: reading the game's asset catalogue...");
                _catalogue = new List<string>();
                var locators = Addressables.ResourceLocators.Cast<Il2CppSystem.Collections.IEnumerable>().GetEnumerator();
                while (locators.MoveNext())
                {
                    var locator = locators.Current?.TryCast<IResourceLocator>();
                    if (locator == null)
                        continue;
                    var keys = locator.Keys.Cast<Il2CppSystem.Collections.IEnumerable>().GetEnumerator();
                    while (keys.MoveNext() && _catalogue.Count < 50000)
                    {
                        var k = keys.Current?.ToString();
                        if (k == null)
                            continue;
                        var file = k.Substring(k.LastIndexOf('/') + 1);
                        if (file.StartsWith("SA_") || file.StartsWith("Boid_SA_"))
                            _catalogue.Add(k);
                    }
                }
                Plugin.Logger.LogInfo($"Fish catalogue: {_catalogue.Count} creature entries in the game files");
            }
            string best = null;
            foreach (var k in _catalogue)
            {
                if (k.IndexOf(want, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                // Prefer the plain prefab: a ".prefab" path or a bare name, shortest.
                var isPrefab = k.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) || k.IndexOf('.') < 0;
                if (!isPrefab)
                    continue;
                if (best == null || k.Length < best.Length)
                    best = k;
            }
            if (best == null)
            {
                Plugin.Logger.LogInfo($"Fish catalogue: nothing named like \"{want}\"");
                return null;
            }
            var handle = Addressables.LoadAssetAsync<GameObject>(new Il2CppSystem.Object(IL2CPP.ManagedStringToIl2Cpp(best)));
            var prefab = handle.WaitForCompletion();
            Plugin.Logger.LogInfo($"Fish catalogue: loaded \"{best}\" -> {(prefab != null ? prefab.name : "nothing")}");
            if (prefab == null || prefab.GetComponentInChildren<DR.AI.FishAISystem>(true) == null)
                return null;
            return prefab;
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogInfo($"Fish catalogue: could not load \"{want}\" ({exception.Message})");
            return null;
        }
    }

    private static IEnumerable<GameObject> Prefabs(FishAllocator allocator)
    {
        var list = new List<GameObject>();
        try
        {
            var single = allocator.FishPrefabOrGroup;
            if (single != null)
                list.Add(single);
        }
        catch { }
        try
        {
            var first = allocator.GetFirstFishPrefab;
            if (first != null && (list.Count == 0 || first.Pointer != list[0].Pointer))
                list.Add(first);
        }
        catch { }
        return list;
    }
}
