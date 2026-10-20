using System;
using System.Collections.Generic;
using System.Reflection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace DaveTheDiverCoop;

// A copied UI object keeps only what Unity saves with it. References the game
// fills in at run time stay empty (or still point at the original). For every
// script in the copy, fill each empty reference from the original, pointing it
// at the matching part of the copy when the original pointed inside itself.
internal static class CloneRepair
{
    internal static int Fill(GameObject original, GameObject copy, string label)
    {
        var fixes = new List<string>();
        try
        {
            var a = original.GetComponentsInChildren(Il2CppType.Of<MonoBehaviour>(), true);
            var b = copy.GetComponentsInChildren(Il2CppType.Of<MonoBehaviour>(), true);
            if (a.Length != b.Length)
            {
                Plugin.Logger.LogWarning($"{label}: copy has {b.Length} scripts, original {a.Length}; not repairing");
                return 0;
            }
            for (var i = 0; i < a.Length; i++)
            {
                var from = a[i];
                var to = b[i];
                if (from == null || to == null)
                    continue;
                var type = InteropType(from);
                if (type == null)
                    continue;
                var typedFrom = Activator.CreateInstance(type, from.Pointer);
                var typedTo = Activator.CreateInstance(type, to.Pointer);
                FillObject(type, typedFrom, typedTo, original.transform, copy.transform, from.name, fixes);
            }
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning($"{label}: repair failed: {exception.Message}");
        }
        Plugin.Logger.LogInfo($"{label}: filled {fixes.Count} reference(s): {string.Join(", ", fixes)}");
        return fixes.Count;
    }

    // Find the interop wrapper class for a game script (e.g. OxygenInfoText).
    private static Type InteropType(Component component)
    {
        var name = component.GetIl2CppType().FullName;
        if (string.IsNullOrEmpty(name) || name.StartsWith("UnityEngine") || name.StartsWith("TMPro"))
            return null;
        return typeof(PlayerCharacter).Assembly.GetType(name);
    }

    private static void FillObject(Type type, object from, object to, Transform fromRoot, Transform toRoot, string where, List<string> fixes)
    {
        for (var t = type; t != null && t.Assembly == typeof(PlayerCharacter).Assembly; t = t.BaseType)
        {
            foreach (var property in t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length > 0)
                    continue;
                if (!typeof(Il2CppObjectBase).IsAssignableFrom(property.PropertyType))
                    continue;
                object value, current;
                try
                {
                    value = property.GetValue(from);
                    current = property.GetValue(to);
                }
                catch
                {
                    continue;
                }
                if (value == null)
                    continue;
                var mapped = Map(value, fromRoot, toRoot, property.PropertyType);
                if (current != null)
                {
                    // Still pointing at the original's part? Point at the copy's.
                    if (current is Component c && c.transform.IsChildOf(fromRoot) && mapped != null)
                    {
                        Set(property, to, mapped);
                        fixes.Add($"{where}.{property.Name}(re-pointed)");
                    }
                    continue;
                }
                if (mapped != null)
                {
                    Set(property, to, mapped);
                    fixes.Add($"{where}.{property.Name}");
                }
                else if (value is UnityEngine.Object unityObject && !(value is Component comp && comp.transform.IsChildOf(fromRoot)) &&
                         !(value is GameObject go && go.transform.IsChildOf(fromRoot)))
                {
                    // Something outside the panel (shared asset, camera...): share it.
                    Set(property, to, unityObject);
                    fixes.Add($"{where}.{property.Name}(shared)");
                }
            }
        }
    }

    private static object Map(object value, Transform fromRoot, Transform toRoot, Type propertyType)
    {
        Transform source = value switch
        {
            Component component => component.transform,
            GameObject gameObject => gameObject.transform,
            _ => null,
        };
        if (source == null || !source.IsChildOf(fromRoot))
            return null;
        var path = Path(fromRoot, source);
        var target = path.Length == 0 ? toRoot : toRoot.Find(path);
        if (target == null)
            return null;
        if (value is GameObject)
            return target.gameObject;
        var match = target.GetComponent(((Component)value).GetIl2CppType());
        return match == null ? null : Activator.CreateInstance(propertyType, match.Pointer);
    }

    private static void Set(PropertyInfo property, object target, object value)
    {
        if (value != null && !property.PropertyType.IsInstanceOfType(value) && value is Il2CppObjectBase baseObject)
            value = Activator.CreateInstance(property.PropertyType, baseObject.Pointer);
        property.SetValue(target, value);
    }

    private static string Path(Transform root, Transform child)
    {
        var parts = new List<string>();
        for (var t = child; t != null && t != root; t = t.parent)
            parts.Insert(0, t.name);
        return string.Join("/", parts);
    }
}
