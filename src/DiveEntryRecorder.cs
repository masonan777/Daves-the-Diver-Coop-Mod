using System;
using UnityEngine;

namespace DaveTheDiverCoop;

// Diagnostics: when a dive starts, log which animations player 1 plays while
// jumping in (and where he is), so player 2's join can play the same entry.
internal static class DiveEntryRecorder
{
    private static IntPtr _diver;
    private static float _until;
    private static string _lastClip;
    private static float _startedAt;

    internal static void Tick()
    {
        PlayerCharacter p1;
        try
        {
            p1 = InGameManager.TryGetPlayerCharacter;
        }
        catch
        {
            return;
        }
        if (p1 == null || SecondDiver.IsPlayer2(p1))
            return;
        if (p1.Pointer != _diver)
        {
            _diver = p1.Pointer;
            _startedAt = Time.realtimeSinceStartup;
            _until = _startedAt + 20f;
            _lastClip = null;
            Plugin.Logger.LogInfo("DiveEntry: new player 1 diver seen, recording its first 20 s");
        }
        if (Time.realtimeSinceStartup > _until)
            return;
        try
        {
            var animator = p1.characterAnimator;
            if (animator == null)
                return;
            var clips = animator.GetCurrentAnimatorClipInfo(0);
            var clip = clips != null && clips.Length > 0 && clips[0].clip != null ? clips[0].clip.name : "(none)";
            var state = animator.GetCurrentAnimatorStateInfo(0);
            var key = clip + "/" + state.fullPathHash;
            if (key == _lastClip)
                return;
            _lastClip = key;
            var body = p1.GetComponent<Rigidbody2D>();
            Plugin.Logger.LogInfo(
                $"DiveEntry +{Time.realtimeSinceStartup - _startedAt:F2}s clip={clip} len={(clips != null && clips.Length > 0 && clips[0].clip != null ? clips[0].clip.length : 0f):F2} " +
                $"state={state.fullPathHash} short={state.shortNameHash} t={state.normalizedTime:F2} pos={p1.transform.position} " +
                $"body={body?.bodyType} animSpeed={animator.speed} moveLocked={p1.IsMoveLocked} location={p1.locationState} sub={p1.CurrentSubSMBState}");
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning($"DiveEntry: {exception.Message}");
            _until = 0f;
        }
    }
}
