using System;
using System.Text;
using UnityEngine;

namespace DaveTheDiverCoop;

// Diagnostics: while each diver aims the gun, write out which parts of the
// aim guide are on (and which sprites are drawn), so player 2's can be made to
// match player 1's exactly.
internal static class GuideDump
{
    private static int _p1Dumps, _p2Dumps;
    private static float _p1Next, _p2Next;

    internal static void Tick(PlayerCharacter p1, PlayerCharacter p2)
    {
        Check(p1, "P1", ref _p1Dumps, ref _p1Next);
        Check(p2, "P2", ref _p2Dumps, ref _p2Next);
    }

    private static void Check(PlayerCharacter p, string who, ref int dumps, ref float next)
    {
        if (p == null || dumps >= 3 || Time.realtimeSinceStartup < next)
            return;
        try
        {
            if (p.PlayerActionState != RangeActionType.Gun || p.GetRangeActionState.ToString() != "Aim")
                return;
            var guide = p.RangeAttackArm?.attackRangeObject;
            if (guide == null)
                return;
            dumps++;
            next = Time.realtimeSinceStartup + 2f;
            var text = new StringBuilder();
            Walk(guide.transform, 0, text);
            Plugin.Logger.LogInfo($"Guide while {who} aims the gun (arm out={p.RangeAttackArm.gameObject.activeInHierarchy}):\n{text}");
        }
        catch (Exception exception)
        {
            dumps = 3;
            Plugin.Logger.LogWarning($"Guide dump: {exception.Message}");
        }
    }

    private static void Walk(Transform t, int depth, StringBuilder text)
    {
        var renderer = t.GetComponent<SpriteRenderer>();
        var drawn = renderer == null ? "" : $" sprite={(renderer.sprite != null ? renderer.sprite.name : "none")} enabled={renderer.enabled} color={renderer.color} size={renderer.size} draw={renderer.drawMode}";
        text.AppendLine($"{new string(' ', depth * 2)}{t.name} active={t.gameObject.activeSelf} rotZ={t.localEulerAngles.z:F0} pos={t.localPosition} scale={t.localScale}{drawn}");
        if (depth > 4)
            return;
        for (var i = 0; i < t.childCount; i++)
            Walk(t.GetChild(i), depth + 1, text);
    }
}
