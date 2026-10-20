using System;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DaveTheDiverCoop;

// Grabbing fish (e.g. the sawtooth shark's bite-and-hold) lock onto their
// target diver, but the hold's damage, struggle and camera go to "the player".
// When the fish's target is player 2, run its hold code as player 2's turn.
internal static class GrabRedirect
{
    private static int _logs;

    internal static bool TargetsPlayer2(Transform target)
    {
        var p2 = SecondDiver.Player2;
        return p2 != null && target != null && (target.Pointer == p2.transform.Pointer || target.IsChildOf(p2.transform));
    }

    internal static bool HoldTargetsPlayer2(BiteHoldSkillRunner runner)
    {
        if (runner == null || SecondDiver.Player2 == null)
            return false;
        try
        {
            if (TargetsPlayer2(runner._cachedTarget))
                return true;
            var hasTarget = runner._hasTarget;
            if (hasTarget != null && TargetsPlayer2(hasTarget.Target))
                return true;
        }
        catch
        {
        }
        return false;
    }

    internal static bool Begin(bool forPlayer2, string what)
    {
        if (!forPlayer2)
            return false;
        SecondDiver.BeginPlayer2Turn(SecondDiver.Player2);
        if (_logs++ < 15)
            Plugin.Logger.LogInfo($"Grab: {what} running for player 2");
        return true;
    }

    internal static void End(bool began)
    {
        if (began && SecondDiver.Player2 != null)
            SecondDiver.EndPlayer2Turn(SecondDiver.Player2);
    }
}

[HarmonyPatch(typeof(BiteHoldSkillRunner), nameof(BiteHoldSkillRunner.FixedUpdate))]
internal static class GrabHoldFixedUpdate
{
    private static void Prefix(BiteHoldSkillRunner __instance, ref bool __state) =>
        __state = GrabRedirect.HoldTargetsPlayer2(__instance) && GrabRedirect.Begin(true, "hold update");
    private static void Postfix(bool __state) => GrabRedirect.End(__state);
}

[HarmonyPatch(typeof(BiteHoldSkillRunner), nameof(BiteHoldSkillRunner.AttachToTarget))]
internal static class GrabHoldAttach
{
    private static bool Prefix(BiteHoldSkillRunner __instance, Transform __0, ref bool __state)
    {
        __state = false;
        if (NoSharkGrab.Skip("bite-and-hold", NoSharkGrab.FishName(__instance), GrabRedirect.TargetsPlayer2(__0), __instance))
        {
            try { __instance.ForceRelease(); }
            catch { }
            return false;
        }
        __state = GrabRedirect.Begin(GrabRedirect.TargetsPlayer2(__0), "hold attach");
        return true;
    }
    private static void Postfix(bool __state) => GrabRedirect.End(__state);
}

[HarmonyPatch(typeof(BiteHoldSkillRunner), nameof(BiteHoldSkillRunner.ForceRelease))]
internal static class GrabHoldRelease
{
    private static void Prefix(BiteHoldSkillRunner __instance, ref bool __state) =>
        __state = GrabRedirect.Begin(GrabRedirect.HoldTargetsPlayer2(__instance), "hold release");
    private static void Postfix(bool __state) => GrabRedirect.End(__state);
}

[HarmonyPatch(typeof(BiteHoldSkillRunner), nameof(BiteHoldSkillRunner.OnDisable))]
internal static class GrabHoldDisable
{
    private static void Prefix(BiteHoldSkillRunner __instance, ref bool __state) =>
        __state = GrabRedirect.Begin(GrabRedirect.HoldTargetsPlayer2(__instance), "hold end");
    private static void Postfix(bool __state) => GrabRedirect.End(__state);
}

[HarmonyPatch(typeof(AttachQTESkillRunner), nameof(AttachQTESkillRunner.OnAttachQTE))]
internal static class GrabAttachQte
{
    private static bool Prefix(AttachQTESkillRunner __instance, ref bool __state)
    {
        __state = false;
        var hold = __instance.GetComponent<BiteHoldSkillRunner>() ?? __instance.GetComponentInParent<BiteHoldSkillRunner>();
        if (NoSharkGrab.Skip("attach struggle", NoSharkGrab.FishName(__instance),
                GrabRedirect.HoldTargetsPlayer2(hold) || NoSharkGrab.CloserToPlayer2(__instance), __instance))
            return false;
        __state = GrabRedirect.Begin(GrabRedirect.HoldTargetsPlayer2(hold), "struggle start");
        return true;
    }
    private static void Postfix(bool __state) => GrabRedirect.End(__state);
}

// Sharks that grab (sawtooth shark etc.) attach "the player" to their mouth
// with PlayerCharacter.AttachTransform. When the mouth is at player 2, attach
// player 2 instead, and run that fish's skill updates as player 2's turn so
// the hold's damage, struggle and release go to player 2.
internal static class GrabAttach
{
    private static Transform _p2AttachedTo;
    private static bool _p1Attached, _redirecting;
    internal static bool AnyoneHeld => _p1Attached || _p2AttachedTo != null;
    private static float _p2AttachedAt;
    private static int _logs, _fishLogs;
    private static readonly System.Collections.Generic.HashSet<IntPtr> _loggedFish = new();
    private static readonly System.Collections.Generic.Dictionary<IntPtr, (MonoBehaviour owner, SA.RuntimeSkillManager skills, int frame)> _fish = new();
    private static readonly System.Collections.Generic.Dictionary<IntPtr, float> _onP2Until = new();
    private static float _lastP2Hp = -1f;
    private static PlayerCharacter _focus;
    private static float _focusUntil;

    // The diver a fish is holding (or held a moment ago): the camera centres
    // on them while the game zooms in on the grab.
    internal static PlayerCharacter CameraFocus
    {
        get
        {
            if (_focus == null)
                return null;
            if (_p1Attached || _p2AttachedTo != null || Time.time < _focusUntil)
                return _focus;
            _focus = null;
            return null;
        }
    }

    private static void Focus(PlayerCharacter who, float linger)
    {
        _focus = who;
        _focusUntil = Time.time + linger;
    }
    private static readonly System.Collections.Generic.Dictionary<IntPtr, (DR.AI.SABaseFishSystem fish, float until)> _marked = new();

    // Called around every attacking fish's Update: a fish after player 2
    // thinks player 2 is "the player".
    internal static bool FishUpdateForPlayer2(DR.AI.SABaseFishSystem fish)
    {
        // Off: running a fish as player 2's turn made its attacks on player 1
        // misfire. Fish now behave as in the base game.
        if (!Experimental)
            return false;
        var p2 = SecondDiver.Player2;
        if (p2 == null || fish == null || _marked.Count == 0 || SecondDiver.InPlayer2Turn)
            return false;
        if (!_marked.TryGetValue(fish.Pointer, out var entry))
            return false;
        var holding = _p2AttachedTo != null && _p2AttachedTo.IsChildOf(fish.transform);
        if (entry.until < Time.time && !holding)
        {
            _marked.Remove(fish.Pointer);
            Plugin.Logger.LogInfo($"Grab: {fish.name} no longer after player 2");
            return false;
        }
        try
        {
            var target = fish.Target;
            if (target == null || target.Pointer != p2.transform.Pointer)
                fish.Target = p2.transform;
        }
        catch
        {
        }
        if (_fishLogs < 20 && _loggedFish.Add(fish.Pointer))
        {
            _fishLogs++;
            Plugin.Logger.LogInfo($"Grab: {fish.name} runs as player 2's turn (after player 2)");
        }
        SecondDiver.BeginPlayer2Turn(p2);
        return true;
    }

    // A fish that just hurt player 2 is after player 2: find the nearest fish
    // when player 2's oxygen drops sharply, and point it at player 2.
    internal static bool Experimental = false;

    // A diver held by a creature that is then killed or removed (P2 harpoons
    // the shark holding P1, or it despawns) never gets the game's own release:
    // release them ourselves, unlock them and bring the HUD back.
    private static Transform _p1AttachedTo;
    private static float _p1GoneSince = -1f, _p2GoneSince = -1f;
    // The creature that last grabbed player 1. It is kept after the grab is
    // released: some grabs (whitetip bite hold) release the attach but leave
    // player 1 locked with the HUD hidden.
    private static Transform _p1Grabber;
    private static float _p1DetachedAt = -1f, _p1GrabberGoneSince = -1f;

    // The creature itself (not the mouth bone, which can switch off for a
    // moment during the bite animation) must be destroyed or switched off,
    // and stay that way for half a second.
    private static bool CreatureGone(Transform point, ref float since)
    {
        bool gone;
        if ((object)point == null)
            gone = false;
        else if (point == null)
            gone = true; // destroyed
        else
        {
            var fish = point.GetComponentInParent<DR.AI.FishAISystem>(true);
            gone = fish == null ? !point.root.gameObject.activeInHierarchy : !fish.gameObject.activeInHierarchy;
        }
        if (!gone)
        {
            since = -1f;
            return false;
        }
        if (since < 0f)
            since = Time.time;
        return Time.time - since > 0.5f;
    }

    private static void Watchdog()
    {
        void Free(PlayerCharacter diver, string who)
        {
            if (diver == null)
                return;
            try { diver.ForceReleaseCommonQTE(); } catch { }
            _redirecting = true;
            try { diver.DetachFollowing(); } catch { }
            finally { _redirecting = false; }
            try { if (diver.IsMoveLocked) diver.UnlockMove(); } catch { }
            try { if (diver.IsActionLock) diver.SetActionLock(false); } catch { }
            CoopBehaviour.ResetHud();
            CoopTest.Record($"watchdog freed {who} (creature gone mid-grab)");
            Plugin.Logger.LogInfo($"Grab watchdog: the creature holding {who} is gone; released and unlocked {who}");
        }
        if (_p1Attached && CreatureGone(_p1AttachedTo, ref _p1GoneSince))
        {
            _p1Attached = false;
            _p1AttachedTo = null;
            _p1GoneSince = -1f;
            Free(SecondDiver.Player1, "player 1");
        }
        // After a player 1 grab is released: unlock once the creature is gone,
        // or 6 s after the release if player 1 is still locked.
        if ((object)_p1Grabber != null && !_p1Attached)
        {
            var p1 = SecondDiver.Player1;
            if (_p1DetachedAt < 0f)
                _p1DetachedAt = Time.time;
            bool locked;
            try { locked = p1 != null && (p1.IsActionLock || p1.IsMoveLocked || !(UnityEngine.Object.FindFirstObjectByType<MainCanvasManager>()?.IsHudOn ?? true)); }
            catch { locked = p1 != null; }
            var since = Time.time - _p1DetachedAt;
            if (!locked && since > 1f)
            {
                _p1Grabber = null;
                _p1GrabberGoneSince = -1f;
            }
            else if (locked && (CreatureGone(_p1Grabber, ref _p1GrabberGoneSince) || since > 6f))
            {
                _p1Grabber = null;
                _p1GrabberGoneSince = -1f;
                Free(p1, "player 1");
            }
        }
        if (CreatureGone(_p2AttachedTo, ref _p2GoneSince))
        {
            _p2AttachedTo = null;
            _p2GoneSince = -1f;
            Free(SecondDiver.Player2, "player 2");
        }
    }

    internal static void Tick()
    {
        try { Watchdog(); } catch { }
        var p2 = SecondDiver.Player2;
        if (p2 == null || !Experimental)
        {
            _lastP2Hp = -1f;
            return;
        }
        float hp;
        try { hp = p2.BreathHandler.HP; }
        catch { return; }
        var drop = _lastP2Hp < 0f ? 0f : _lastP2Hp - hp;
        _lastP2Hp = hp;
        if (drop < 6f)
            return;
        DR.AI.SABaseFishSystem best = null;
        var bestDistance = 9f;
        var all = Object.FindObjectsByType<DR.AI.SABaseFishSystem>(FindObjectsSortMode.None);
        foreach (var fish in all)
        {
            if (fish == null || !fish.isActiveAndEnabled)
                continue;
            var d = Vector2.Distance(fish.transform.position, p2.transform.position);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = fish;
            }
        }
        if (best == null)
        {
            Plugin.Logger.LogInfo($"Grab: player 2 lost {drop:F0} oxygen, no attacking fish nearby ({all.Length} in level)");
            return;
        }
        _marked[best.Pointer] = (best, Time.time + 12f);
        try { best.Target = p2.transform; }
        catch (Exception exception) { Plugin.Logger.LogWarning($"Grab: retarget {best.name}: {exception.Message}"); }
        Plugin.Logger.LogInfo($"Grab: {best.name} hit player 2 ({drop:F0} oxygen, {bestDistance:F1} away); it now goes after player 2");
    }

    // Is this creature (or the fish it belongs to) going after player 2?
    internal static bool FishOnP2(Component c)
    {
        var p2 = SecondDiver.Player2;
        if (c == null || p2 == null)
            return false;
        try
        {
            var fish = c.GetComponentInParent<DR.AI.SABaseFishSystem>();
            if (fish == null)
                return false;
            if (_marked.TryGetValue(fish.Pointer, out var entry) && entry.until >= Time.time)
                return true;
            var target = fish.Target;
            return target != null && (target.Pointer == p2.transform.Pointer || target.IsChildOf(p2.transform));
        }
        catch
        {
            return false;
        }
    }

    private static bool MarkedOnP2(Transform t)
    {
        if (t == null)
            return false;
        foreach (var pair in _marked)
            if (pair.Value.until >= Time.time && pair.Value.fish != null && t.IsChildOf(pair.Value.fish.transform))
                return true;
        return false;
    }

    internal static void Clear()
    {
        _p2AttachedTo = null;
        _p1Attached = false;
        _p1AttachedTo = null;
        _p1Grabber = null;
        _loggedFish.Clear();
        _fish.Clear();
        _onP2Until.Clear();
        _marked.Clear();
        _focus = null;
        _lastP2Hp = -1f;
    }

    private static string Describe(Transform t)
    {
        if (t == null)
            return "none";
        var path = t.name;
        var p = t.parent;
        for (var i = 0; i < 4 && p != null; i++, p = p.parent)
            path = p.name + "/" + path;
        return path;
    }

    internal static bool OnAttach(PlayerCharacter who, Transform point, float a1, Il2CppSystem.Func<bool> a2, bool a3,
        AniClipEvents.PlayerFollowTargetBone.AttatchType a4, bool a5, bool a6, float a7, bool a8, ref Il2CppSystem.IDisposable result)
    {
        if (_redirecting)
            return true;
        var p1 = SecondDiver.Player1;
        var p2 = SecondDiver.Player2;
        try
        {
            var onP2 = p2 != null && who != null && who.Pointer == p2.Pointer;
            if (!onP2 && p1 != null && p2 != null && point != null)
                onP2 = Vector2.Distance(point.position, p2.transform.position) < Vector2.Distance(point.position, p1.transform.position);
            if (NoSharkGrab.Skip("attach to mouth", NoSharkGrab.FishName(point), onP2, point))
            {
                result = null;
                return false;
            }
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning($"Grab check: {exception.Message}");
        }
        try
        {
            var d1 = p1 != null && point != null ? Vector2.Distance(point.position, p1.transform.position) : -1f;
            var d2 = p2 != null && point != null ? Vector2.Distance(point.position, p2.transform.position) : -1f;
            var isP1 = p1 != null && who != null && who.Pointer == p1.Pointer;
            CoopTest.Record($"attach to mouth: {(isP1 ? "P1" : "P2")}");
            if (_logs++ < 30)
                Plugin.Logger.LogInfo(
                    $"Grab attach: {(isP1 ? "player 1" : "player 2")} to {Describe(point)} type={a4} p2Turn={SecondDiver.InPlayer2Turn} " +
                    $"distance to P1={d1:F2} P2={d2:F2} fishAfterP2={MarkedOnP2(point)}");
            var marked = MarkedOnP2(point);
            if (!isP1 || p2 == null || SecondDiver.InPlayer2Turn || d2 < 0f || d1 < 0f || (d2 >= d1 && !marked))
            {
                Focus(who, 30f);
                if (isP1)
                {
                    _p1Attached = true;
                    _p1AttachedTo = point;
                    try { _p1Grabber = point.GetComponentInParent<DR.AI.FishAISystem>(true)?.transform ?? point.root; }
                    catch { _p1Grabber = point; }
                    _p1DetachedAt = -1f;
                }
                else
                {
                    _p2AttachedTo = point;
                    _p2AttachedAt = Time.time;
                }
                return true;
            }
            // The mouth is at player 2: player 2 is the one grabbed.
            _redirecting = true;
            SecondDiver.BeginPlayer2Turn(p2);
            try
            {
                result = p2.AttachTransform(point, a1, a2, a3, a4, a5, a6, a7, a8);
            }
            finally
            {
                SecondDiver.EndPlayer2Turn(p2);
                _redirecting = false;
            }
            _p2AttachedTo = point;
            _p2AttachedAt = Time.time;
            Focus(p2, 30f);
            Plugin.Logger.LogInfo($"Grab attach: moved to player 2 ({Describe(point)})");
            return false;
        }
        catch (Exception exception)
        {
            _redirecting = false;
            Plugin.Logger.LogWarning($"Grab attach redirect: {exception.Message}");
            return true;
        }
    }

    // Release: the game releases "the player"; if player 2 is the one held
    // (and player 1 isn't), release player 2.
    internal static bool OnDetach(PlayerCharacter who)
    {
        if (_redirecting)
            return true;
        var p1 = SecondDiver.Player1;
        var p2 = SecondDiver.Player2;
        var isP1 = p1 != null && who != null && who.Pointer == p1.Pointer;
        if (_focus != null)
            _focusUntil = Time.time + 3f;
        if (_logs++ < 30)
            Plugin.Logger.LogInfo($"Grab release: called on {(isP1 ? "player 1" : "player 2")} p2Held={_p2AttachedTo != null} p1Held={_p1Attached}");
        if (!isP1)
        {
            _p2AttachedTo = null;
            return true;
        }
        if (_p1Attached || _p2AttachedTo == null || p2 == null)
        {
            _p1Attached = false;
            _p1AttachedTo = null;
        _p1AttachedTo = null;
            return true;
        }
        _redirecting = true;
        SecondDiver.BeginPlayer2Turn(p2);
        try
        {
            p2.DetachFollowing();
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning($"Grab release redirect: {exception.Message}");
        }
        finally
        {
            SecondDiver.EndPlayer2Turn(p2);
            _redirecting = false;
        }
        _p2AttachedTo = null;
        Plugin.Logger.LogInfo("Grab release: moved to player 2");
        return false;
    }

    // A fish's skills run as player 2's turn while it holds player 2 or is
    // hunting player 2.
    internal static bool FishOnPlayer2(SA.RuntimeSkillManager skills)
    {
        if (!Experimental)
            return false;
        var p2 = SecondDiver.Player2;
        if (p2 == null || skills == null || SecondDiver.InPlayer2Turn)
            return false;
        try
        {
            var owner = skills.GetOwner;
            if (owner == null)
                return false;
            _fish[owner.Pointer] = (owner, skills, Time.frameCount);
            var marked = _onP2Until.TryGetValue(owner.Pointer, out var until) && until >= Time.time;
            if (marked)
            {
                var t = skills.GetHasTarget;
                if (t != null && (t.Target == null || t.Target.Pointer != p2.transform.Pointer))
                    t.Target = p2.transform;
            }
            var holding = _p2AttachedTo != null && _p2AttachedTo.IsChildOf(owner.transform);
            var target = skills.GetHasTarget?.Target;
            var hunting = marked || target != null && (target.Pointer == p2.transform.Pointer || target.IsChildOf(p2.transform));
            if ((holding || hunting) && _fishLogs < 20 && _loggedFish.Add(owner.Pointer))
            {
                _fishLogs++;
                Plugin.Logger.LogInfo($"Grab: {owner.name} ({owner.GetIl2CppType().Name}) {(holding ? "holds" : "targets")} player 2; its skills run as player 2's turn");
            }
            return holding || hunting;
        }
        catch
        {
            return false;
        }
    }
}

[HarmonyPatch(typeof(PlayerCharacter), nameof(PlayerCharacter.AttachTransform))]
internal static class GrabAttachTransform
{
    private static bool Prefix(PlayerCharacter __instance, Transform __0, float __1, Il2CppSystem.Func<bool> __2, bool __3,
        AniClipEvents.PlayerFollowTargetBone.AttatchType __4, bool __5, bool __6, float __7, bool __8, ref Il2CppSystem.IDisposable __result) =>
        GrabAttach.OnAttach(__instance, __0, __1, __2, __3, __4, __5, __6, __7, __8, ref __result);
}

[HarmonyPatch(typeof(PlayerCharacter), nameof(PlayerCharacter.DetachFollowing))]
internal static class GrabDetachFollowing
{
    private static bool Prefix(PlayerCharacter __instance) => GrabAttach.OnDetach(__instance);
}

[HarmonyPatch(typeof(SA.RuntimeSkillManager), nameof(SA.RuntimeSkillManager.Update))]
internal static class GrabFishSkills
{
    private static void Prefix(SA.RuntimeSkillManager __instance, ref bool __state) =>
        __state = GrabAttach.FishOnPlayer2(__instance) && GrabRedirect.Begin(true, "fish skills");
    private static void Postfix(bool __state) => GrabRedirect.End(__state);
}

[HarmonyPatch(typeof(DR.AI.SABaseFishSystem), nameof(DR.AI.SABaseFishSystem.Update))]
internal static class GrabFishUpdate
{
    private static void Prefix(DR.AI.SABaseFishSystem __instance, ref bool __state) =>
        __state = GrabAttach.FishUpdateForPlayer2(__instance);
    private static void Postfix(bool __state) => GrabRedirect.End(__state);
}

// Diagnostics: who gets locked / unlocked (a grab on player 1 left both
// divers unable to act).
[HarmonyPatch(typeof(PlayerCharacter), nameof(PlayerCharacter.SetActionLock))]
internal static class TraceActionLock
{
    private static int _logs;

    private static void Postfix(PlayerCharacter __instance, bool __0)
    {
        if (SecondDiver.Player2 == null || _logs >= 60)
            return;
        _logs++;
        var p2 = SecondDiver.Player2;
        var who = __instance != null && __instance.Pointer == p2.Pointer ? "player 2" : "player 1";
        Plugin.Logger.LogInfo($"Action lock {(__0 ? "ON" : "off")} on {who} (p2Turn={SecondDiver.InPlayer2Turn}) -> P1 locked={SecondDiver.Player1?.IsActionLock} P2 locked={p2.IsActionLock}");
    }
}
