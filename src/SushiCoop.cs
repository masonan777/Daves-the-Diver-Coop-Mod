using System;
using System.Text;
using Il2CppInterop.Runtime;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DaveTheDiverCoop;

// Sushi bar (night): player 2 is a second, purple Dave walking the restaurant.
// Step 1: spawn a copy of the restaurant Dave and let player 2 walk it.
// Serving / picking up dishes comes next, once the log shows how the copy
// behaves. Player 2 appears automatically if they were in the day's dive
// (still joined), or joins here with F9 / LB + RB.
internal static class SushiCoop
{
    private static readonly Color Tint = new(0.78f, 0.62f, 1f, 1f);

    private static SushiBarStaffManagerImpl _staff;
    private static StaffDave _p1, _p2;
    private static SushiBarPlayerHanlder _p2Handler;
    private static float _nextSearch, _nextLog, _guardUntil;
    private static int _guardLogs;
    private static bool _failed;
    private static string _scene = string.Empty;

    internal static StaffDave Player2 => _p2;
    internal static bool Active => _p2 != null;
    internal static bool InRestaurant => _p1 != null;

    internal static void Tick()
    {
        try
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            if (scene != _scene)
            {
                _scene = scene;
                Despawn("scene changed");
                SushiP1Drive.Reset();
                _clean1 = default;
                _staff = null;
                _p1 = null;
                _failed = false;
            }
            if (!P2Keys.Joined)
            {
                if (_p2 != null)
                    Despawn("player 2 left");
                return;
            }
            if (_p1 == null)
            {
                if (Time.realtimeSinceStartup < _nextSearch)
                    return;
                _nextSearch = Time.realtimeSinceStartup + 1f;
                _staff = Object.FindFirstObjectByType<SushiBarStaffManagerImpl>();
                _p1 = _staff?.Dave;
                if (_p1 == null)
                    return;
                Plugin.Logger.LogInfo($"Sushi bar: found the restaurant Dave ({_p1.name}) in scene {scene}");
            }
            if (_p2 == null && !_failed)
                Spawn();
            if (_p2 == null)
                return;
            KeepPlayerOneTheDave();
            TickModal();
            P2VirtualPad.Tick();
            SushiP1Drive.Tick(_p1, _modal != SushiBarInteraction.None);
            if (_modal == SushiBarInteraction.None)
                Drive();
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning($"Sushi bar co-op: {exception}");
            _failed = true;
        }
    }

    private static void Spawn()
    {
        try
        {
            var source = _p1.gameObject;
            var copy = Object.Instantiate(source, source.transform.parent);
            copy.name = source.name + " (Player 2)";
            copy.transform.position = source.transform.position + new Vector3(1.2f, 0f, 0f);
            _p2 = copy.GetComponent<StaffDave>();
            // The copy has none of Dave's runtime set-up (his staff data,
            // progress, move speed values): fill those in from player 1.
            try { CloneRepair.Fill(source, copy, "Sushi bar player 2 Dave"); }
            catch (Exception exception) { Plugin.Logger.LogWarning($"Sushi bar: repair copy: {exception.Message}"); }
            try
            {
                if (_p2.m_Data == null)
                    _p2.SetData(_p1.m_Data);
                if (_p2.m_ProgressData == null)
                    _p2.m_ProgressData = _p1.m_ProgressData;
            }
            catch (Exception exception) { Plugin.Logger.LogWarning($"Sushi bar: staff data: {exception.Message}"); }
            // Own stamina / move values (not player 1's).
            try
            {
                var move = new Common.Contents.DaveMoveValue();
                move.Init();
                _p2._daveMoveValue = move;
            }
            catch (Exception exception) { Plugin.Logger.LogWarning($"Sushi bar: own move values: {exception.Message}"); }
            // Things the repair shared with player 1 that must be player 2's own:
            // the dish being carried and the list of nearby things to use.
            try { _p2.PickupRecipeID = new UniRx.ReactiveProperty<int>(0); }
            catch (Exception exception) { Plugin.Logger.LogWarning($"Sushi bar: own carried dish: {exception.Message}"); }
            try { _p2.m_InteractionList = new Il2CppSystem.Collections.Generic.List<IStaffDaveInteraction>(); }
            catch (Exception exception) { Plugin.Logger.LogWarning($"Sushi bar: own interaction list: {exception.Message}"); }
            Plugin.Logger.LogInfo($"Sushi bar: copy has own dish={(_p2.PickupRecipeID?.Pointer != _p1.PickupRecipeID?.Pointer)} " +
                                  $"own list={(_p2.m_InteractionList?.Pointer != _p1.m_InteractionList?.Pointer)} " +
                                  $"own action UI={(_p2.m_ActionInfoUI?.Pointer != _p1.m_ActionInfoUI?.Pointer)}");
            Plugin.Logger.LogInfo($"Sushi bar: copy set-up data={(_p2.m_Data != null)} progress={(_p2.m_ProgressData != null)} " +
                                  $"moveValue={(_p2._daveMoveValue != null)} sharedWithP1={(_p2._daveMoveValue?.Pointer == _p1._daveMoveValue?.Pointer)}");
            _p2Handler = copy.GetComponentInChildren<SushiBarPlayerHanlder>(true);
            if (_p2Handler != null)
            {
                try { _p2Handler.Init(_p2); }
                catch (Exception exception) { Plugin.Logger.LogWarning($"Sushi bar: player 2 move handler init: {exception.Message}"); }
            }
            var renderers = copy.GetComponentsInChildren(Il2CppType.Of<SpriteRenderer>(), true);
            for (var i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i]?.TryCast<SpriteRenderer>();
                if (r != null)
                    r.color *= Tint;
            }
            _guardUntil = Time.realtimeSinceStartup + 5f;
            Plugin.Logger.LogInfo($"Sushi bar: player 2 Dave spawned at {copy.transform.position}; handler={(_p2Handler != null)} " +
                                  $"handlerStaffIsP2={(_p2Handler?.m_Staff?.Pointer == _p2?.Pointer)}");
            Describe(copy);
        }
        catch (Exception exception)
        {
            _failed = true;
            Plugin.Logger.LogWarning($"Sushi bar: could not spawn player 2: {exception}");
        }
    }

    // The copy's own start-up may register itself as "the" Dave; keep the
    // game pointed at player 1 (serving logic is player 1's for now).
    private static void KeepPlayerOneTheDave()
    {
        if (_staff == null || _turnDepth > 0)
            return;
        try
        {
            var current = _staff.m_Dave;
            if (current != null && current.Pointer == _p2.Pointer)
            {
                _staff.m_Dave = _p1;
                if (_guardLogs++ < 5)
                    Plugin.Logger.LogInfo("Sushi bar: the copy registered itself as the restaurant Dave; put player 1 back");
            }
        }
        catch
        {
        }
    }

    private static Vector2 _lastMove;

    private static void Drive()
    {
        if (_p2Handler == null)
            return;
        var move = _clean.active ? Vector2.zero : P2Keys.Move;
        try
        {
            if (move.sqrMagnitude > 0.04f)
                _p2Handler.OnMove(move);
            else if (_lastMove.sqrMagnitude > 0.04f)
                _p2Handler.OnIdle(Vector2.zero);
        }
        catch (Exception exception)
        {
            if (Time.realtimeSinceStartup > _nextLog)
            {
                _nextLog = Time.realtimeSinceStartup + 3f;
                Plugin.Logger.LogWarning($"Sushi bar: player 2 move: {exception.Message}");
            }
        }
        _lastMove = move;
        Actions();
        if (Time.realtimeSinceStartup > _nextLog)
        {
            _nextLog = Time.realtimeSinceStartup + 3f;
            Plugin.Logger.LogInfo($"Sushi bar: P2 at {_p2.transform.position} move={move} handlerDir={_p2Handler.m_MoveDirection} " +
                                  $"next={_p2Handler.m_NextMovePosition} | P1 at {_p1.transform.position} | game's Dave={(_staff?.m_Dave?.Pointer == _p1.Pointer ? "player 1" : "other")}");
        }
    }

    // Player 2's buttons -> the restaurant Dave's actions. The game's own
    // handler tries, per button, the matching kind of interaction; player 2
    // does the same on the copy, with the game pointed at the copy meanwhile.
    //   A / X: QTE, talk, serve, special    B / Back: throw away (hold)
    //   Y / Item: special             LT / RT: clean left / right
    private static readonly (P2Keys.Action key, SushiBarInteraction[] kinds)[] ActionMap =
    {
        (P2Keys.Action.Ok, new[] { SushiBarInteraction.QTE, SushiBarInteraction.Talk, SushiBarInteraction.Serve, SushiBarInteraction.Special }),
        (P2Keys.Action.Interact, new[] { SushiBarInteraction.QTE, SushiBarInteraction.Talk, SushiBarInteraction.Serve, SushiBarInteraction.Special }),
        (P2Keys.Action.Back, new[] { SushiBarInteraction.Trash }),
        (P2Keys.Action.Item, new[] { SushiBarInteraction.Special }),
        (P2Keys.Action.Dash, new[] { SushiBarInteraction.CleanLeft }),
        (P2Keys.Action.Fire, new[] { SushiBarInteraction.CleanRight }),
    };
    private static readonly System.Collections.Generic.Dictionary<P2Keys.Action, SushiBarInteraction> _held = new();
    private static int _actionLogs;

    private static void Actions()
    {
        if (_clean.active)
        {
            CleanTick();
            return;
        }
        foreach (var (key, kinds) in ActionMap)
        {
            if (P2Keys.Pressed(key))
            {
                foreach (var kind in kinds)
                {
                    if (Run(kind, true, key))
                    {
                        _held[key] = kind;
                        break;
                    }
                }
            }
            else if (P2Keys.Released(key) && _held.TryGetValue(key, out var kind))
            {
                _held.Remove(key);
                Run(kind, false, key);
            }
        }
    }

    private static bool Run(SushiBarInteraction kind, bool pressed, P2Keys.Action key)
    {
        // Only one mini-game / conversation screen can run at a time: while
        // player 1 has one (or the clean screen) open, player 2 can't start one.
        if (pressed && (kind == SushiBarInteraction.QTE || kind == SushiBarInteraction.Talk || kind == SushiBarInteraction.Special)
            && OtherScreenOpen(out var what))
        {
            if (_actionLogs++ < 200)
                Plugin.Logger.LogInfo($"Sushi bar: player 2 {kind} blocked - {what} is open");
            return false;
        }
        var ok = false;
        BeginTurn();
        try
        {
            ok = _p2.OnInteraction_Impl(kind, pressed);
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning($"Sushi bar: player 2 {kind} ({(pressed ? "press" : "release")}): {exception.Message}");
        }
        finally
        {
            EndTurn();
        }
        if (ok && pressed && _clean.active)
        {
            // cleaning is handled by the mod (see CleanTick), nobody gets locked
        }
        else if (ok && pressed && (kind == SushiBarInteraction.QTE || kind == SushiBarInteraction.Talk || kind == SushiBarInteraction.Special
                              || kind == SushiBarInteraction.CleanLeft || kind == SushiBarInteraction.CleanRight))
            StartModal(kind);
        if ((ok || pressed) && _actionLogs++ < 200)
            Plugin.Logger.LogInfo($"Sushi bar: player 2 {key} -> {kind} {(pressed ? "press" : "release")}: {(ok ? "done" : "nothing to do")}; " +
                                  $"carrying={_p2.PickupRecipeID?.Value} near={_p2.m_InteractionList?.Count} now={_p2.m_NowInteractionObject?.name ?? "none"} " +
                                  $"customer={_p2.m_LastContactCustomer?.name ?? "none"} (P1's: {_p1.m_LastContactCustomer?.name ?? "none"})");
        return ok;
    }

    // Mini-games (tea, beer, wasabi), cleaning and talking read the game's
    // controller input directly. While player 2 is in one, lend player 2's
    // controller to the game; take it back when it ends (or as soon as
    // player 2's stick starts moving player 1, i.e. it is over).
    private static SushiBarInteraction _modal = SushiBarInteraction.None;
    private static float _modalSince;
    private static Vector3 _p1AtModal;
    private static SushiBarQTECleanPanel _cleanPanel;

    private static bool OtherScreenOpen(out string what)
    {
        what = "";
        try
        {
            if (SushiBar.QTE.QTEController.IsPlaying()) { what = "a mini-game"; return true; }
            if (_cleanPanel == null)
                _cleanPanel = Object.FindFirstObjectByType<SushiBarQTECleanPanel>(FindObjectsInactive.Include);
            if (_cleanPanel != null && _cleanPanel.IsOpened) { what = "player 1's cleaning"; return true; }
        }
        catch
        {
        }
        return false;
    }

    internal static bool Player2ScreenOpen => _modal != SushiBarInteraction.None;

    private static void StartModal(SushiBarInteraction kind)
    {
        _modal = kind;
        _modalSince = Time.realtimeSinceStartup;
        _p1AtModal = _p1.transform.position;
        // Player 2's controller, or (on the keyboard) a virtual one fed from
        // player 2's keys: player 1's keyboard is busy driving player 1.
        if (P2Pad.Connected)
            P2Pad.Lend(true, $"player 2's {kind}");
        else
            P2VirtualPad.Begin($"player 2's {kind}");
    }

    private static void TickModal()
    {
        if (_modal == SushiBarInteraction.None)
            return;
        var age = Time.realtimeSinceStartup - _modalSince;
        var over = false;
        var why = "";
        try
        {
            if (age > 0.6f)
            {
                switch (_modal)
                {
                    case SushiBarInteraction.QTE:
                        if (!SushiBar.QTE.QTEController.IsPlaying()) { over = true; why = "mini-game finished"; }
                        break;
                    case SushiBarInteraction.CleanLeft:
                    case SushiBarInteraction.CleanRight:
                        if (_cleanPanel == null)
                            _cleanPanel = Object.FindFirstObjectByType<SushiBarQTECleanPanel>(FindObjectsInactive.Include);
                        if (_cleanPanel == null || !_cleanPanel.IsOpened) { over = true; why = "cleaning finished"; }
                        break;
                }
            }
            // Player 1 moving while player 2's stick is pushed (and player 1
            // isn't moving himself): player 2's controller is driving player 1,
            // so the activity is over.
            if (!over && age > 0.6f && P2Keys.Move.sqrMagnitude > 0.04f && !SushiP1Drive.MoveKeysHeld
                && (_p1.transform.position - _p1AtModal).sqrMagnitude > 0.0004f)
            {
                over = true;
                why = "player 1 started moving";
            }
            if (!over && age > 60f) { over = true; why = "time limit"; }
        }
        catch (Exception exception)
        {
            over = true;
            why = exception.Message;
        }
        _p1AtModal = _p1.transform.position;
        if (!over)
            return;
        Plugin.Logger.LogInfo($"Sushi bar: player 2's {_modal} over ({why}) after {age:F1} s");
        _modal = SushiBarInteraction.None;
        P2Pad.Lend(false, why);
        P2VirtualPad.End(why);
    }

    // Player 2's cleaning without the game's clean screen (which takes over
    // everyone's controls): player 2 alternates LT / RT, the mod counts the
    // strokes and runs the game's own "one wipe" / "finished" steps, so
    // player 1 can keep serving meanwhile.
    private static (bool active, Il2CppSystem.Action unit, Il2CppSystem.Action finished, int need, int done, bool lastLeft, bool any, SushiBarTrashTrigger table) _clean;

    // Called from the clean screen's Open while it's player 2's action.
    internal static bool CaptureClean(SushiBarQTECleanPanel panel, Il2CppSystem.Action unit, Il2CppSystem.Action finished, SushiBarTrashTrigger table)
    {
        if (_p2 == null)
            return false;
        var need = 6;
        try { need = Math.Max(1, panel.needCount); } catch { }
        if (_turnDepth == 0)
        {
            // Player 1 cleaning while player 2's mini-game has the game's input:
            // the mod counts player 1's strokes too (SushiP1Drive).
            if (SushiP1Drive.Driving && !_clean1.active)
            {
                _clean1 = (true, unit, finished, need, 0, false, false, table);
                Plugin.Logger.LogInfo($"Sushi bar: player 1 cleaning ({need} wipes, clean left / right in turn; trash to stop) while player 2 is in a mini-game");
                return true;
            }
            // Already cleaning that way: keep the game's clean screen shut.
            return _clean1.active;
        }
        _clean = (true, unit, finished, need, 0, false, false, table);
        Plugin.Logger.LogInfo($"Sushi bar: player 2 cleaning ({need} wipes, LT / RT in turn; B to stop) - player 1 stays free");
        return true;
    }

    // The cleaning tip player 1 would get (gold), like the clean screen does.
    private static void PayTip(int tip)
    {
        if (tip <= 0)
            return;
        try
        {
            object common = null;
            for (var t = typeof(CommonDefine); t != null && common == null; t = t.BaseType)
                common = t.GetProperty("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static
                                                   | System.Reflection.BindingFlags.FlattenHierarchy)?.GetValue(null);
            if (common is CommonDefine define)
                define.AddPlayerGoods((GoodsType)1, tip, int.MaxValue, false);
            else
                Plugin.Logger.LogInfo("Sushi bar: cleaning tip not paid (gold manager not found)");
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning($"Sushi bar: cleaning tip: {exception.Message}");
        }
    }

    private static void CleanTick() =>
        CleanStep(ref _clean, P2Keys.Pressed(P2Keys.Action.Dash), P2Keys.Pressed(P2Keys.Action.Fire),
            P2Keys.Pressed(P2Keys.Action.Back), true);

    // Player 1's cleaning run by the mod (see CaptureClean / SushiP1Drive).
    private static (bool active, Il2CppSystem.Action unit, Il2CppSystem.Action finished, int need, int done, bool lastLeft, bool any, SushiBarTrashTrigger table) _clean1;

    internal static bool Player1Cleaning => _clean1.active;

    internal static void Player1CleanStep(bool left, bool right, bool stop) => CleanStep(ref _clean1, left, right, stop, false);

    private static void CleanStep(ref (bool active, Il2CppSystem.Action unit, Il2CppSystem.Action finished, int need, int done, bool lastLeft, bool any, SushiBarTrashTrigger table) clean,
        bool left, bool right, bool stop, bool player2)
    {
        var who = player2 ? "player 2" : "player 1";
        if (!clean.active)
            return;
        if (stop)
        {
            Plugin.Logger.LogInfo($"Sushi bar: {who} stopped cleaning");
            clean = default;
            return;
        }
        if (!left && !right)
            return;
        // Strokes must alternate (left, right, left...).
        if (clean.any && left == clean.lastLeft && !(left && right))
            return;
        clean.any = true;
        clean.lastLeft = left;
        clean.done++;
        if (player2)
            BeginTurn();
        try
        {
            clean.unit?.Invoke();
            if (clean.done >= clean.need)
            {
                // What the clean screen does on success: the table is clean,
                // then the game's own "done" step.
                var tip = 0;
                try { tip = SNSInfoManager.CleanQTETip; } catch { }
                PayTip(tip);
                clean.table?.CleanFinished(tip);
                clean.finished?.Invoke();
                Plugin.Logger.LogInfo($"Sushi bar: {who} finished cleaning");
                clean = default;
            }
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning($"Sushi bar: {who} cleaning: {exception.Message}");
            clean = default;
        }
        finally
        {
            if (player2)
                EndTurn();
        }
    }

    // While player 2 acts, "the restaurant's Dave" is player 2.
    private static int _turnDepth;

    internal static bool InPlayer2Turn => _turnDepth > 0;

    private static void BeginTurn()
    {
        if (_turnDepth++ == 0 && _staff != null)
            try { _staff.m_Dave = _p2; } catch { }
    }

    private static void EndTurn()
    {
        if (--_turnDepth == 0 && _staff != null)
            try { _staff.m_Dave = _p1; } catch { }
    }

    internal static void Despawn(string why)
    {
        if (_p2 == null)
            return;
        _modal = SushiBarInteraction.None;
        _clean = default;
        _clean1 = default;
        SushiP1Drive.Reset();
        P2Pad.Lend(false, "player 2 Dave removed");
        P2VirtualPad.End("player 2 Dave removed");
        try
        {
            Object.Destroy(_p2.gameObject);
        }
        catch
        {
        }
        _p2 = null;
        _p2Handler = null;
        Plugin.Logger.LogInfo($"Sushi bar: player 2 Dave removed ({why})");
    }

    private static void Describe(GameObject root)
    {
        var text = new StringBuilder("Sushi bar: player 2 Dave parts:\n");
        void Walk(Transform t, int depth)
        {
            if (depth > 4)
                return;
            text.Append(' ', depth * 2).Append(t.name).Append(t.gameObject.activeSelf ? "" : " (off)").Append(": ");
            var components = t.GetComponents(Il2CppType.Of<Component>());
            for (var i = 0; i < components.Length; i++)
                text.Append(components[i]?.GetIl2CppType()?.Name).Append(", ");
            text.Append('\n');
            for (var i = 0; i < t.childCount; i++)
                Walk(t.GetChild(i), depth + 1);
        }
        Walk(root.transform, 0);
        Plugin.Logger.LogInfo(text.ToString());
    }
}

[HarmonyLib.HarmonyPatch(typeof(SushiBarQTECleanPanel), nameof(SushiBarQTECleanPanel.Open))]
internal static class SushiCleanOpen
{
    private static bool Prefix(SushiBarQTECleanPanel __instance, Il2CppSystem.Action __0, Il2CppSystem.Action __1, SushiBarTrashTrigger __2) =>
        !SushiCoop.CaptureClean(__instance, __0, __1, __2);
}
