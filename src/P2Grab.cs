using System;
using BepInEx.Configuration;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DaveTheDiverCoop;

// A real grab for player 2, run by the mod.
//
// The game's grab sequences (pull into the mouth, struggle prompt, mash bar)
// only work on "the player", and running them on player 2 freezes player 1's
// controls and hides the HUD. So the game's grab on player 2 is still skipped
// (NoSharkGrab), and this does the grab instead: player 2 is held in the
// creature's mouth, loses oxygen, and mashes A / B (Enter / Backspace) to
// break free. Player 1 keeps playing and can harpoon the creature to free
// player 2.
internal static class P2Grab
{
    private static ConfigEntry<bool> _enabled;
    private static Transform _mouth, _creature;
    private static string _creatureName;
    private static float _since, _progress, _immuneUntil, _lastPress;
    private static int _presses;
    private static GUIStyle _style;
    private static bool _gameBar;

    // Presses needed to break free, oxygen lost per second while held, and
    // the longest a hold lasts.
    private const int PressesNeeded = 12;
    private const float DrainPerSecond = 0.03f; // of the full tank
    private const float MaxHold = 8f;

    internal static bool Held => (object)_creature != null;
    internal static bool Enabled => _enabled != null && _enabled.Value;

    // The creature's own result triggers (from its mash prompt), so it plays
    // its "shaken off" animation when player 2 breaks free.
    private static Animator _resultAnimator;
    private static string _successTrigger, _failTrigger;

    internal static void SetResult(Animator animator, string success, string fail)
    {
        _resultAnimator = animator;
        _successTrigger = success;
        _failTrigger = fail;
    }

    internal static bool HoldsBy(Component c)
    {
        if (!Held || c == null)
            return false;
        try
        {
            var t = c.GetComponentInParent<DR.AI.FishAISystem>(true)?.transform ?? c.transform.root;
            return t != null && t.Pointer == _creature.Pointer;
        }
        catch { return false; }
    }

    private static void PlayResult(bool escaped)
    {
        try
        {
            var trigger = escaped ? _successTrigger : _failTrigger;
            if (_resultAnimator != null && !string.IsNullOrEmpty(trigger))
            {
                _resultAnimator.SetTrigger(trigger);
                Plugin.Logger.LogInfo($"P2 grab: creature plays '{trigger}'");
            }
        }
        catch { }
        // Biting creatures (whitetip...): do what the game's own mash result
        // does (Biteable.OpenQTE result callback, from the Cpp2IL dump): send
        // the fish its result transition, then end the bite. Without the
        // transition the fish stays in its bite/charge animation.
        // BiteSkillEnd clears "the player's" QTE state, so run it as player 2.
        try
        {
            var bite = _creature != null ? _creature.GetComponentInChildren<DR.AI.Biteable>(true) : null;
            if (bite != null)
            {
                var fish = bite.AISystem;
                if (fish != null)
                {
                    fish.SetTrigger(escaped
                        ? FishAnimatorTransitionController.Transition.UserQTESucces0
                        : FishAnimatorTransitionController.Transition.UserQTEFail0);
                    Plugin.Logger.LogInfo($"P2 grab: {fish.name} sent {(escaped ? "UserQTESucces0" : "UserQTEFail0")}");
                }
                var p2 = SecondDiver.Player2;
                SecondDiver.BeginPlayer2Turn(p2);
                try { bite.BiteSkillEnd(); }
                finally { SecondDiver.EndPlayer2Turn(p2); }
            }
        }
        catch (Exception exception) { Plugin.Logger.LogInfo($"P2 grab: bite end failed ({exception.Message})"); }
        _resultAnimator = null;
        _successTrigger = _failTrigger = null;
    }

    private static bool _animOn;

    internal static void Bind(ConfigFile config)
    {
        _enabled = config.Bind("Coop", "Player2Grabs", true,
            "Sharks can grab player 2 too (player 2 mashes A / B to break free). Off = grabs aimed at player 2 are skipped.");
    }

    // The game started a grab struggle on player 1 with this animation.
    internal static void LearnAnimation(string name)
    {
    }

    // Player 2 struggles like player 1 does. The game's start of a struggle
    // also hides the HUD, so bring it straight back.
    private static void StartStruggle(PlayerCharacter p2)
    {
        // The game passes the grabbing creature's name here (it isn't an
        // animation name); the struggle pose itself is fixed.
        _animOn = false;
        var name = _creatureName;
        try
        {
            P2Struggle.Ours = true;
            p2.CommonQTEStart(name);
            _animOn = true;
        }
        catch (Exception exception) { Plugin.Logger.LogInfo($"P2 grab: struggle animation failed ({exception.Message})"); }
        finally { P2Struggle.Ours = false; }
        CoopBehaviour.ResetHud();
    }

    private static void EndStruggle(PlayerCharacter p2)
    {
        if (!_animOn || p2 == null)
            return;
        _animOn = false;
        try
        {
            P2Struggle.Ours = true;
            p2.CommonQTEEnd();
        }
        catch (Exception exception) { Plugin.Logger.LogInfo($"P2 grab: struggle end failed ({exception.Message})"); }
        finally { P2Struggle.Ours = false; }
        CoopBehaviour.ResetHud();
    }

    // A grab step aimed at player 2 was just skipped: hold player 2 instead.
    internal static void TryBegin(Component source, string step)
    {
        var p2 = SecondDiver.Player2;
        if (_enabled == null || !_enabled.Value || p2 == null || source == null || Held || Time.time < _immuneUntil)
            return;
        Transform creature;
        try { creature = source.GetComponentInParent<DR.AI.FishAISystem>(true)?.transform ?? source.transform.root; }
        catch { creature = source.transform.root; }
        if (creature == null)
            return;
        _creature = creature;
        _creatureName = creature.name;
        _mouth = FindMouth(source.transform, creature);
        _since = Time.time;
        _progress = 0f;
        _presses = 0;
        _gameBar = P2MashUI.Show();
        StartStruggle(p2);
        CoopTest.Record($"P2 held by {_creatureName} (mod grab, from '{step}')");
        Plugin.Logger.LogInfo($"P2 grab: {_creatureName} holds player 2 ({step}); mouth={_mouth?.name}");
    }

    private static Transform FindMouth(Transform source, Transform creature)
    {
        if (source != null && source.name == "Target")
            return source;
        try
        {
            Transform fallback = null;
            foreach (var t in creature.GetComponentsInChildren<Transform>(true))
            {
                var n = t.name;
                if (n == "Target" && t.parent != null && t.parent.name.Contains("Head"))
                    return t;
                if (fallback == null && (n.Contains("Mouth") || n.Contains("mouth") || n.Contains("Jaw")))
                    fallback = t;
            }
            if (fallback != null)
                return fallback;
        }
        catch
        {
        }
        return creature;
    }

    // One mash press by player 2 (or the test bot).
    internal static void Press(string who)
    {
        if (!Held)
            return;
        _presses++;
        _lastPress = Time.time;
        _progress += 1f / PressesNeeded;
        if (_progress >= 1f)
            Release(true, $"broke free after {_presses} presses ({who})");
    }

    internal static void Tick()
    {
        if (!Held)
            return;
        var p2 = SecondDiver.Player2;
        if (p2 == null)
        {
            Clear();
            return;
        }
        if (_creature == null || !_creature.gameObject.activeInHierarchy)
        {
            Release(false, "the creature is gone");
            return;
        }
        if (Time.time - _since > MaxHold)
        {
            Release(false, "the creature let go");
            return;
        }
        if (P2Keys.Pressed(P2Keys.Action.Ok) || P2Keys.Pressed(P2Keys.Action.Back) || P2Keys.Pressed(P2Keys.Action.Interact))
            Press("player 2");
        if (!Held)
            return;
        // Slowly lose grip when not mashing.
        if (Time.time - _lastPress > 0.6f)
            _progress = Mathf.Max(0f, _progress - Time.deltaTime * 0.15f);
        try
        {
            var tank = p2.BreathHandler;
            if (tank != null && tank.HP > 1f)
                tank.HP = Mathf.Max(1f, tank.HP - tank.MaxHP * DrainPerSecond * Time.deltaTime);
        }
        catch
        {
        }
        Stick(p2);
        if (_gameBar)
        {
            P2MashUI.Set(_progress);
            P2MashUI.Follow(p2.transform.position);
        }
    }

    // After the game's own movement, keep player 2 in the mouth.
    internal static void LateTick()
    {
        var p2 = SecondDiver.Player2;
        if (Held && p2 != null && _mouth != null)
            Stick(p2);
    }

    private static void Stick(PlayerCharacter p2)
    {
        if (_mouth == null)
            return;
        var at = _mouth.position;
        var pos = new Vector3(at.x, at.y, p2.transform.position.z);
        p2.transform.position = pos;
        try
        {
            var body = p2.GetComponent<Rigidbody2D>();
            if (body != null)
            {
                body.position = pos;
                body.velocity = Vector2.zero;
            }
        }
        catch
        {
        }
    }

    private static void Release(bool escaped, string why)
    {
        var p2 = SecondDiver.Player2;
        try
        {
            if (p2 != null && _creature != null)
            {
                // Push player 2 a little away from the mouth.
                var away = (Vector2)(p2.transform.position - _creature.position);
                if (away.sqrMagnitude < 0.01f)
                    away = Vector2.left;
                var pos = p2.transform.position + (Vector3)(away.normalized * 1.5f);
                p2.transform.position = pos;
                var body = p2.GetComponent<Rigidbody2D>();
                if (body != null)
                    body.position = pos;
            }
            EndStruggle(p2);
            PlayResult(escaped);
            if (p2 != null)
            {
                if (p2.IsActionLock)
                    p2.SetActionLock(false);
                if (p2.IsMoveLocked)
                    p2.UnlockMove();
            }
        }
        catch
        {
        }
        CoopTest.Record(escaped ? $"P2 {why}" : $"P2 released: {why}");
        Plugin.Logger.LogInfo($"P2 grab: released from {_creatureName}: {why}");
        _immuneUntil = Time.time + 3f;
        Clear();
    }

    internal static void Clear()
    {
        _resultAnimator = null;
        P2MashUI.Hide();
        _gameBar = false;
        _creature = null;
        _mouth = null;
        _progress = 0f;
    }

    internal static void Draw()
    {
        if (!Held || _gameBar)
            return;
        var p2 = SecondDiver.Player2;
        var cam = Camera.main;
        if (p2 == null || cam == null)
            return;
        _style ??= new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 22,
            fontStyle = FontStyle.Bold,
            richText = true,
        };
        var sp = cam.WorldToScreenPoint(p2.transform.position);
        var x = sp.x;
        var y = Screen.height - sp.y - 110f;
        const float w = 220f, h = 22f;
        var old = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.75f);
        GUI.DrawTexture(new Rect(x - w / 2f - 6f, y - 34f, w + 12f, h + 44f), Texture2D.whiteTexture);
        GUI.color = Color.white;
        GUI.Label(new Rect(x - w / 2f, y - 32f, w, 30f), "<color=#C79BFF>P2</color>  MASH  A / B !", _style);
        GUI.color = new Color(1f, 1f, 1f, 0.25f);
        GUI.DrawTexture(new Rect(x - w / 2f, y, w, h), Texture2D.whiteTexture);
        GUI.color = new Color(0.78f, 0.61f, 1f, 1f);
        GUI.DrawTexture(new Rect(x - w / 2f, y, w * Mathf.Clamp01(_progress), h), Texture2D.whiteTexture);
        GUI.color = old;
    }
}

// Learn which animation the game uses when it grabs player 1.
[HarmonyLib.HarmonyPatch(typeof(PlayerCharacter), nameof(PlayerCharacter.CommonQTEStart))]
internal static class P2Struggle
{
    internal static bool Ours;

    private static bool Prefix(PlayerCharacter __instance, string __0)
    {
        if (Ours)
            return true;
        var p1 = SecondDiver.Player1;
        var p2 = SecondDiver.Player2;
        // The game's struggle on player 2 brings its mash bar (which takes
        // player 1's controls) and hides the HUD: the mod's grab does it instead.
        if (p2 != null && __instance != null && __instance.Pointer == p2.Pointer && P2Grab.Enabled && !NoSharkGrab.TestOff)
        {
            Plugin.Logger.LogInfo($"Grab struggle on player 2 by '{__0}' skipped (the mod runs player 2's grab)");
            CoopTest.Record($"game struggle on P2 skipped ({__0})");
            return false;
        }
        if (p1 != null && __instance != null && __instance.Pointer == p1.Pointer)
            P2Grab.LearnAnimation(__0);
        Plugin.Logger.LogInfo($"Grab struggle started on {(p1 != null && __instance != null && __instance.Pointer == p1.Pointer ? "player 1" : "player 2")} (by '{__0}', p2Turn={SecondDiver.InPlayer2Turn})");
        return true;
    }
}

// A creature's mash prompt (an animation event on the creature). When it's
// aimed at player 2, don't open it (it takes player 1's controls): the mod
// holds player 2 instead and remembers the creature's result animations.
[HarmonyLib.HarmonyPatch(typeof(AniClipEvents.OpenQTEUI), nameof(AniClipEvents.OpenQTEUI.Open))]
internal static class P2GrabPrompt
{
    private static bool Prefix(AniClipEvents.OpenQTEUI __instance)
    {
        try
        {
            var p2 = SecondDiver.Player2;
            if (p2 == null || !P2Grab.Enabled || NoSharkGrab.TestOff)
                return true;
            var fish = __instance.saAI;
            Component who = fish;
            if (who == null && __instance._animator != null)
                who = __instance._animator;
            if (who == null)
                return true;
            var onP2 = P2Grab.HoldsBy(who) || GrabAttach.FishOnP2(who) || NoSharkGrab.CloserToPlayer2(who);
            if (!onP2)
                return true;
            P2Grab.TryBegin(who, "mash prompt");
            P2Grab.SetResult(__instance._animator, __instance.hasResultTrigger ? __instance.successTriggerName : null,
                __instance.hasResultTrigger ? __instance.failTriggerName : null);
            CoopTest.Record($"game mash prompt for P2 skipped ({NoSharkGrab.FishName(who)})");
            Plugin.Logger.LogInfo($"P2 grab: {NoSharkGrab.FishName(who)} mash prompt kept away from player 1 (success='{__instance.successTriggerName}', fail='{__instance.failTriggerName}')");
            return false;
        }
        catch (System.Exception exception)
        {
            Plugin.Logger.LogInfo($"P2 grab prompt check: {exception.Message}");
            return true;
        }
    }
}

// A grabbing creature's animation event that plays Dave's panic / struggle
// animation (AniClipEvents.PlayDaveAnimation.Fire, from the Cpp2IL dump): it
// calls StartBattleQTE, the battle-QTE "PlayDaveAnim" trigger and the QTE
// animation key on "the player". When the creature is grabbing player 2, run
// it as player 2's turn so player 2 panics instead of player 1. Everything it
// touches is per-player, and P2Grab's CommonQTEEnd on player 2 clears it.
[HarmonyLib.HarmonyPatch(typeof(AniClipEvents.PlayDaveAnimation), nameof(AniClipEvents.PlayDaveAnimation.Fire))]
internal static class P2PanicAnimation
{
    private static int _logs;

    private static bool Prefix(Animator __0, ref bool __state)
    {
        __state = false;
        try
        {
            var p1 = SecondDiver.Player1;
            var p2 = SecondDiver.Player2;
            if (p2 == null || __0 == null || !P2Grab.Enabled || NoSharkGrab.TestOff)
                return true;
            var fish = NoSharkGrab.FishName(__0);
            // Creatures on the block list never grab anyone in co-op, so their
            // "grab" animation must not make either diver panic (sawtooth).
            if (NoSharkGrab.Blocked(fish))
            {
                if (_logs++ < 20)
                    Plugin.Logger.LogInfo($"P2 grab: {fish} grab animation on Dave skipped (block list)");
                return false;
            }
            // Player 1 already held by this kind of grab: it's player 1's.
            if (p1 != null && p1.IsBattleQTEState && !P2Grab.Held)
                return true;
            var bite = __0.GetComponentInParent<DR.AI.Biteable>(true) ?? __0.GetComponentInChildren<DR.AI.Biteable>(true);
            // Only when the creature is really on player 2 (not just nearer to
            // player 2: that sent player 1's attacks to player 2).
            var onP2 = P2Grab.HoldsBy(__0) || GrabAttach.FishOnP2(__0) || (bite != null && BiteRedirect.Targets(bite));
            if (!onP2)
                return true;
            SecondDiver.BeginPlayer2Turn(p2);
            __state = true;
            CoopTest.Record($"creature panic animation sent to P2 ({fish})");
            if (_logs++ < 20)
                Plugin.Logger.LogInfo($"P2 grab: {fish} plays Dave's grab animation on player 2");
        }
        catch (System.Exception exception)
        {
            Plugin.Logger.LogInfo($"P2 panic animation check: {exception.Message}");
        }
        return true;
    }

    private static void Postfix(bool __state)
    {
        if (__state)
            SecondDiver.EndPlayer2Turn(SecondDiver.Player2);
    }
}
