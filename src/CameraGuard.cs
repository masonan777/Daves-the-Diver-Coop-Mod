using HarmonyLib;
using UnityEngine;

namespace DaveTheDiverCoop;

// Some game events (like landing a catch) tell the camera to "go back to the
// player", sometimes snapping instantly. With two divers that makes the view
// jump to one of them and then slide back to the middle. While the co-op
// camera is following, send those requests to the midpoint instead, and turn
// instant snaps into normal movement.
internal static class CameraGuard
{
    private static int _logs;

    internal static bool IsDiver(Transform target)
    {
        if (target == null)
            return false;
        var p1 = SecondDiver.Player1;
        var p2 = SecondDiver.Player2;
        return (p1 != null && target.Pointer == p1.transform.Pointer) ||
               (p2 != null && (target.Pointer == p2.transform.Pointer || target.IsChildOf(p2.transform)));
    }

    // Returns true when the request was redirected to the midpoint.
    internal static bool Redirect(string what, ref Transform target)
    {
        var anchor = CoopCamera.AnchorTransform;
        var redirect = SecondDiver.Player2 != null && CoopCamera.Following && anchor != null && IsDiver(target);
        Log(what, target, redirect);
        if (redirect)
            target = anchor;
        return redirect;
    }

    internal static void Log(string what, Transform target, bool redirected)
    {
        if (SecondDiver.Player2 == null || _logs++ > 40)
            return;
        Plugin.Logger.LogInfo($"Camera request: {what} target={target?.name ?? "none"} player2Turn={SecondDiver.InPlayer2Turn} {(redirected ? "-> midpoint" : "")} (frame {Time.frameCount})");
    }
}

[HarmonyPatch(typeof(global::CameraManager), nameof(global::CameraManager.ChangeTarget))]
internal static class CameraGuardChangeTarget
{
    private static void Prefix(ref Transform __0)
    {
        if (CoopCamera.AnchorTransform != null && __0 != null && __0.Pointer == CoopCamera.AnchorTransform.Pointer)
            return; // the mod's own call
        CameraGuard.Redirect("change target", ref __0);
    }
}

[HarmonyPatch(typeof(global::CameraManager), nameof(global::CameraManager.ChangeTargetReset))]
internal static class CameraGuardChangeTargetReset
{
    private static bool Prefix(global::CameraManager __instance, ref Transform __0)
    {
        if (!CameraGuard.Redirect("change target + snap", ref __0))
            return true;
        __instance.ChangeTarget(__0); // follow the midpoint without snapping
        return false;
    }
}

[HarmonyPatch(typeof(global::CameraManager), nameof(global::CameraManager.ChangeTargetAndMoveSpeed))]
internal static class CameraGuardChangeTargetSpeed
{
    private static void Prefix(ref Transform __0) => CameraGuard.Redirect("change target + speed", ref __0);
}

[HarmonyPatch(typeof(global::CameraManager), nameof(global::CameraManager.EndDummyCameraTarget))]
internal static class CameraGuardEndDummy
{
    private static void Prefix(ref Transform __0) => CameraGuard.Redirect("end temporary target", ref __0);
}

[HarmonyPatch(typeof(global::CameraManager), nameof(global::CameraManager.SetCameraPositionToTarget))]
internal static class CameraGuardSetPosition
{
    private static bool Prefix(Transform __0)
    {
        var skip = SecondDiver.Player2 != null && CoopCamera.Following && CameraGuard.IsDiver(__0);
        CameraGuard.Log("move camera to target", __0, skip);
        return !skip;
    }
}

[HarmonyPatch(typeof(global::CameraManager), nameof(global::CameraManager.InstantlyMoveTo))]
internal static class CameraGuardInstantMove
{
    private static bool Prefix(Vector2 __0)
    {
        var skip = SecondDiver.Player2 != null && CoopCamera.Following;
        CameraGuard.Log($"instant move to {__0}", null, skip);
        return !skip;
    }
}

[HarmonyPatch(typeof(global::CameraManager), nameof(global::CameraManager.ForceUpdateCamera))]
internal static class CameraGuardForceUpdate
{
    private static void Prefix() => CameraGuard.Log("force update", null, false);
}

[HarmonyPatch(typeof(global::CameraManager), nameof(global::CameraManager.AddSecondTarget))]
internal static class CameraGuardAddSecond
{
    private static void Prefix(Transform __0) => CameraGuard.Log("add second target", __0, false);
}

[HarmonyPatch(typeof(global::CameraManager), nameof(global::CameraManager.Shake), new[] { typeof(float), typeof(float), typeof(float), typeof(bool) })]
internal static class CameraGuardShake
{
    private static void Prefix() { HitTrace.Gated("enter camera shake"); CameraGuard.Log("shake", null, false); }
    private static void Postfix() => HitTrace.Gated("leave camera shake");
}

// The game's camera step would otherwise drag the view toward its own stale
// target (where player 1 was); undo it right after it runs.
[HarmonyPatch(typeof(global::CameraManager), nameof(global::CameraManager.CameraUpdate_Impl))]
internal static class CameraHoldAfterUpdate
{
    private static void Postfix() => CoopCamera.HoldPosition();
}

[HarmonyPatch(typeof(global::CameraManager), nameof(global::CameraManager.FixedUpdate))]
internal static class CameraHoldAfterFixedUpdate
{
    private static void Postfix() => CoopCamera.HoldPosition();
}

[HarmonyPatch(typeof(global::CameraManager), nameof(global::CameraManager.RemoveSecondTarget))]
internal static class CameraGuardRemoveSecond
{
    private static void Prefix() => CameraGuard.Log("remove second target", null, false);
}
