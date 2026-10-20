using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using Il2CppInterop.Runtime;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DaveTheDiverCoop;

// In-game test bot for creature grabs (see the coop-anything playbook,
// automated-qa.md). During a dive with player 2 joined:
//   Left Shift + F3 ... run the grab suite
//   Left Shift + F4 ... same suite with the mod's grab rules switched OFF
//                  ("mutation twin": it must report failures, or the tests
//                   can't actually see the fix)
// For each creature in [Test] GrabSuite and each target diver, the bot
// spawns the creature next to the target, forces its grab attack, watches
// what happens, and writes PASS / FAIL / SKIP / NO-ATTACK lines plus a
// summary to BepInEx/coop_tests.log.
internal static class CoopTest
{
    private static ConfigEntry<string> _suite;
    private static readonly List<string> _events = new();
    private static bool _shiftF3, _shiftF4;

    private sealed class Case
    {
        internal string Fish;
        internal bool TargetP2;
    }

    private static readonly Queue<Case> _cases = new();
    private static Case _case;
    private static int _step;
    private static float _stepAt;
    private static GameObject _fishObj;
    private static DR.AI.SABaseFishSystem _fish;
    private static Vector3 _base, _caseBase;
    // The level loads its creature spawners in pieces around the divers, so a
    // shark that lives deeper isn't spawnable near the surface. Before the
    // cases, the bot scouts downward and remembers where each creature's
    // spawner is; each case is then run there.
    private static readonly Dictionary<string, Vector3> _habitat = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> _wanted = new();
    private static int _scoutStep = -1;
    private static float _scoutAt;
    private const int ScoutSteps = 24; // 25 units each
    private const bool UseHabitats = false;
    private static bool _forced, _lockedDuring, _spawned;
    private static string _fishName;
    private static string _skillInfo;
    private static bool _mutation;
    private static readonly List<string> _results = new();
    private static int _pass, _fail, _skip, _noAttack;
    private static StreamWriter _log;

    private static bool _active, _haveBase, _p1LockedInHold, _hudOffInHold;
    private static float _nextMash, _nextHoldCheck, _nextRetry;
    private static int _grabId = int.MinValue, _retries; // true from Start until Finish (the last case is done before Finish runs)
    internal static bool Running => _active || _case != null || _cases.Count > 0;

    private static ConfigEntry<bool> _autoRun, _autoQuit, _autoMutation, _autoStart;
    private static ConfigEntry<string> _lastDive;
    private static string _navScene = "";
    private static float _navSceneAt;
    private static bool _navTitleDone, _navLobbyDone;
    private static bool _autoDone, _autoMutationPending, _needSuite, _travelAsked, _travelled;
    private static ConfigEntry<string> _areas;
    private static string _travelTo;
    private static float _travelAt;
    private static List<string> _override;
    private static readonly HashSet<string> _missing = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _visited = new(StringComparer.OrdinalIgnoreCase);
    private static float _autoSince = -1f, _autoLook;

    internal static void Bind(ConfigFile config)
    {
        _suite = config.Bind("Test", "GrabSuite", "Saw,Whitetip,Tiger_Shark",
            "Creatures (parts of names) the grab test bot tries (Shift + F3 in a dive with player 2).");
        _autoRun = config.Bind("Test", "AutoRun", false,
            "Test mode: as soon as a dive loads, join player 2, run the grab suite and write BepInEx/coop_tests.log.");
        _autoMutation = config.Bind("Test", "AutoRunMutation", true,
            "In test mode, also run the suite once with the grab rules off (it must fail).");
        _autoQuit = config.Bind("Test", "AutoQuit", true,
            "In test mode, close the game when the tests are done.");
        _autoStart = config.Bind("Test", "AutoStartDive", true,
            "In test mode, skip the title screen (Continue) and go straight from the boat into the last dive area.");
        _lastDive = config.Bind("Test", "LastDiveScene", "",
            "Remembered automatically: the dive area the test last ran in.");
        _areas = config.Bind("Test", "OtherAreas", "",
            "In test mode, creatures that don't live in the first dive area are tested by travelling to these areas, in order.");
        FishSpawner.UseGameFiles = config.Bind("Test", "LoadFromGameFiles", false,
            "Experimental: load creatures from other areas out of the game's asset catalogue (crashed once).").Value;
    }

    private static object Instance(Type type)
    {
        for (var t = type; t != null; t = t.BaseType)
        {
            var property = t.GetProperty("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (property != null)
                return property.GetValue(null);
        }
        return null;
    }

    // Test mode: title screen -> Continue -> boat -> straight into the last
    // dive area, so a test run needs nothing but starting the game.
    private static int _navLobbyTries;
    private static LobbyStartGamePanelUI _divePanel;
    private static float _holdUntil;

    private static bool PressDiveButton()
    {
        try
        {
            LobbyStartGamePanelUI best = null;
            foreach (var p in Resources.FindObjectsOfTypeAll<LobbyStartGamePanelUI>())
                if (p != null && p.gameObject.scene.IsValid() && (best == null || p.gameObject.activeInHierarchy))
                    best = p;
            if (best == null)
            {
                Plugin.Logger.LogInfo("Test mode: the boat's dive button was not found");
                return false;
            }
            // Normally the button only switches on once Dave walks to the
            // right end of the boat; switch it on directly.
            try { best.Activate(true, true); } catch (Exception e) { Plugin.Logger.LogInfo($"Test mode: dive button Activate: {e.Message}"); }
            try { best.ActiveStartButton(); } catch (Exception e) { Plugin.Logger.LogInfo($"Test mode: dive button ActiveStartButton: {e.Message}"); }
            try
            {
                var names = new StringBuilder();
                foreach (var c in Object.FindObjectsByType<Collider2D>(FindObjectsSortMode.None))
                    if (c != null && c.isTrigger && (c.name.IndexOf("Dive", StringComparison.OrdinalIgnoreCase) >= 0 || c.name.IndexOf("Start", StringComparison.OrdinalIgnoreCase) >= 0))
                        names.Append($"{c.name}@{c.transform.position} ");
                Plugin.Logger.LogInfo($"Test mode: boat dive/start triggers: {names}");
            }
            catch { }
            _divePanel = best;
            _holdUntil = Time.realtimeSinceStartup + Mathf.Max(1f, best.startGameConfirmTime) + 2f;
            Plugin.Logger.LogInfo($"Test mode: boat -> holding the dive button ({best.gameObject.name}, active={best.gameObject.activeInHierarchy}, hold {best.startGameConfirmTime:F1} s)");
            return true;
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogInfo($"Test mode: dive button: {exception.Message}");
            return false;
        }
    }

    // Called every frame: keep the button held until the dive starts.
    private static void HoldDiveButton()
    {
        if (_divePanel == null || Time.realtimeSinceStartup > _holdUntil)
        {
            _divePanel = null;
            return;
        }
        try { _divePanel.HoldStartGame(); }
        catch (Exception exception)
        {
            Plugin.Logger.LogInfo($"Test mode: holding the dive button failed ({exception.Message})");
            _divePanel = null;
        }
    }

    private static void AutoNavigate()
    {
        if (_autoStart == null || !_autoStart.Value || _autoDone)
            return;
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        if (scene != _navScene)
        {
            _navScene = scene;
            _navSceneAt = Time.realtimeSinceStartup;
        }
        var since = Time.realtimeSinceStartup - _navSceneAt;
        if (scene == "DR_Lobby")
            HoldDiveButton();
        try
        {
            if (scene == "DR_Title" && !_navTitleDone && since > 5f)
            {
                _navTitleDone = true;
                var title = Object.FindFirstObjectByType<DR.Title.TitleManager>();
                if (title == null)
                {
                    Plugin.Logger.LogWarning("Test mode: title screen manager not found; press Continue yourself");
                    return;
                }
                Plugin.Logger.LogInfo("Test mode: title screen -> Continue");
                title.ContinueGame();
            }
            else if (scene == "DR_Lobby" && !_navLobbyDone && since > 8f + 15f * _navLobbyTries)
            {
                // The boat can ignore the request while something is on screen
                // (a new day, a dialogue): ask again every 15 s, 4 times.
                if (++_navLobbyTries >= 4)
                {
                    _navLobbyDone = true;
                    if (_navLobbyTries > 4)
                        return;
                }
                if (_navLobbyTries > 1)
                    Plugin.Logger.LogInfo($"Test mode: still on the boat after {since:F0} s; asking again (try {_navLobbyTries} of 4)");
                // Start the dive the normal way: hold the boat's dive button.
                // (Going straight to a dive area skips the game's dive setup,
                // which leaves out most of the sharks.)
                if (PressDiveButton())
                    return;
                var dive = _lastDive?.Value;
                if (string.IsNullOrEmpty(dive))
                {
                    Plugin.Logger.LogInfo("Test mode: no dive area remembered yet; start this dive yourself once");
                    return;
                }
                var loader = (Instance(typeof(GlobalSceneLoader)) as GlobalSceneLoader)?.sceneLoader
                             ?? Object.FindFirstObjectByType<SceneLoader>();
                if (loader == null)
                {
                    Plugin.Logger.LogWarning("Test mode: scene loader not found; start the dive yourself");
                    return;
                }
                Plugin.Logger.LogInfo($"Test mode: boat -> dive area {dive}");
                loader.GoToInGameEntry(dive, (SceneTransitionType)0, false);
            }
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning($"Test mode: automatic start failed ({exception.Message}); start the dive yourself");
        }
    }

    // Test mode: nobody needs to press anything once a dive has loaded.
    private static void AutoTick()
    {
        if (_autoRun == null || !_autoRun.Value || _autoDone || Running)
            return;
        AutoNavigate();
        if (_travelTo != null)
        {
            Travel();
            return;
        }
        if (_autoMutationPending && !_needSuite)
        {
            _autoMutationPending = false;
            SafeStart(true);
            return;
        }
        // Player 1 is only tracked once player 2 has joined, so look for a
        // diver in the scene ourselves (once a second).
        if (!P2Keys.Joined)
        {
            if (Time.realtimeSinceStartup < _autoLook)
                return;
            _autoLook = Time.realtimeSinceStartup + 1f;
            var diver = Object.FindFirstObjectByType<PlayerCharacter>();
            if (diver == null || !diver.isActiveAndEnabled)
            {
                _autoSince = -1f;
                return;
            }
            Plugin.Logger.LogInfo("Test mode: dive found, joining player 2");
            if (!_travelled)
                try { _lastDive.Value = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name; } catch { }
            P2Keys.JoinFromController();
            return;
        }
        Application.runInBackground = true;
        if (SecondDiver.Player2 == null)
            return;
        if (_autoSince < 0f)
        {
            _autoSince = Time.realtimeSinceStartup;
            Plugin.Logger.LogInfo("Test mode: dive loaded with player 2; starting the grab suite in 6 s");
        }
        if (Time.realtimeSinceStartup - _autoSince < 6f)
            return;
        if (_needSuite)
        {
            _needSuite = false; // the mutation run is still pending
            SafeStart(false);
            return;
        }
        _autoMutationPending = _autoMutation.Value;
        SafeStart(false);
    }

    // Creatures that weren't in this area: go to the next area and test them there.
    private static void DecideTravel()
    {
        if (_autoRun == null || !_autoRun.Value || _mutation || _missing.Count == 0)
            return;
        var here = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        _visited.Add(here);
        foreach (var a in (_areas?.Value ?? "").Split(','))
        {
            var area = a.Trim();
            if (area.Length == 0 || _visited.Contains(area))
                continue;
            _override = new List<string>(_missing);
            _travelTo = area;
            _travelAsked = false;
            Plugin.Logger.LogInfo($"Test mode: {string.Join(", ", _override)} not in {here}; travelling to {area}");
            return;
        }
        Plugin.Logger.LogInfo($"Test mode: {string.Join(", ", _missing)} not found in any test area");
    }

    private static void Travel()
    {
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        if (!_travelAsked)
        {
            _travelAsked = true;
            _travelAt = Time.realtimeSinceStartup;
            _travelled = true;
            _visited.Add(_travelTo);
            _haveBase = false;
            _autoSince = -1f;
            try
            {
                var loader = (Instance(typeof(GlobalSceneLoader)) as GlobalSceneLoader)?.sceneLoader
                             ?? Object.FindFirstObjectByType<SceneLoader>();
                Write($"==== travelling to {_travelTo} for: {string.Join(", ", _override)}");
                loader.GoToInGameEntry(_travelTo, (SceneTransitionType)0, false);
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning($"Test mode: travel to {_travelTo} failed ({exception.Message})");
                _travelTo = null;
            }
            return;
        }
        if (scene == _travelTo)
        {
            Plugin.Logger.LogInfo($"Test mode: arrived in {scene}");
            _travelTo = null;
            _needSuite = true;
            return;
        }
        if (Time.realtimeSinceStartup - _travelAt > 60f)
        {
            Plugin.Logger.LogWarning($"Test mode: still not in {_travelTo} after 60 s; giving up on travelling");
            Write($"Travel to {_travelTo} did not happen (still in {scene})");
            _travelTo = null;
        }
    }

    // An auto start that throws must not retry every frame.
    private static void SafeStart(bool mutation)
    {
        try { Start(mutation); }
        catch (Exception exception)
        {
            Plugin.Logger.LogError($"Test bot could not start: {exception.Message}");
            _active = false;
            _autoMutationPending = false;
            _autoDone = true;
        }
    }

    private static void AutoFinished()
    {
        if (_autoRun == null || !_autoRun.Value || _autoMutationPending)
            return;
        _autoDone = true;
        if (_autoQuit.Value)
        {
            Plugin.Logger.LogInfo("Test mode: tests done, closing the game");
            Application.Quit();
        }
    }

    // Called from the mod's grab code while a test runs.
    internal static void Record(string what)
    {
        if (_case != null)
            _events.Add($"{Time.time - _stepAt:F1}s {what}");
    }

    internal static void Tick()
    {
        var shift = P2Keys.IsDown(0xA0);
        var f3 = shift && P2Keys.IsDown(0x72);
        var f4 = shift && P2Keys.IsDown(0x73);
        if (f3 && !_shiftF3 && !Running)
            Start(false);
        if (f4 && !_shiftF4 && !Running)
            Start(true);
        _shiftF3 = f3;
        _shiftF4 = f4;
        AutoTick();
        if (Running)
        {
            try { Step(); }
            catch (Exception exception)
            {
                Write($"ERROR in test bot: {exception}");
                Finish("aborted");
            }
        }
    }

    private static void Start(bool mutation)
    {
        Application.runInBackground = true; // don't stall if the window loses focus
        var p1 = SecondDiver.Player1;
        var p2 = SecondDiver.Player2;
        _results.Clear();
        _pass = _fail = _skip = _noAttack = 0;
        _mutation = mutation;
        Directory.CreateDirectory(Paths.BepInExRootPath);
        try { _log?.Dispose(); } catch { }
        _log = null;
        var stream = new FileStream(Path.Combine(Paths.BepInExRootPath, "coop_tests.log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _log = new StreamWriter(stream) { AutoFlush = true };
        _active = true;
        Write($"==== Grab suite {(mutation ? "(MUTATION: grab rules OFF, expect FAILs)" : "")} {DateTime.Now:yyyy-MM-dd HH:mm:ss} scene={UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}");
        // Assert the mode we think we're in (a hollow pass is worse than none).
        if (p1 == null || p2 == null)
        {
            Write("PRECONDITION FAIL: need a dive with player 2 joined and spawned.");
            Finish("not started");
            return;
        }
        // Test spot: open water a bit below player 1 (a dive starts just under
        // the surface, and swimming up to it asks to end the dive). Teleports
        // fail silently, so check the spot is clear of rock first.
        // Reuse the last good spot (the mutation run follows straight after).
        var reuse = _haveBase && Clear(_base) && Clear(_base + new Vector3(-12f, 0f)) && Clear(_base + new Vector3(4f, 0f));
        if (!reuse && !FindSpot(p1.transform.position, out _base))
        {
            Write("PRECONDITION FAIL: no open water found near player 1 for the test spot - move somewhere more open and run again.");
            Finish("not started");
            return;
        }
        _haveBase = true;
        Write($"Test spot {_base} (player 1 started at {p1.transform.position})");
        NoSharkGrab.TestOff = mutation;
        if (!mutation)
            _missing.Clear();
        var names = !mutation && _override != null ? string.Join(",", _override) : _suite?.Value ?? "";
        foreach (var name in names.Split(','))
        {
            var n = name.Trim();
            if (n.Length == 0)
                continue;
            _cases.Enqueue(new Case { Fish = n, TargetP2 = false });
            _cases.Enqueue(new Case { Fish = n, TargetP2 = true });
        }
        Plugin.Logger.LogInfo($"Test bot: running {_cases.Count} grab cases (see BepInEx/coop_tests.log)");
        _wanted.Clear();
        _habitat.Clear();
        foreach (var c in _cases)
            if (!_wanted.Contains(c.Fish))
                _wanted.Add(c.Fish);
        NoteHabitats();
        // Scouting (teleporting deeper to load spawners) turned out not to be
        // needed: a dive started from the boat's dive button has all of them.
        _scoutStep = -1;
        if (_scoutStep == 0)
            Write($"  .. scouting deeper for: {string.Join(", ", _wanted.FindAll(w => !_habitat.ContainsKey(w)))}");
    }

    private static void Step()
    {
        var p1 = SecondDiver.Player1;
        var p2 = SecondDiver.Player2;
        if (p1 == null || p2 == null)
        {
            Write("ABORT: a diver disappeared (dive ended?)");
            Finish("aborted");
            return;
        }
        if (_scoutStep >= 0)
        {
            Scout(p1, p2);
            return;
        }
        if (_case == null)
        {
            if (_cases.Count == 0)
            {
                Finish("done");
                return;
            }
            _case = _cases.Dequeue();
            _events.Clear();
            _step = 0;
            _stepAt = Time.time;
            _forced = false;
            _skillInfo = "";
            _p1LockedInHold = _hudOffInHold = false;
            _grabId = int.MinValue;
            _caseStart = Time.time;
            _hasLastFishPos = false;
            P2Grab.Clear();
            _caseBase = _base;
            if (UseHabitats && _habitat.TryGetValue(_case.Fish, out var home) && Vector2.Distance(home, _base) > 30f)
            {
                // Go where it lives first, and let that part of the level load.
                Place(p1, home);
                Place(p2, home + new Vector3(-3f, 0f, 0f));
                _step = -1;
                Write($"  .. {_case.Fish} lives at {home}; going there");
                return;
            }
            Write($"  .. {_case.Fish} -> {(_case.TargetP2 ? "P2" : "P1")}: spawning");
            Begin(p1, p2);
            return;
        }
        var age = Time.time - _stepAt;
        Track(p1, p2);
        WhileP2Held(p1);
        RetryGrab(p1, p2);
        switch (_step)
        {
            case -1 when age > 3f:
                _caseBase = FindSpot(_habitat[_case.Fish], out var spot) ? spot : _habitat[_case.Fish];
                Write($"  .. {_case.Fish} -> {(_case.TargetP2 ? "P2" : "P1")}: spawning (test spot {_caseBase})");
                Begin(p1, p2);
                _step = 0;
                _stepAt = Time.time;
                _caseStart = Time.time;
                break;
            case 0 when age > 1.0f:
                if (Fire(p1, p2) || age > 8f)
                {
                    Write($"  .. attack {(_forced ? "forced" : "not forced")} at {age:F1}s");
                    _step = 1;
                    _stepAt = Time.time - 1f; // judge 9 s after firing
                }
                break;
            // Don't take the creature away in the middle of a P2 hold.
            case 1 when age > 10f && (!(P2Grab.Held || GrabAttach.AnyoneHeld) || age > 16f):
                // Take the creature away, then give the game 4 s to recover
                // (a shark can also die or swim off mid-grab in real play).
                _lockedDuring = p1.IsActionLock || p1.IsMoveLocked || p2.IsActionLock || p2.IsMoveLocked;
                Write("  .. watched 10 s, removing the creature");
                if (_fishObj != null)
                    Object.Destroy(_fishObj);
                _step = 2;
                _stepAt = Time.time - 10f; // judge 4 s after removing it
                break;
            case 2 when age > 14f:
                Judge(p1, p2);
                Cleanup(p1, p2);
                _case = null;
                break;
        }
    }

    // While the mod holds player 2: player 1 must stay free and the HUD on.
    // After 0.8 s of checking, mash for player 2 like a person would.
    private static float _heldSince = -1f;

    private static void WhileP2Held(PlayerCharacter p1)
    {
        if (!P2Grab.Held)
        {
            _heldSince = -1f;
            return;
        }
        if (_heldSince < 0f)
        {
            _heldSince = Time.time;
            _nextMash = Time.time + 0.8f;
        }
        if (Time.time >= _nextHoldCheck)
        {
            _nextHoldCheck = Time.time + 0.25f;
            try
            {
                if (p1.IsActionLock || p1.IsMoveLocked)
                    _p1LockedInHold = true;
                var hud = Object.FindFirstObjectByType<MainCanvasManager>();
                if (hud != null && !hud.IsHudOn)
                    _hudOffInHold = true;
            }
            catch
            {
            }
        }
        if (Time.time >= _nextMash)
        {
            _nextMash = Time.time + 0.1f;
            P2Grab.Press("test bot");
        }
    }

    // A forced grab attack can miss (wrong distance or side). If nothing has
    // been grabbed yet, try again from other spots.
    private static readonly Vector2[] RetryOffsets = new Vector2[3]; // number of retries
    private static float _caseStart;
    private static Vector2 _lastFishPos;
    private static bool _hasLastFishPos;

    // 2.5-7 m from the target and moving towards it.
    private static bool Approaching(PlayerCharacter target)
    {
        if (_fish == null || target == null)
            return false;
        Vector2 at = _fish.transform.position;
        Vector2 to = (Vector2)target.transform.position - at;
        var moved = _hasLastFishPos ? at - _lastFishPos : Vector2.zero;
        _lastFishPos = at;
        _hasLastFishPos = true;
        var d = to.magnitude;
        return d >= 2.5f && d <= 7f && moved.sqrMagnitude > 1e-6f && Vector2.Dot(moved.normalized, to / d) > 0.7f;
    }

    private static void RetryGrab(PlayerCharacter p1, PlayerCharacter p2)
    {
        if (_step != 1 || !_forced || _grabId == int.MinValue || _fishObj == null || _fish == null || _retries >= RetryOffsets.Length || Time.time < _nextRetry)
            return;
        if (_events.Exists(e => e.Contains("attach to mouth") || e.Contains("held by") || e.Contains("grab step")))
            return;
        var target = Target(p1, p2);
        if (!Approaching(target))
            return;
        _retries++;
        _nextRetry = Time.time + 2.5f;
        try
        {
            _fish.Target = target.transform;
            _fish.cachedRuntimeSkillMgr?.ForceUse(_grabId);
            Write($"  .. no grab yet, forced again ({Vector2.Distance(_fish.transform.position, target.transform.position):F1} m away, swimming at the target)");
        }
        catch (Exception exception)
        {
            Write($"  .. retry failed: {exception.Message}");
        }
    }

    private static PlayerCharacter Target(PlayerCharacter p1, PlayerCharacter p2) => _case.TargetP2 ? p2 : p1;

    private static void Begin(PlayerCharacter p1, PlayerCharacter p2)
    {
        Reset(p1);
        Reset(p2);
        var target = Target(p1, p2);
        var other = _case.TargetP2 ? p1 : p2;
        Place(target, _caseBase);
        Place(other, _caseBase + new Vector3(-12f, 0f, 0f));
        _fishObj = FishSpawner.SpawnAt(_case.Fish, _caseBase + new Vector3(4f, 0f, 0f));
        _fish = _fishObj != null ? _fishObj.GetComponentInChildren<DR.AI.SABaseFishSystem>(true) : null;
        _spawned = _fishObj != null && _fish != null;
        _fishName = _fishObj != null ? _fishObj.name : null;
        _last1 = p1.BreathHandler.HP;
        _last2 = p2.BreathHandler.HP;
        _lost1 = _lost2 = 0f;
    }

    // true when done (attack forced, or nothing to force)
    private static bool Fire(PlayerCharacter p1, PlayerCharacter p2)
    {
        if (_fishObj == null || _fish == null)
            return true; // judged as SKIP
        var target = Target(p1, p2);
        try { _fish.Target = target.transform; } catch { }
        var skills = _fish.cachedRuntimeSkillMgr;
        if (skills == null)
        {
            _skillInfo = "no skill manager";
            return false; // may still be setting up; retried until 4 s
        }
        var list = new StringBuilder();
        int grabId = int.MinValue, bestRank = 99;
        var all = skills.runtimeSkills;
        if (all != null)
        {
            foreach (var entry in all)
            {
                var skill = entry.Value?.Skill;
                if (skill == null)
                    continue;
                var runners = new List<string>();
                var runnerList = skill.SkillRunners;
                if (runnerList != null)
                    foreach (var r in runnerList)
                        if (r != null)
                            runners.Add(r.GetIl2CppType().Name);
                var text = string.Join("+", runners);
                list.Append($"[{entry.Key}] {skill.Name} ({text}); ");
                var name = (skill.Name ?? "").ToLowerInvariant();
                // Grab-like names first, then grab-like steps, then bites.
                var rank = name.Contains("grab") ? 0
                    : name.Contains("hold") || name.Contains("catch") || name.Contains("drag") ? 1
                    : text.Contains("QTE") || text.Contains("Attach") || text.Contains("BiteHold") || text.Contains("Grab") ? 2
                    : name.Contains("bite") ? 3 : 99;
                if (rank < bestRank)
                {
                    bestRank = rank;
                    grabId = entry.Key;
                }
            }
        }
        _skillInfo = list.ToString();
        if (grabId == int.MinValue)
            return true;
        // Lunging grabs (mako) miss when started too close or facing away:
        // wait until the creature is swimming at its target from a few metres
        // away (forced anyway after 7 s).
        if (!Approaching(target) && Time.time - _caseStart < 7f)
        {
            _skillInfo = "waiting for a clean approach | " + _skillInfo;
            return false;
        }
        skills.ForceUse(grabId);
        _forced = true;
        _grabId = grabId;
        _retries = 0;
        _nextRetry = Time.time + 3f;
        _skillInfo = $"forced [{grabId}] | " + _skillInfo;
        return true;
    }

    // Count damage, but keep both divers alive: whenever a tank drops below
    // 60 % it is refilled (the loss so far is remembered).
    private static float _last1, _last2, _lost1, _lost2;

    private static void Track(PlayerCharacter p1, PlayerCharacter p2)
    {
        // Sharks can shove divers upward; keep them well below the surface so
        // the "end the dive?" prompt never appears mid-test.
        KeepDown(p1);
        KeepDown(p2);
        Leash(Target(p1, p2));
        Watch(p1, "P1", ref _last1, ref _lost1);
        Watch(p2, "P2", ref _last2, ref _lost2);
        Jumps(p1, "P1", ref _pos1);
        Jumps(p2, "P2", ref _pos2);
    }

    private static void Watch(PlayerCharacter diver, string who, ref float last, ref float lost)
    {
        try
        {
            var tank = diver.BreathHandler;
            var hp = tank.HP;
            if (hp < last)
                lost += last - hp;
            // A real hit takes several points at once: was anything near?
            if (last - hp > 4f)
                NoteHurt(diver, who, last - hp);
            if (hp < tank.MaxHP * 0.6f)
            {
                tank.HP = tank.MaxHP;
                hp = tank.MaxHP;
            }
            last = hp;
        }
        catch
        {
        }
    }

    // A diver that moves several metres in one frame was teleported. The bot's
    // own moves (Place) are excused.
    private static Vector2? _pos1, _pos2;
    private static int _placedFrame = -1;

    private static void Jumps(PlayerCharacter diver, string who, ref Vector2? last)
    {
        try
        {
            Vector2 at = diver.transform.position;
            if (last.HasValue && Time.frameCount > _placedFrame + 1)
            {
                var d = Vector2.Distance(last.Value, at);
                if (d > 4f)
                    Record($"{who} teleported {d:F0} m in one frame{(P2Grab.Held ? " (while P2 was held)" : "")}");
            }
            last = at;
        }
        catch
        {
        }
    }

    private static void NoteHurt(PlayerCharacter diver, string who, float amount)
    {
        try
        {
            Vector2 at = diver.transform.position;
            var mine = _fish != null ? Vector2.Distance(_fish.transform.position, at) : 999f;
            var nearest = 999f;
            var nearestName = "-";
            foreach (var f in Object.FindObjectsByType<DR.AI.SABaseFishSystem>(FindObjectsSortMode.None))
            {
                if (f == null || !f.isActiveAndEnabled || (_fish != null && f.Pointer == _fish.Pointer))
                    continue;
                var d = Vector2.Distance(f.transform.position, at);
                if (d < nearest)
                {
                    nearest = d;
                    nearestName = f.name;
                }
            }
            var near = mine < 6f || nearest < 6f;
            Record($"{who} hurt -{amount:F0} O2 {(near ? "" : "with nothing near ")}(test creature {mine:F0} m, nearest other {nearest:F0} m: {nearestName})");
        }
        catch
        {
        }
    }

    // On screen while the bot runs, so it's clear which pass is which.
    private static GUIStyle _banner;

    internal static void Draw()
    {
        if (!Running)
            return;
        _banner ??= new GUIStyle(GUI.skin.box) { fontSize = 18, richText = true, alignment = TextAnchor.MiddleCenter };
        var label = _case != null ? $"{_case.Fish} -> {(_case.TargetP2 ? "P2" : "P1")}" : "setting up";
        var text = _mutation
            ? $"<b>TEST BOT - CHECK PASS</b>: co-op grab fixes are OFF on purpose, bugs here are expected   <color=#FFD27F>{label}</color>"
            : $"<b>TEST BOT</b> - normal pass: report anything odd   <color=#B6FF9E>{label}</color>";
        var content = new GUIContent(text);
        var size = _banner.CalcSize(content);
        GUI.Box(new Rect((Screen.width - size.x - 24f) / 2f, 8f, size.x + 24f, size.y + 8f), content, _banner);
    }

    private static void Judge(PlayerCharacter p1, PlayerCharacter p2)
    {
        var label = $"{_case.Fish} -> {(_case.TargetP2 ? "P2" : "P1")}";
        var name = _fishName ?? "-";
        if (!_spawned)
        {
            if (!_mutation)
                _missing.Add(_case.Fish);
            _skip++;
            Result("SKIP", label, "not spawnable in this level");
            return;
        }
        // Normal breathing costs a few points in 10 s; more than that is a hit.
        const float Hit = 10f;
        var lost1 = _lost1;
        var lost2 = _lost2;
        var attacked = _events.Count > 0 || lost1 > Hit || lost2 > Hit;
        var problems = new List<string>();
        var notes = new List<string>();
        if (p1.IsActionLock || p1.IsMoveLocked)
            problems.Add($"P1 still locked 4 s after the creature left (action={p1.IsActionLock} move={p1.IsMoveLocked})");
        if (p2.IsActionLock || p2.IsMoveLocked)
            problems.Add($"P2 still locked 4 s after the creature left (action={p2.IsActionLock} move={p2.IsMoveLocked})");
        try
        {
            var hud = Object.FindFirstObjectByType<MainCanvasManager>();
            if (hud != null && !hud.IsHudOn)
                problems.Add("HUD still hidden 4 s after the creature left");
        }
        catch
        {
        }
        // Sharks hit whoever is in reach, so damage to the other diver is a
        // note, not a failure.
        if (_case.TargetP2 ? lost1 > Hit : lost2 > Hit)
            notes.Add("other diver was hit too");
        if (_lockedDuring)
            notes.Add("a diver was locked while the creature was there (recovered after it left)");
        // A creature may switch to P1 after its grab on P2 is skipped; that's
        // the base game's choice, so only a note.
        if (_case.TargetP2 && _events.Exists(e => e.Contains("attach to mouth: P1")))
            notes.Add("creature turned to P1 and grabbed him");
        if (_case.TargetP2 && _events.Exists(e => e.Contains("attach to mouth: P2")))
            problems.Add("P2 was grabbed (grabs on P2 should be skipped)");
        if (_case.TargetP2 && !_mutation)
        {
            var held = _events.Exists(e => e.Contains("P2 held by"));
            var skippedOnP2 = _events.Exists(e => e.Contains("skipped (aimed at P2)"));
            if (held)
            {
                notes.Add("P2 was held by the mod's grab");
                if (!_events.Exists(e => e.Contains("P2 broke free")))
                    problems.Add("P2 was held but did not break free by mashing");
                if (_p1LockedInHold)
                    problems.Add("P1 was locked while P2 was held");
                if (_hudOffInHold)
                    problems.Add("HUD was hidden while P2 was held");
            }
            else if (skippedOnP2)
                problems.Add("a grab on P2 was skipped but P2 was not held (no real P2 grab)");
        }
        foreach (var e in _events)
            if (e.Contains("teleported") && !(_case.TargetP2 && e.StartsWith("P2") && e.Contains("while P2 was held")) && !e.Contains("P2 teleported") )
            {
                problems.Add("a diver was teleported: " + e);
                break;
            }
        foreach (var e in _events)
            if (e.Contains("with nothing near"))
            {
                problems.Add("a diver was hurt with no creature near: " + e);
                break;
            }
        if (problems.Count > 0 && _lockedDuring)
            notes.Remove("a diver was locked while the creature was there (recovered after it left)");
        var detail = $"{name}: {(notes.Count > 0 ? "[" + string.Join("; ", notes) + "] " : "")}P1 -{lost1:F0} O2, P2 -{lost2:F0} O2; events: {(_events.Count == 0 ? "none" : string.Join(" | ", _events))}; skills: {_skillInfo}";
        if (problems.Count > 0)
        {
            _fail++;
            Result("FAIL", label, string.Join("; ", problems) + " || " + detail);
        }
        else if (!attacked)
        {
            _noAttack++;
            Result("NO-ATTACK", label, $"{(_forced ? "grab forced but nothing happened" : "no grab attack found")} || " + detail);
        }
        else
        {
            _pass++;
            Result("PASS", label, detail);
        }
    }

    private static void Cleanup(PlayerCharacter p1, PlayerCharacter p2)
    {
        if (_fishObj != null)
            Object.Destroy(_fishObj);
        _fishObj = null;
        _fish = null;
        Reset(p1);
        Reset(p2);
    }

    private static void Finish(string why)
    {
        _active = false;
        _cases.Clear();
        _case = null;
        NoSharkGrab.TestOff = false;
        if (_log != null)
        {
            var verdict = _fail > 0 ? "FAIL" : (_pass > 0 ? "PASS" : "NO RESULT");
            Write($"RESULT: {verdict} - {_pass} pass, {_fail} fail, {_noAttack} no-attack, {_skip} skipped ({why})");
            if (_mutation)
                Write(_fail > 0
                    ? "Mutation check OK: with the grab rules off the suite fails, so these tests do detect the fix."
                    : "WARNING: with the grab rules off nothing failed - these tests cannot see the fix.");
            _log.Dispose();
            _log = null;
        }
        Plugin.Logger.LogInfo($"Test bot: {why} - {_pass} pass, {_fail} fail, {_noAttack} no-attack, {_skip} skipped");
        DecideTravel();
        if (_travelTo != null)
            return;
        AutoFinished();
    }

    private static void Result(string verdict, string label, string detail)
    {
        var line = $"{verdict,-9} {label}: {detail}";
        _results.Add(line);
        Write(line);
        Plugin.Logger.LogInfo($"Test bot: {verdict} {label}");
    }

    private static void Write(string line) => _log?.WriteLine(line);

    private static void Reset(PlayerCharacter diver)
    {
        try
        {
            var tank = diver.BreathHandler;
            if (tank != null)
                tank.HP = tank.MaxHP;
            try { diver.ForceReleaseCommonQTE(); } catch { }
            try { diver.DetachFollowing(); } catch { }
            if (diver.IsMoveLocked)
                diver.UnlockMove();
            if (diver.IsActionLock)
                diver.SetActionLock(false);
        }
        catch
        {
        }
    }

    // Every spot a test uses must be free of solid (non-trigger) colliders
    // that aren't a diver or a fish.
    private static readonly List<string> _blockers = new();

    private static bool Clear(Vector2 at)
    {
        var hits = Physics2D.OverlapCircleAll(at, 1.2f);
        var clear = true;
        foreach (var hit in hits)
        {
            if (hit == null || hit.isTrigger)
                continue;
            if (hit.GetComponentInParent<PlayerCharacter>() != null || hit.GetComponentInParent<DR.AI.FishAISystem>() != null)
                continue;
            // Helpers that follow the divers around (e.g. GroundPredictor) are not rock.
            if (hit.name.Contains("Predictor") || !hit.name.Contains("Coll") && hit.bounds.size.magnitude < 5f)
                continue;
            // Huge colliders are area volumes (water, zones), not rock.
            var size = hit.bounds.size;
            if (size.x > 40f && size.y > 40f)
                continue;
            if (_blockers.Count < 12)
                _blockers.Add($"{hit.name} ({hit.GetIl2CppType().Name}, {size.x:F0}x{size.y:F0}) at {at}");
            clear = false;
        }
        return clear;
    }

    private static bool FindSpot(Vector3 start, out Vector3 spot)
    {
        _blockers.Clear();
        // Close to where player 1 is first (just under the surface is fine:
        // KeepDown stops divers being pushed up to it).
        var offsets = new[]
        {
            new Vector2(0, -3), new Vector2(0, -1), new Vector2(0, -5), new Vector2(0, -8), new Vector2(5, -4), new Vector2(-5, -4),
            new Vector2(8, -6), new Vector2(-8, -6), new Vector2(0, -11), new Vector2(12, -8), new Vector2(-12, -8),
            new Vector2(10, -4), new Vector2(15, -4), new Vector2(20, -6), new Vector2(10, -10), new Vector2(15, -12),
        };
        foreach (var o in offsets)
        {
            var b = new Vector2(start.x + o.x, start.y + o.y);
            if (Clear(b) && Clear(b + new Vector2(-12f, 0f)) && Clear(b + new Vector2(4f, 0f)) && Clear(b + new Vector2(1.5f, 0f)))
            {
                spot = new Vector3(b.x, b.y, start.z);
                return true;
            }
        }
        Write("Blocked by: " + string.Join("; ", _blockers));
        spot = start;
        return false;
    }

    // Creatures swim back to their home area when they're outside it, which
    // here means leaving without attacking. Keep the test creature on its
    // target instead; if it still wanders off, bring it back.
    private static float _nextLeash, _nextPull;

    private static void Leash(PlayerCharacter target)
    {
        if (_fish == null || _fishObj == null || target == null || _step > 1)
            return;
        try
        {
            _fish.IsNeedRunToHome = false;
            _fish.CountOfOutActivityArea = 0;
            if (Time.time >= _nextLeash)
            {
                _nextLeash = Time.time + 0.5f;
                _fish.StopRunToHome();
                _fish.Target = target.transform;
            }
            var d = Vector2.Distance(_fish.transform.position, target.transform.position);
            if (d > 12f && !P2Grab.Held && Time.time >= _nextPull)
            {
                _nextPull = Time.time + 3f;
                var side = _fish.transform.position.x >= target.transform.position.x ? 1f : -1f;
                _fishObj.transform.position = target.transform.position + new Vector3(7f * side, 0f, 0f);
                Write($"  .. creature wandered {d:F0} m off, brought back");
            }
        }
        catch
        {
        }
    }

    private static void NoteHabitats()
    {
        foreach (var (name, at) in FishSpawner.LoadedHabitats())
            foreach (var w in _wanted)
                if (!_habitat.ContainsKey(w) && name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _habitat[w] = at;
                    Write($"  .. found {w} ({name}) spawner at {at}");
                }
    }

    private static void Scout(PlayerCharacter p1, PlayerCharacter p2)
    {
        if (Time.time < _scoutAt)
            return;
        // Keep both alive down there.
        foreach (var d in new[] { p1, p2 })
            try { d.BreathHandler.HP = d.BreathHandler.MaxHP; } catch { }
        if (_scoutStep > 0)
            NoteHabitats();
        if (_scoutStep >= ScoutSteps || _wanted.TrueForAll(w => _habitat.ContainsKey(w)))
        {
            var missing = _wanted.FindAll(w => !_habitat.ContainsKey(w));
            Write($"  .. scouting done{(missing.Count > 0 ? "; not found anywhere: " + string.Join(", ", missing) : "")}");
            Place(p1, _base);
            Place(p2, _base + new Vector3(-12f, 0f, 0f));
            _scoutStep = -1;
            _scoutAt = Time.time + 3f;
            _stepAt = Time.time;
            return;
        }
        _scoutStep++;
        var at = _base + new Vector3(0f, -25f * _scoutStep, 0f);
        Place(p1, at);
        Place(p2, at + new Vector3(-3f, 0f, 0f));
        _scoutAt = Time.time + 1.5f;
    }

    private static void KeepDown(PlayerCharacter diver)
    {
        try
        {
            var at = diver.transform.position;
            if (at.y > _caseBase.y + 2f)
                Place(diver, new Vector3(at.x, _caseBase.y, at.z));
        }
        catch
        {
        }
    }

    private static void Place(PlayerCharacter diver, Vector3 at)
    {
        _placedFrame = Time.frameCount;
        diver.transform.position = at;
        var body = diver.GetComponent<Rigidbody2D>();
        if (body != null)
        {
            body.position = at;
            body.velocity = Vector2.zero;
        }
    }
}
