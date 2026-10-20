using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace DaveTheDiverCoop;

// Player 2's short dash (Comma). Dave's short dash is an "ability": the ability
// component hands Dave three callbacks (start, trigger, finish) and keeps a
// cooldown. Check that player 2's callbacks point at player 2's own ability
// and repair them if not; log the state on each press.
internal static class ShortDashProbe
{
    private static int _logs;
    private static float _followUpAt = -1f;
    private static bool _repaired;
    private static bool _ctrlWasDown;
    private static int _p1Logs;

    internal static void Reset() => _repaired = false;

    private static string Target(Il2CppSystem.Delegate action, ShortDashAbility mine, ShortDashAbility other)
    {
        if (action == null)
            return "none";
        try
        {
            var target = action.Target;
            if (target == null)
                return "static";
            if (mine != null && target.Pointer == mine.Pointer)
                return "own";
            if (other != null && target.Pointer == other.Pointer)
                return "OTHER DIVER";
            return target.GetIl2CppType().Name;
        }
        catch (Exception exception)
        {
            return "?" + exception.Message;
        }
    }

    // Raw values the game's short-dash check reads (offsets from the game code).
    internal static string Raw(PlayerCharacter p)
    {
        if (p == null)
            return "none";
        try
        {
            var b = p.Pointer;
            string TypeAt(int offset)
            {
                var ptr = Marshal.ReadIntPtr(b, offset);
                if (ptr == IntPtr.Zero)
                    return "null";
                return new Il2CppSystem.Object(ptr).GetIl2CppType().Name;
            }
            var settings = Marshal.ReadIntPtr(b, 0x78);
            var threshold = settings == IntPtr.Zero ? float.NaN : BitConverter.Int32BitsToSingle(Marshal.ReadInt32(settings, 0x24));
            float F(int o) => BitConverter.Int32BitsToSingle(Marshal.ReadInt32(b, o));
            var act = Marshal.ReadIntPtr(b, 0x1d8);
            var actTarget = "none";
            if (act != IntPtr.Zero)
            {
                var target = new Il2CppSystem.Delegate(act).Target;
                if (target == null) actTarget = "static";
                else if (target.Pointer == p.Pointer) actTarget = "this diver";
                else if (SecondDiver.Player1 != null && target.Pointer == SecondDiver.Player1.Pointer) actTarget = "PLAYER 1";
                else if (SecondDiver.Player2 != null && target.Pointer == SecondDiver.Player2.Pointer) actTarget = "PLAYER 2";
                else actTarget = target.GetIl2CppType().Name;
            }
            return $"actionTarget={actTarget} i358={Marshal.ReadInt32(b, 0x358)} b168={Marshal.ReadByte(b, 0x168)} b3a0={Marshal.ReadByte(b, 0x3a0)} i1b0={Marshal.ReadInt32(b, 0x1b0)} " +
                   $"f1cc={F(0x1cc):F2} v208=({F(0x208):F2},{F(0x20c):F2}) b394={Marshal.ReadByte(b, 0x394)} threshold={threshold:F2} " +
                   $"action1d8={TypeAt(0x1d8)} c0={TypeAt(0xc0)} c8={TypeAt(0xc8)} subHelperUsing={p.IsSubHelperUsing}";
        }
        catch (Exception exception)
        {
            return "raw read failed: " + exception.Message;
        }
    }

    internal static string Describe(PlayerCharacter who, PlayerCharacter other)
    {
        var mine = who?.GetComponent<ShortDashAbility>();
        var theirs = other?.GetComponent<ShortDashAbility>();
        string cooldown;
        try
        {
            var cd = mine?.dashCooldown;
            cooldown = cd == null ? "none" : $"active={cd.IsActive} timer={cd.Timer:F2}/{cd.Duration:F2}";
        }
        catch (Exception exception)
        {
            cooldown = "?" + exception.Message;
        }
        return $"data={(mine?.OwnData != null)} start={Target(who?.ShortDashAbilityAction, mine, theirs)} " +
               $"trigger={Target(who?.ShortDashAbilityTriggerAction, mine, theirs)} finish={Target(who?.ShortDashAbilityFinishAction, mine, theirs)} " +
               $"cooldown[{cooldown}] locomotion={who?.IsLocomotion} state={who?.CurrentSubSMBState}";
    }

    internal static string Clip(PlayerCharacter p)
    {
        try
        {
            var clips = p.characterAnimator.GetCurrentAnimatorClipInfo(0);
            return clips != null && clips.Length > 0 && clips[0].clip != null ? clips[0].clip.name : "(none)";
        }
        catch
        {
            return "?";
        }
    }

    internal static void Tick(PlayerCharacter p1, PlayerCharacter p2)
    {
        if (p1 == null || p2 == null)
            return;
        try
        {
            if (!_repaired)
            {
                _repaired = true;
                Repair(p1, p2);
            }
            var ctrl = P2Keys.IsDown(0xA2);
            if (ctrl && !_ctrlWasDown && _p1Logs++ < 6)
                Plugin.Logger.LogInfo($"P1 short dash (Left Ctrl) for comparison: raw P1: {Raw(p1)}");
            _ctrlWasDown = ctrl;
            if (P2Keys.Pressed(P2Keys.Action.ShortDash) && _logs++ < 12)
            {
                Plugin.Logger.LogInfo($"P2 short dash pressed: P2[{Describe(p2, p1)}] | P1[{Describe(p1, p2)}]");
                Plugin.Logger.LogInfo($"   raw P2: {Raw(p2)}");
                Plugin.Logger.LogInfo($"   raw P1: {Raw(p1)}");
                _followUpAt = Time.realtimeSinceStartup + 0.3f;
            }
            else if (_followUpAt > 0f && Time.realtimeSinceStartup >= _followUpAt)
            {
                _followUpAt = -1f;
                Plugin.Logger.LogInfo($"P2 short dash 0.3 s later: P2[{Describe(p2, p1)}] clip={Clip(p2)} | P1 cooldown[{Describe(p1, p2)}]");
            }
        }
        catch (Exception exception)
        {
            if (_logs++ < 12)
                Plugin.Logger.LogWarning($"Short dash probe: {exception.Message}");
        }
    }

    // If player 2's callbacks are missing or point at player 1's ability,
    // re-run player 2's ability setup as player 2.
    private static void Repair(PlayerCharacter p1, PlayerCharacter p2)
    {
        var mine = p2.GetComponent<ShortDashAbility>();
        var theirs = p1.GetComponent<ShortDashAbility>();
        var before = Describe(p2, p1);
        var p1Before = Describe(p1, p2);
        var start = Target(p2.ShortDashAbilityAction, mine, theirs);
        if (mine == null || start == "own")
        {
            Plugin.Logger.LogInfo($"P2 short dash looks connected: P2[{before}] | P1[{p1Before}]");
            return;
        }
        var data = (mine.OwnData ?? theirs?.OwnData)?.TryCast<CharacterAbilityData>();
        if (data == null)
        {
            Plugin.Logger.LogWarning($"P2 short dash: no ability data to set up from. P2[{before}]");
            return;
        }
        SecondDiver.BeginPlayer2Turn(p2);
        try
        {
            mine.SetData(data);
        }
        finally
        {
            SecondDiver.EndPlayer2Turn(p2);
        }
        Plugin.Logger.LogInfo($"P2 short dash re-connected: before P2[{before}] after P2[{Describe(p2, p1)}] | P1[{Describe(p1, p2)}]");
    }
}
