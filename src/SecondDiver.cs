using System;
using System.Text;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace DaveTheDiverCoop;

// Prototype: player 2 is a clone of Dave's PlayerCharacter whose input fields
// are written from player 2's keys instead of the game's input.
internal static class SecondDiver
{
    private static readonly Color Tint = new(0.78f, 0.62f, 1f, 1f);

    private static PlayerCharacter _p1;
    private static PlayerCharacter _p2;
    private static IntPtr _p2Pointer;
    private static DRInput.Handler_InGame _p2Handler;
    private static IntPtr _p2HandlerPointer;
    private static string _scene = string.Empty;
    private static float _p1SeenAt = -1f;
    private static bool _spawnFailed;
    private static bool _dumped;
    private static float _nextLog;
    private static float _dashSyncAt = -1f;
    private static int _traceFrames;
    private static int _fireLogs;
    private static float _nextCallbackCheck;
    private static float _nextP1InputLog;
    private static string _status = "not in a dive";

    internal static string Status => _status;
    internal static bool IsPlayer2(PlayerCharacter character) =>
        _p2Pointer != IntPtr.Zero && character != null && character.Pointer == _p2Pointer;
    internal static PlayerCharacter Player2 => _p2;
    internal static PlayerCharacter Player1 => _p1;

    internal static void Tick(bool joined)
    {
        var scene = SceneManager.GetActiveScene().name;
        if (scene != _scene)
        {
            _scene = scene;
            Despawn("scene changed");
            _spawnFailed = false;
            _p1SeenAt = -1f;
        }
        NightBox.Tick(_p2);
        try { GrabAttach.Tick(); } catch (Exception exception) { Plugin.Logger.LogWarning($"Grab tick: {exception.Message}"); }
        if (_dashSyncAt > 0f && Time.realtimeSinceStartup >= _dashSyncAt)
        {
            _dashSyncAt = -1f;
            if (_p2 != null)
                SyncDashUI(); // again, after the game's own late HUD setup
        }
        if (!joined)
        {
            if (_p2 != null)
                Despawn("player 2 left");
            _status = "player 2 not joined";
            P2Dispatch.Clear();
            return;
        }

        PlayerCharacter p1;
        try
        {
            p1 = InGameManager.TryGetPlayerCharacter;
        }
        catch
        {
            p1 = null;
        }
        if (p1 == null || p1.Pointer == _p2Pointer)
        {
            _status = _p2 == null ? "waiting for a dive" : _status;
            return;
        }
        _p1 = p1;
        try
        {
            if (_p2 == null)
                SchemeGuard.Verify(_p1);
            SchemeGuard.Repair(_p1);
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning($"Control mode repair failed: {exception.Message}");
        }
        if (_p1SeenAt < 0f)
            _p1SeenAt = Time.realtimeSinceStartup;

        if (_p2 == null)
        {
            _p2Pointer = IntPtr.Zero;
            if (_spawnFailed)
                return;
            // Wait until player 1 is really swimming (not in the dive-entry
            // sequence, where the body is frozen and the animation paused),
            // and has been for a moment.
            if (!PlayerOneIsSwimming(p1))
            {
                _p1SeenAt = Time.realtimeSinceStartup;
                _status = "waiting for player 1 to start swimming";
                return;
            }
            if (Time.realtimeSinceStartup - _p1SeenAt < 1.5f)
            {
                _status = "spawning soon";
                return;
            }
            Spawn(p1);
            return;
        }

        DiveIn.Tick();
        BeginPlayer2Turn(_p2);
        try
        {
            if (DispatchEnabled && !DiveIn.Active && !P2Grab.Held)
                P2Dispatch.Dispatch(_p2);
            // Button-mash moments (like breaking free from a shark bite) also
            // listen to the input system's callback, so forward player 2's
            // presses to them directly.
            if (DispatchEnabled && !DiveIn.Active && !P2Grab.Held &&
                (P2Keys.Pressed(P2Keys.Action.Ok) || P2Keys.Pressed(P2Keys.Action.Back) || P2Keys.Pressed(P2Keys.Action.Interact)))
            {
                try
                {
                    var sub = _p2.CurrentSubSMBState;
                    var qte = _p2.IsBattleQTEState || sub == PlayerSubSMBState.BattleQTE;
                    if (sub != PlayerSubSMBState.LocoMotion && _qteLogs++ < 12)
                        Plugin.Logger.LogInfo($"P2 button in state {sub}: battleQTE={_p2.IsBattleQTEState} ableToQTE={_p2.IsAbleToQTE} -> {(qte ? "sent to QTE" : "ignored")}");
                    if (qte)
                        _p2.OnQTE_Performed();
                }
                catch (Exception exception)
                {
                    if (_qteLogs++ < 12)
                        Plugin.Logger.LogWarning($"P2 QTE press failed: {exception.Message}");
                }
            }
            // The short dash is started by the input system's own callback, not
            // by Dave's per-frame button check, so player 2's needs a direct call.
            if (DispatchEnabled && !DiveIn.Active && !P2Grab.Held && P2Keys.Pressed(P2Keys.Action.ShortDash))
            {
                try
                {
                    _p2.OnShortDash_Performed();
                    // If the game's own check passed but nothing started (the
                    // dash cooldown did not begin), start the dash directly.
                    var dash = _p2.GetComponent<ShortDashAbility>();
                    var started = dash?.dashCooldown?.IsActive == true;
                    if (!started && _p2.IsLocomotion && _p2.ShortDashAbilityTriggerAction != null)
                    {
                        _p2.ShortDashAbilityTriggerAction.Invoke();
                        started = dash?.dashCooldown?.IsActive == true;
                        Plugin.Logger.LogInfo($"P2 short dash: started directly (cooldown now {(started ? "running" : "still off")}, clip {ShortDashProbe.Clip(_p2)})");
                    }
                }
                catch (Exception exception)
                {
                    Plugin.Logger.LogWarning($"P2 short dash failed: {exception.Message}");
                }
            }
            AdvanceAnimator();
            if (P2Keys.Pressed(P2Keys.Action.Ok) || P2Keys.Released(P2Keys.Action.Ok))
                Plugin.Logger.LogInfo($"P2 aim {(P2Keys.Pressed(P2Keys.Action.Ok) ? "pressed" : "released")}: " + DescribeVisuals());
            if (DispatchEnabled && P2Keys.Released(P2Keys.Action.Ok) && !P2Keys.Held(P2Keys.Action.Fire))
            {
                try
                {
                    _p2.OnCancelRangeAttack();
                }
                catch (Exception exception)
                {
                    Plugin.Logger.LogWarning($"Aim cancel failed: {exception.Message}");
                }
            }
        }
        finally
        {
            EndPlayer2Turn(_p2);
        }
        ApplyInput(_p2);
        TracePhase(_p2, "coop-update");
        if (_traceFrames == 1)
            Plugin.Logger.LogInfo("Visuals 20 frames after spawn: " + DescribeVisuals());
        if (_traceFrames > 0)
        {
            _traceFrames--;
            Plugin.Logger.LogInfo(
                $"P2 trace frame={Time.frameCount}: p2={_p2.transform.position} p2body={_p2.Controller2D?.GetPosition()} " +
                $"p1={_p1.transform.position} p2move={_p2.moveInput}");
        }
        LogPlayerOneInput();
        LogPlayerOneAim();
        var p1Fired = UnityEngine.InputSystem.Mouse.current?.leftButton.wasPressedThisFrame == true;
        if ((p1Fired || P2Keys.Pressed(P2Keys.Action.Fire)) && _fireLogs < 12)
        {
            _fireLogs++;
            Plugin.Logger.LogInfo($"Fire pressed by {(p1Fired ? "P1" : "P2")}: " + DescribeHarpoons());
            Plugin.Logger.LogInfo("Visuals at fire: " + DescribeVisuals());
        }
        CoopCamera.Tick(_p1, _p2);
        SharedOxygen.Mirror();
        P2Oxygen.Tick(_p1, _p2);
        ShortDashProbe.Tick(_p1, _p2);
        GunProbe.Tick(_p1, _p2);
        GunAimFix.Tick(_p1, _p2);
        GuideDump.Tick(_p1, _p2);
        MeleeTrace.Tick();
        WatchMelee();
        WatchInteraction();
        ProbeInteraction();
        KeepPlayerOneAuthoritative();
        RestorePlayerOneInput(false);
        if (Time.realtimeSinceStartup >= _nextCallbackCheck)
        {
            // The clone's Start() runs a frame later and may grab slots again.
            _nextCallbackCheck = Time.realtimeSinceStartup + 0.5f;
            ActionCallbacks.Restore("periodic");
        }
        if (Time.realtimeSinceStartup >= _nextLog)
        {
            _nextLog = Time.realtimeSinceStartup + 2f;
            Plugin.Logger.LogInfo(
                $"P2 diver: pos={_p2.transform.position} move={_p2.moveInput} aim={_p2.aimInputRight} " +
                $"keys={P2Keys.Move} camera=[{CoopCamera.Describe()}] state=[{DescribeState(_p2)}] locked={_p2.IsMoveLocked} vel={_p2.GetComponent<Rigidbody2D>()?.velocity} | " +
                $"P1 pos={_p1.transform.position} move={_p1.moveInput} locked={_p1.IsMoveLocked} " +
                $"input={DescribeInputTarget()} " +
                $"igmPlayerIsP1={InGameManager.TryGetPlayerCharacter?.Pointer == _p1.Pointer}");
        }
    }

    private static void Spawn(PlayerCharacter p1)
    {
        GameObject holder = null;
        try
        {
            _status = "spawning";
            if (!_dumped)
            {
                _dumped = true;
                Plugin.Logger.LogInfo("Player 1 hierarchy:\n" + Describe(p1.gameObject));
            }

            // Clone under an inactive holder so nothing on the copy runs until
            // it is placed in the world.
            holder = new GameObject("Coop P2 Holder");
            holder.SetActive(false);
            var spawnAt = p1.transform.position + new Vector3(1.5f, 0f, 0f);
            var clone = Object.Instantiate(p1.gameObject, spawnAt, p1.transform.rotation, holder.transform);
            clone.name = "Dave (Player 2)";
            Plugin.Logger.LogInfo("Clone created (inactive); activating");

            // Switch the clone's input receiver off *before* it ever starts,
            // so it never takes player 1's controls.
            var cloneHandler = clone.GetComponent<DRInput.Handler_InGame>();
            if (cloneHandler != null)
                cloneHandler.enabled = false;

            ActionCallbacks.Snapshot(p1.inputAsset);
            clone.transform.SetParent(p1.transform.parent, true);
            ActionCallbacks.Restore("clone started");
            PlaceAt(clone, spawnAt);
            Object.Destroy(holder);
            holder = null;

            _p2 = clone.GetComponent<PlayerCharacter>();
            if (_p2 == null)
                throw new InvalidOperationException("clone has no PlayerCharacter");
            _p2Pointer = _p2.Pointer;

            _p2Handler = cloneHandler;
            _p2HandlerPointer = _p2Handler?.Pointer ?? IntPtr.Zero;
            Plugin.Logger.LogInfo(
                $"Clone state: initialized={_p2.IsInitialized} handlerEnabled={_p2Handler?.enabled} " +
                $"input={DescribeInputTarget()}");
            // Init() repositions the shared player group (it moved both divers),
            // so only mark the clone initialized.
            if (!_p2.IsInitialized)
                _p2.IsInitialized = true;
            RestorePlayerOneInput(false);
            if (_p2.IsMoveLocked)
                _p2.UnlockMove();
            if (_p2.locationState != p1.locationState)
            {
                Plugin.Logger.LogInfo($"Clone location {_p2.locationState} -> {p1.locationState}");
                _p2.ChangeLocationState(p1.locationState);
            }
            PlaceAt(clone, spawnAt);
            Plugin.Logger.LogInfo($"Spawn steps: rewire={P2Keys.Step("rewire")} animator={P2Keys.Step("animator")} " +
                $"harpoon={P2Keys.Step("harpoon")} equip={P2Keys.Step("equip")} armInit={P2Keys.Step("armInit")}");
            if (P2Keys.Step("animator"))
            {
                SeparateAnimatorScripts(p1, _p2);
                CopyAnimator(p1, _p2);
            }
            StopAnimatorMovingClone(p1, _p2);
            FixSharedEffects(p1, _p2);
            // Player 2's animator is advanced by the mod inside player 2's turn
            // so its animation events (show gun, hide aim guide, ...) see
            // player 2 as "the player" instead of player 1.
            _p2Animator = _p2.characterAnimator;
            if (_p2Animator != null && _manualAnimator)
                _p2Animator.enabled = false;
            Plugin.Logger.LogInfo("State P1: " + DescribeState(p1));
            Plugin.Logger.LogInfo("State P2: " + DescribeState(_p2));
            Plugin.Logger.LogInfo(
                $"Clone state after init: initialized={_p2.IsInitialized} moveLocked={_p2.IsMoveLocked} " +
                $"pos={clone.transform.position}");

            foreach (var renderer in clone.GetComponentsInChildren<SpriteRenderer>(true))
                renderer.color *= Tint;

            IgnoreCollisionsWithPlayerOne(p1, clone);
            var cloneBody = clone.GetComponent<Rigidbody2D>();
            Plugin.Logger.LogInfo(
                $"Clone parent={clone.transform.parent?.name ?? "(root)"} constraints={cloneBody?.constraints} " +
                $"interpolation={cloneBody?.interpolation} joints={clone.GetComponentsInChildren<Joint2D>(true).Length} " +
                $"p1Parent={p1.transform.parent?.name ?? "(root)"}");
            foreach (var joint in clone.GetComponentsInChildren<Joint2D>(true))
                Plugin.Logger.LogInfo(
                    $"Clone joint {joint.name}/{joint.GetIl2CppType().Name} connected={joint.connectedBody?.name} " +
                    $"connectedIsP1={joint.connectedBody != null && joint.connectedBody.transform.IsChildOf(p1.transform)}");
            if (P2Keys.Step("rewire"))
            {
                RewireReferences(p1, clone, _p2);
                RelinkConstraints(clone, p1, clone);
            }
            Plugin.Logger.LogInfo("Harpoons before equip: " + DescribeHarpoons());
            if (P2Keys.Step("harpoon"))
                GiveOwnHarpoon(p1, _p2);
            P2Hud.Create(_p2);
            if (P2Keys.Step("equip"))
            {
                // Inside player 2's turn so anything that asks "who is the
                // player" during equipping gets player 2.
                BeginPlayer2Turn(_p2);
                try
                {
                    EquipClone(_p2);
                }
                finally
                {
                    EndPlayer2Turn(_p2);
                }
                if (P2Keys.Step("rewire"))
                    RewireReferences(p1, clone, _p2);
            }
            P2Oxygen.Setup(p1, _p2);
            ApplyCharms(_p2);
            SyncDashUI();
            _dashSyncAt = Time.realtimeSinceStartup + 1.5f;
            Plugin.Logger.LogInfo("Harpoons after equip: " + DescribeHarpoons());
            Plugin.Logger.LogInfo("Visuals after equip: " + DescribeVisuals());
            ReportCrossReferences(p1.gameObject, clone);
            _traceFrames = 20;
            _phaseLogs = 0;

            KeepPlayerOneAuthoritative();
            ActionCallbacks.Restore("clone set up");
            var camera = Object.FindFirstObjectByType<global::CameraManager>();
            if (camera != null && CameraSecondTarget)
            {
                camera.AddSecondTarget(clone.transform);
                Plugin.Logger.LogInfo("Camera now follows both divers");
            }
            else
            {
                Plugin.Logger.LogWarning("No CameraManager found; camera follows player 1 only");
            }
            _status = "player 2 diving";
            HudDump.ScheduleAfterSpawn();
            DiveIn.Start(_p2);
            GunAimFix.Reset();
            Plugin.Logger.LogInfo($"Player 2 diver spawned at {clone.transform.position}");
        }
        catch (Exception exception)
        {
            _spawnFailed = true;
            _status = "spawn failed (see log)";
            Plugin.Logger.LogError($"Player 2 spawn failed: {exception}");
            if (holder != null)
                Object.Destroy(holder);
            Despawn("spawn failed");
        }
    }

    // If the game's current input target is player 2's clone (or nothing,
    // right after spawning), point it back at player 1. Menus and dialogue
    // set their own targets; those are left alone.
    private static void RestorePlayerOneInput(bool force)
    {
        if (_p1 == null)
            return;
        try
        {
            var asset = _p1.inputAsset;
            var p1Handler = _p1.handler;
            if (asset == null || p1Handler == null)
                return;
            var current = asset.currentHandler;
            var takenByClone = current != null && _p2HandlerPointer != IntPtr.Zero &&
                               current.Pointer == _p2HandlerPointer;
            if (!takenByClone && !(force && current == null))
                return;
            // Re-enabling player 1's receiver makes the game register it again
            // the normal way (setting the target directly did not reconnect it).
            if (_p2Handler != null)
                _p2Handler.enabled = false;
            p1Handler.enabled = false;
            p1Handler.enabled = true;
            Plugin.Logger.LogInfo($"Input target given back to player 1 (now {DescribeInputTarget()})");
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning($"Input target restore failed: {exception.Message}");
        }
    }

    private static string DescribeInputTarget()
    {
        try
        {
            var current = _p1?.inputAsset?.currentHandler;
            if (current == null)
                return "none";
            if (current.Pointer == _p1.handler?.Pointer)
                return "P1";
            if (current.Pointer == _p2HandlerPointer)
                return "P2-clone";
            return current.name;
        }
        catch
        {
            return "?";
        }
    }

    private static bool PlayerOneIsSwimming(PlayerCharacter p1)
    {
        try
        {
            var body = p1.GetComponent<Rigidbody2D>();
            var animator = p1.characterAnimator;
            return p1.IsInitialized && !p1.IsMoveLocked && body != null &&
                   body.bodyType == RigidbodyType2D.Dynamic && animator != null && animator.speed > 0f &&
                   p1.locationState == PlayerLocationState.Sea;
        }
        catch
        {
            return false;
        }
    }

    // The game must keep treating player 1 as "the player".
    private static void KeepPlayerOneAuthoritative()
    {
        if (_p1 == null)
            return;
        _swapDepth = 0;
        RestoreScheme();
        var manager = Object.FindFirstObjectByType<InGameManager>();
        if (manager == null)
            return;
        var current = manager.playerCharacter;
        if (current == null || current.Pointer != _p1.Pointer)
        {
            manager.playerCharacter = _p1;
            Plugin.Logger.LogWarning("InGameManager player was changed by the clone; restored player 1");
        }
    }

    internal static void Despawn(string reason)
    {
        var p2 = _p2;
        _p2 = null;
        _p2Pointer = IntPtr.Zero;
        _p2Handler = null;
        _p2Body = null;
        _p2Animator = null;
        _manager = null;
        if (p2 == null)
            return;
        try
        {
            var camera = Object.FindFirstObjectByType<global::CameraManager>();
            if (camera != null && camera.IsSecondTarget(p2.transform))
                camera.RemoveSecondTarget();
            CoopCamera.Stop(_p1);
            DiveIn.Finish();
            P2Hud.Destroy();
            BiteRedirect.Clear();
            GrabAttach.Clear();
            P2Oxygen.Destroy();
            ShortDashProbe.Reset();
            Object.Destroy(p2.gameObject);
            if (_p2Projectile != null)
                Object.Destroy(_p2Projectile);
            if (_p2Rope != null)
                Object.Destroy(_p2Rope);
            _p2Projectile = null;
            _p2Rope = null;
            Plugin.Logger.LogInfo($"Player 2 diver removed: {reason}");
            ActionCallbacks.Restore("player 2 removed");
            RestorePlayerOneInput(true);
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning($"Player 2 removal: {exception.Message}");
        }
        KeepPlayerOneAuthoritative();
        _p2HandlerPointer = IntPtr.Zero;
    }

    // Called around the clone's own Update/FixedUpdate so whatever the game
    // read from player 1's controls is replaced with player 2's.
    private static int _phaseLogs;
    private static Rigidbody2D _p2Body;

    // Something that runs after Update (the clone's animator) keeps writing
    // the clone back to its spawn spot; the physics body moves correctly, so
    // put the transform back on the body once that has happened.
    internal static void LateSync()
    {
        if (_p2 == null)
            return;
        if (_p2Body == null)
            _p2Body = _p2.GetComponent<Rigidbody2D>();
        if (_p2Body == null)
            return;
        var current = _p2.transform.position;
        var body = _p2Body.position;
        if (Mathf.Abs(current.x - body.x) > 0.0001f || Mathf.Abs(current.y - body.y) > 0.0001f)
            _p2.transform.position = new Vector3(body.x, body.y, current.z);
    }

    internal static void TracePhase(PlayerCharacter character, string phase)
    {
        if (_p2Pointer == IntPtr.Zero || character == null || character.Pointer != _p2Pointer)
            return;
        if (P2Keys.Move == Vector2.zero || _phaseLogs >= 120)
            return;
        _phaseLogs++;
        var body = character.GetComponent<Rigidbody2D>();
        Plugin.Logger.LogInfo(
            $"P2 phase {phase} frame={Time.frameCount}: transform={character.transform.position.x:F3},{character.transform.position.y:F3} " +
            $"body={(body != null ? body.position.x : 0f):F3},{(body != null ? body.position.y : 0f):F3} " +
            $"vel={(body != null ? body.velocity : Vector2.zero)} local={character.transform.localPosition.x:F3}");
    }

    internal static void ApplyInput(PlayerCharacter character)
    {
        if (_p2Pointer == IntPtr.Zero || character == null || character.Pointer != _p2Pointer)
            return;
        // While aiming, Dave holds still and the stick only turns the aim
        // (otherwise the swim input kept pushing player 2 along).
        var aiming = P2Keys.Held(P2Keys.Action.Ok);
        if (!aiming)
        {
            try
            {
                aiming = character.GetRangeActionState.ToString() == "Aim";
            }
            catch
            {
            }
        }
        character.moveInput = aiming ? Vector2.zero : P2Keys.Move;
        // Bleed off the leftover swimming speed quickly when aiming starts.
        if (aiming && _p2Body != null)
        {
            var velocity = _p2Body.velocity;
            if (velocity.sqrMagnitude > 0.0004f)
                _p2Body.velocity = velocity * Mathf.Pow(0.02f, Time.deltaTime);
        }
        var aim = P2Keys.Aim;
        character.aimInput = aim;
        character.aimInputRight = aim;
    }

    // Move the transform *and* the physics body; otherwise the next physics
    // step pulls the clone back to where it was created.
    private static void PlaceAt(GameObject clone, Vector3 position)
    {
        clone.transform.position = position;
        var body = clone.GetComponent<Rigidbody2D>();
        if (body != null)
        {
            body.position = position;
            body.velocity = Vector2.zero;
        }
        clone.GetComponent<PlayerCharacter>()?.Controller2D?.SetPosition(position);
        Physics2D.SyncTransforms();
    }

    // The clone's animator writes the character's root position back to the
    // spawn spot every frame (before effects like bubbles are placed). With
    // root motion on, those constant root curves become zero movement instead.
    private static void StopAnimatorMovingClone(PlayerCharacter p1, PlayerCharacter p2)
    {
        var a1 = p1.characterAnimator;
        var a2 = p2.characterAnimator;
        Plugin.Logger.LogInfo(
            $"Animators: P1 rootMotion={a1?.applyRootMotion} update={a1?.updateMode} culling={a1?.cullingMode} " +
            $"onRoot={a1 != null && a1.gameObject == p1.gameObject} | " +
            $"P2 rootMotion={a2?.applyRootMotion} onRoot={a2 != null && a2.gameObject == p2.gameObject}");
        if (a2 != null && _rootMotionFix)
            a2.applyRootMotion = true;
    }

    private static bool _rootMotionFix = true;

    // Test key F11: switch the root-motion fix on/off to confirm the cause.
    internal static void ToggleRootMotionFix()
    {
        _rootMotionFix = !_rootMotionFix;
        var animator = _p2?.characterAnimator;
        if (animator != null)
            animator.applyRootMotion = _rootMotionFix;
        Plugin.Logger.LogInfo($"Root motion fix {(_rootMotionFix ? "on" : "off")}");
    }

    // Dave's aiming lives in state-machine scripts on his animation
    // controller. If those are shared between animators, both divers run one
    // aiming script. Give player 2 its own copy of the controller in that case.
    // Hit flashes / blood on one diver showing on the other: check that player
    // 2's effect handler is its own, not player 1's.
    private static void FixSharedEffects(PlayerCharacter p1, PlayerCharacter p2)
    {
        try
        {
            var e1 = p1.effectHandler;
            var e2 = p2.effectHandler;
            var own = p2.GetComponentInChildren<CharacterEffectHandler>(true);
            var shared = e1 != null && e2 != null && e1.Pointer == e2.Pointer;
            var foreign = e2 != null && !e2.transform.IsChildOf(p2.transform);
            Plugin.Logger.LogInfo($"Effects: P1 handler={e1?.name} P2 handler={e2?.name} shared={shared} notP2s={foreign} P2 has own={own != null}");
            if ((shared || foreign || e2 == null) && own != null)
            {
                p2.effectHandler = own;
                Plugin.Logger.LogInfo("Effects: player 2 now uses its own effect handler");
            }
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogInfo($"Effects check failed: {exception.Message}");
        }
    }

    private static void SeparateAnimatorScripts(PlayerCharacter p1, PlayerCharacter p2)
    {
        try
        {
            var a1 = p1.characterAnimator;
            var a2 = p2.characterAnimator;
            if (a1 == null || a2 == null)
                return;
            var shared = CountSharedBehaviours(a1, a2, out var total);
            Plugin.Logger.LogInfo($"Animator scripts: {shared} of {total} shared between the divers");
            if (shared == 0)
                return;
            var controller = Object.Instantiate(a1.runtimeAnimatorController);
            controller.name = a1.runtimeAnimatorController.name + " (Player 2)";
            a2.runtimeAnimatorController = controller;
            a2.Rebind();
            shared = CountSharedBehaviours(a1, a2, out total);
            Plugin.Logger.LogInfo($"Animator scripts after giving player 2 its own controller: {shared} of {total} shared");
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning($"Animator script check failed: {exception.Message}");
        }
    }

    private static int CountSharedBehaviours(Animator a1, Animator a2, out int total)
    {
        var mine = a1.GetBehaviours<StateMachineBehaviour>();
        var theirs = a2.GetBehaviours<StateMachineBehaviour>();
        total = theirs.Length;
        var pointers = new System.Collections.Generic.HashSet<IntPtr>();
        foreach (var behaviour in mine)
            if (behaviour != null)
                pointers.Add(behaviour.Pointer);
        var shared = 0;
        foreach (var behaviour in theirs)
            if (behaviour != null && pointers.Contains(behaviour.Pointer))
                shared++;
        return shared;
    }

    // Start the clone's animation state machine where player 1's is; Dave's
    // swimming is driven by the animator's state.
    private static void CopyAnimator(PlayerCharacter from, PlayerCharacter to)
    {
        try
        {
            var source = from.characterAnimator;
            var target = to.characterAnimator;
            if (source == null || target == null)
                return;
            foreach (var parameter in source.parameters)
            {
                switch (parameter.type)
                {
                    case AnimatorControllerParameterType.Float:
                        target.SetFloat(parameter.nameHash, source.GetFloat(parameter.nameHash));
                        break;
                    case AnimatorControllerParameterType.Int:
                        target.SetInteger(parameter.nameHash, source.GetInteger(parameter.nameHash));
                        break;
                    case AnimatorControllerParameterType.Bool:
                        target.SetBool(parameter.nameHash, source.GetBool(parameter.nameHash));
                        break;
                }
            }
            for (var layer = 0; layer < source.layerCount && layer < target.layerCount; layer++)
            {
                var info = source.GetCurrentAnimatorStateInfo(layer);
                target.Play(info.fullPathHash, layer, info.normalizedTime % 1f);
            }
            target.Update(0f);
            Plugin.Logger.LogInfo($"Clone animator copied from player 1 ({source.parameters.Length} parameters, {source.layerCount} layers)");
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning($"Animator copy failed: {exception.Message}");
        }
    }

    private static string DescribeState(PlayerCharacter character)
    {
        try
        {
            var body = character.GetComponent<Rigidbody2D>();
            var animator = character.characterAnimator;
            var info = animator != null ? animator.GetCurrentAnimatorStateInfo(0) : default;
            return $"location={character.locationState} action={character.PlayerActionState} " +
                   $"init={character.IsInitialized} moveLocked={character.IsMoveLocked} actionLock={character.IsActionLock} " +
                   $"body={body?.bodyType} simulated={body?.simulated} gravity={body?.gravityScale} " +
                   $"animState={info.fullPathHash} animSpeed={animator?.speed} animEnabled={animator?.enabled}";
        }
        catch (Exception exception)
        {
            return "state read failed: " + exception.Message;
        }
    }

    private static void IgnoreCollisionsWithPlayerOne(PlayerCharacter p1, GameObject clone)
    {
        var count = 0;
        foreach (var mine in clone.GetComponentsInChildren<Collider2D>(true))
        foreach (var theirs in p1.GetComponentsInChildren<Collider2D>(true))
        {
            Physics2D.IgnoreCollision(mine, theirs, true);
            count++;
        }
        Plugin.Logger.LogInfo($"Divers no longer collide with each other ({count} collider pairs)");
    }

    // Lists every field on the clone's components that still points at
    // something inside player 1 (Instantiate only remaps references inside the
    // copied hierarchy; anything wired up at runtime keeps pointing at P1).
    private static void ReportCrossReferences(GameObject p1Root, GameObject clone)
    {
        var found = 0;
        foreach (var component in clone.GetComponentsInChildren<Component>(true))
        {
            if (component == null)
                continue;
            object managed;
            try
            {
                managed = CastToManaged(component);
            }
            catch
            {
                managed = null;
            }
            var type = managed?.GetType();
            if (type == null)
                continue;
            foreach (var property in type.GetProperties(
                         System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
            {
                if (!typeof(Object).IsAssignableFrom(property.PropertyType) || property.GetIndexParameters().Length > 0)
                    continue;
                if (type.GetField("NativeFieldInfoPtr_" + property.Name,
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic) == null)
                    continue;
                try
                {
                    if (property.GetValue(managed) is not Object value || value == null)
                        continue;
                    var target = value.TryCast<Component>()?.transform ?? value.TryCast<GameObject>()?.transform;
                    if (target == null || !target.IsChildOf(p1Root.transform))
                        continue;
                    found++;
                    Plugin.Logger.LogWarning(
                        $"Clone {component.name}/{type.Name}.{property.Name} points at player 1's {target.name}");
                }
                catch
                {
                }
            }
        }
        Plugin.Logger.LogInfo($"Cross-reference scan done: {found} reference(s) into player 1");
    }

    // Re-point everything on the clone that still refers to player 1 (or to a
    // part of player 1) at the matching part of the clone, and give empty
    // "owner" references the clone. Weapons get their owner when player 1's
    // equipment loads, which never happens for the copy.
    private static void RewireReferences(PlayerCharacter p1, GameObject clone, PlayerCharacter p2) =>
        RewireReferences(p1, clone, clone, p2, true);

    private static void RewireReferences(
        PlayerCharacter p1, GameObject scanRoot, GameObject clone, PlayerCharacter p2, bool fillOwners)
    {
        var p1Root = p1.transform;
        var rewired = 0;
        var owners = 0;
        foreach (var component in scanRoot.GetComponentsInChildren<Component>(true))
        {
            if (component == null)
                continue;
            object managed;
            try
            {
                managed = CastToManaged(component);
            }
            catch
            {
                continue;
            }
            var type = managed?.GetType();
            if (type == null)
                continue;
            foreach (var property in type.GetProperties(
                         System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
            {
                if (!property.CanWrite || property.GetIndexParameters().Length > 0 ||
                    !typeof(Object).IsAssignableFrom(property.PropertyType))
                    continue;
                if (type.GetField("NativeFieldInfoPtr_" + property.Name,
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic) == null)
                    continue;
                try
                {
                    var value = property.GetValue(managed) as Object;
                    if (value == null)
                    {
                        if (fillOwners &&
                            (property.PropertyType == typeof(PlayerCharacter) || property.PropertyType == typeof(BaseCharacter)))
                        {
                            property.SetValue(managed, Activator.CreateInstance(property.PropertyType, p2.Pointer));
                            owners++;
                            Plugin.Logger.LogInfo($"Owner set: {component.name}/{type.Name}.{property.Name} -> player 2");
                        }
                        continue;
                    }
                    var target = value.TryCast<Component>()?.transform ?? value.TryCast<GameObject>()?.transform;
                    if (target == null || !target.IsChildOf(p1Root))
                        continue;
                    var path = RelativePath(p1Root, target);
                    var counterpart = path.Length == 0 ? clone.transform : clone.transform.Find(path);
                    if (counterpart == null)
                    {
                        Plugin.Logger.LogWarning($"No clone counterpart for {component.name}/{type.Name}.{property.Name} ({path})");
                        continue;
                    }
                    IntPtr pointer;
                    if (value.TryCast<GameObject>() != null)
                        pointer = counterpart.gameObject.Pointer;
                    else
                    {
                        var match = counterpart.GetComponent(value.GetIl2CppType());
                        if (match == null)
                            continue;
                        pointer = match.Pointer;
                    }
                    property.SetValue(managed, Activator.CreateInstance(property.PropertyType, pointer));
                    rewired++;
                    Plugin.Logger.LogInfo($"Rewired {component.name}/{type.Name}.{property.Name} -> clone {path}");
                }
                catch (Exception exception)
                {
                    Plugin.Logger.LogWarning($"Rewire {type.Name}.{property.Name} failed: {exception.Message}");
                }
            }
        }
        Plugin.Logger.LogInfo(
            $"{scanRoot.name} rewired: {rewired} reference(s) moved off player 1, {owners} owner(s) filled in");
    }

    // Run the game's own weapon setup on the clone: aiming arm, inventory and
    // the saved loadout, so it gets its own harpoon projectile and knife.
    private static void EquipClone(PlayerCharacter p2)
    {
        if (P2Keys.Step("armInit"))
            Try("aim arm", () => p2.RangeAttackArm?.Init(p2));
        var inventory = p2.GetComponent<InstanceItemInventory>();
        if (inventory == null)
        {
            Plugin.Logger.LogWarning("Clone has no item inventory");
            return;
        }
        Try("inventory init", () => inventory.Init());
        P2Loadout.WithPlayer2Slots("Player 2 gear", () => Try("saved loadout", () => inventory.EquipSavedSlotData()));
        CopyAbilities(p2);
        var subHelpers = p2.GetComponent<SubHelperItemInventory>();
        if (subHelpers != null)
        {
            Try("booster/helper init", () => subHelpers.Init(p2));
            P2Loadout.WithPlayer2Slots("Player 2 helpers", () => Try("booster/helper loadout", () => subHelpers.EquipSavedSlotData()));
            Plugin.Logger.LogInfo($"Clone helper items: focused={subHelpers.FocusedHandler?.GetType().Name ?? "none"} slots={subHelpers.subHelperSlots?.Length}");
        }
        else
            Plugin.Logger.LogWarning("Clone has no helper item inventory (booster)");
        Plugin.Logger.LogInfo(
            $"Clone equipment: loaded={inventory.IsInitialItemLoaded} harpoon={inventory.harpoonHandler?.CurrentEquipInfo != null} " +
            $"melee={inventory.meleeHandler?.CurrentEquipInfo != null} gun={inventory.gunHandler?.CurrentEquipInfo != null}");
    }

    // Charms: the game applies them (buffs + charm abilities) from the save's
    // charm slots when Dave is set up. The clone never ran that, so run it on
    // player 2 with player 2's charm slots in the save.
    private static void ApplyCharms(PlayerCharacter p2)
    {
        var breath = p2.BreathHandler;
        var before = breath != null ? $"{breath.HP:F0}/{breath.MaxHP:F0}" : "?";
        P2Loadout.WithPlayer2Slots("Player 2 charms", () =>
        {
            BeginPlayer2Turn(p2);
            try
            {
                Try("charm effects", () => p2.ApplyCharmEffect());
            }
            finally
            {
                EndPlayer2Turn(p2);
            }
        });
        var after = breath != null ? $"{breath.HP:F0}/{breath.MaxHP:F0}" : "?";
        Plugin.Logger.LogInfo($"Player 2 charms applied: oxygen {before} -> {after}");
    }

    // The short dash comes from a charm. Its gauge belongs on the oxygen panel
    // of whichever diver actually has it (applying player 2's charm showed it
    // on player 1's panel).
    internal static void SyncDashUI()
    {
        try
        {
            static bool Has(PlayerCharacter p) => p != null && p.GetComponent<ShortDashAbility>()?.OwnData != null;
            var p1Panel = _p1?.BreathHandler?.PlayerStateInfoUI;
            var p2Panel = P2Oxygen.Panel != null ? P2Oxygen.Panel.GetComponent<PlayerStateInfoPanelUI>() : null;
            var p1Has = Has(_p1);
            var p2Has = Has(_p2);
            if (p1Panel != null)
            {
                if (p1Has) p1Panel.ShowDashUI(); else p1Panel.HideDashUI();
            }
            if (p2Panel != null)
            {
                if (p2Has) p2Panel.ShowDashUI(); else p2Panel.HideDashUI();
            }
            Plugin.Logger.LogInfo($"Short dash gauge: player 1 {(p1Has ? "shown" : "hidden")}, player 2 {(p2Has ? "shown" : "hidden")} (p2 panel {(p2Panel != null)})");
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning($"Short dash gauge sync: {exception.Message}");
        }
    }

    // Dave's upgrades (long dash / boost, etc.) are "abilities" handed to him
    // from the save when the dive starts. The clone was made after that, so it
    // has none. Give player 2 a copy of each of player 1's abilities.
    private static void CopyAbilities(PlayerCharacter p2)
    {
        try
        {
            var from = _p1?.GetComponent<CharacterAbilityHandler>();
            var to = p2.GetComponent<CharacterAbilityHandler>();
            if (from == null || to == null)
            {
                Plugin.Logger.LogWarning($"Abilities: handler missing (p1={from != null} p2={to != null})");
                return;
            }
            var source = from.abilityDatas;
            var before = to.abilityDatas?.Count ?? -1;
            var copied = 0;
            var names = new System.Collections.Generic.List<string>();
            if (source != null)
            {
                foreach (var data in source.Values)
                {
                    if (data == null)
                        continue;
                    try
                    {
                        var copy = data.GetClone();
                        copy.Init(p2);
                        to.ApplyAbility(ref copy);
                        copied++;
                        names.Add(data.ToString());
                    }
                    catch (Exception exception)
                    {
                        names.Add($"{data.ToString()}(failed: {exception.Message})");
                    }
                }
            }
            var dash = p2.GetComponent<DashAbility>();
            Plugin.Logger.LogInfo(
                $"Abilities: player 1 has {source?.Count ?? -1}, player 2 had {before}, copied {copied}: {string.Join(", ", names)} | " +
                $"P2 dash data={(dash?.OwnData != null)} P1 dash data={(_p1.GetComponent<DashAbility>()?.OwnData != null)}");
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning($"Ability copy failed: {exception.Message}");
        }
    }

    private static GameObject _p2Projectile;
    private static GameObject _p2Rope;

    // Player 1's harpoon bolt and rope live outside Dave, so the clone pointed
    // at player 1's. Copy both together (so the copied rope links to the
    // copied bolt), re-attach the copy to player 2, and hand it to player 2's
    // harpoon before its equip step runs.
    private static void GiveOwnHarpoon(PlayerCharacter p1, PlayerCharacter p2)
    {
        try
        {
            var h1 = p1.GetComponent<InstanceItemInventory>()?.harpoonHandler;
            var h2 = p2.GetComponent<InstanceItemInventory>()?.harpoonHandler;
            var projectile = h1?.harpoonProjectile;
            if (h1 == null || h2 == null || projectile == null)
            {
                Plugin.Logger.LogWarning("Own harpoon: player 1 has no harpoon projectile to copy");
                return;
            }
            var projectileGo = projectile.gameObject;
            var ropeGo = h1.harpoonRope?.gameObject;
            var projectileParent = projectileGo.transform.parent;
            var ropeParent = ropeGo != null ? ropeGo.transform.parent : null;

            var group = new GameObject("Coop P2 Harpoon Copy");
            group.SetActive(false);
            projectileGo.transform.SetParent(group.transform, true);
            if (ropeGo != null)
                ropeGo.transform.SetParent(group.transform, true);
            GameObject copy;
            try
            {
                copy = Object.Instantiate(group);
            }
            finally
            {
                projectileGo.transform.SetParent(projectileParent, true);
                if (ropeGo != null)
                    ropeGo.transform.SetParent(ropeParent, true);
                Object.Destroy(group);
            }

            var projectileCopy = copy.transform.Find(projectileGo.name)?.gameObject;
            var ropeCopy = ropeGo != null ? copy.transform.Find(ropeGo.name)?.gameObject : null;
            if (projectileCopy == null)
                throw new InvalidOperationException("projectile copy not found");

            RewireReferences(p1, copy, p2.gameObject, p2, false);
            RelinkJoints(copy, p1, p2.gameObject);
            RelinkConstraints(copy, p1, p2.gameObject);
            var ropeComponent = ropeCopy?.GetComponent<HarpoonRope>();
            if (ropeComponent?.startLinkObject != null && ropeComponent.startLinkObject.transform.IsChildOf(p1.transform))
            {
                var counterpart = p2.transform.Find(RelativePath(p1.transform, ropeComponent.startLinkObject.transform));
                if (counterpart != null)
                    ropeComponent.startLinkObject = counterpart.gameObject;
            }

            projectileCopy.name = projectileGo.name + " (Player 2)";
            projectileCopy.transform.SetParent(projectileParent, true);
            if (ropeCopy != null)
            {
                ropeCopy.name = ropeGo.name + " (Player 2)";
                ropeCopy.transform.SetParent(ropeParent, true);
            }
            Object.Destroy(copy);

            h2.harpoonProjectile = projectileCopy.GetComponent<HarpoonProjectile>();
            HarpoonResetGuard.FillMissing(projectile, h2.harpoonProjectile);
            if (ropeComponent != null)
                h2.harpoonRope = ropeComponent;
            _p2Projectile = projectileCopy;
            _p2Rope = ropeCopy;
            foreach (var renderer in projectileCopy.GetComponentsInChildren<SpriteRenderer>(true))
                renderer.color *= Tint;
            Plugin.Logger.LogInfo($"Own harpoon: player 2 has its own bolt{(ropeCopy != null ? " and rope" : "")}");
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning($"Own harpoon failed: {exception}");
        }
    }

    // Unity constraints (what pins the bolt to the gun, aims the arm, ...) are
    // engine components: point any source on player 1 at the same part of
    // player 2.
    private static void RelinkConstraints(GameObject root, PlayerCharacter p1, GameObject clone)
    {
        try
        {
            RelinkConstraintsImpl(root, p1, clone);
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning($"Constraint relink on {root.name} failed: {exception.Message}");
        }
    }

    private static void RelinkConstraintsImpl(GameObject root, PlayerCharacter p1, GameObject clone)
    {
        var relinked = 0;
        relinked += Relink<UnityEngine.Animations.ParentConstraint>(root, p1, clone,
            c => c.sourceCount, (c, i) => c.GetSource(i), (c, i, s) => c.SetSource(i, s));
        relinked += Relink<UnityEngine.Animations.AimConstraint>(root, p1, clone,
            c => c.sourceCount, (c, i) => c.GetSource(i), (c, i, s) => c.SetSource(i, s));
        relinked += Relink<UnityEngine.Animations.PositionConstraint>(root, p1, clone,
            c => c.sourceCount, (c, i) => c.GetSource(i), (c, i, s) => c.SetSource(i, s));
        relinked += Relink<UnityEngine.Animations.RotationConstraint>(root, p1, clone,
            c => c.sourceCount, (c, i) => c.GetSource(i), (c, i, s) => c.SetSource(i, s));
        Plugin.Logger.LogInfo($"{root.name}: {relinked} constraint source(s) moved from player 1 to player 2");
    }

    private static int Relink<T>(GameObject root, PlayerCharacter p1, GameObject clone,
        Func<T, int> count, Func<T, int, UnityEngine.Animations.ConstraintSource> get,
        Action<T, int, UnityEngine.Animations.ConstraintSource> set) where T : Component
    {
        var relinked = 0;
        foreach (var component in root.GetComponentsInChildren(Il2CppType.Of<T>(), true))
        {
            var constraint = component?.TryCast<T>();
            if (constraint == null)
                continue;
            try
            {
                for (var i = 0; i < count(constraint); i++)
                {
                    var source = get(constraint, i);
                    var target = source.sourceTransform;
                    if (target == null || !target.IsChildOf(p1.transform))
                        continue;
                    var counterpart = clone.transform.Find(RelativePath(p1.transform, target));
                    if (counterpart == null)
                        continue;
                    source.sourceTransform = counterpart;
                    set(constraint, i, source);
                    relinked++;
                }
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning($"Constraint relink on {constraint.name} failed: {exception.Message}");
            }
        }
        return relinked;
    }

    // Player 2's "show gun" / "hide aim guide" signals do not arrive, so keep
    // the gun graphic and the aim guide in step with player 2's arm.
    // Player 2's "attack finished" signal does not arrive either, so after a
    // swing has had time to play out, clear the attack state.
    private static float _meleeStartedAt = -1f;
    private static int _meleeLogs;

    // If a pickup or interaction fails partway (e.g. an error), player 2 stays
    // "interacting" and every later action is blocked. Clear it after 3 s.
    private static float _interactingSince = -1f;

    private static void WatchInteraction()
    {
        if (_p2 == null)
            return;
        try
        {
            var busy = _p2.IsInteractionAnim || _p2.isInteracting?.Value == true;
            if (!busy)
            {
                _interactingSince = -1f;
                return;
            }
            if (_interactingSince < 0f)
                _interactingSince = Time.realtimeSinceStartup;
            if (Time.realtimeSinceStartup - _interactingSince < 3f)
                return;
            _interactingSince = -1f;
            _p2.IsInteractionAnim = false;
            _p2.ResetInteractionResource();
            _p2.ResetAttackResources();
            Plugin.Logger.LogWarning("Player 2 was stuck mid-interaction for 3 s; cleared");
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning($"P2 interaction watch failed: {exception.Message}");
        }
    }

    private static float _interactProbeAt = -1f;
    private static string _interactProbeWhat;
    private static int _interactLogs;

    private static string DescribeInteraction() =>
        $"near={(_p2.CurrentInteractionObject != null ? "yes" : "none")} interactingAnim={_p2.IsInteractionAnim} " +
        $"interacting={_p2.isInteracting?.Value} canMelee={_p2.CanMeleeAttack} playingMelee={_p2.IsPlayMeleeAttack} " +
        $"action={_p2.PlayerActionState} range={_p2.GetRangeActionState} actionLock={_p2.IsActionLock} " +
        $"moveLocked={_p2.IsMoveLocked} grab={_p2.grabHandler?.enabled}";

    private static void ProbeInteraction()
    {
        if (_p2 == null || _interactLogs > 40)
            return;
        try
        {
            string what = null;
            if (P2Keys.Pressed(P2Keys.Action.Interact))
                what = "interact";
            else if (P2Keys.Pressed(P2Keys.Action.Back))
                what = "knife";
            if (what != null)
            {
                _interactLogs++;
                Plugin.Logger.LogInfo($"P2 {what} pressed: {DescribeInteraction()}");
                _interactProbeAt = Time.realtimeSinceStartup + 0.5f;
                _interactProbeWhat = what;
            }
            else if (_interactProbeAt > 0f && Time.realtimeSinceStartup >= _interactProbeAt)
            {
                _interactProbeAt = -1f;
                _interactLogs++;
                Plugin.Logger.LogInfo($"P2 0.5 s after {_interactProbeWhat}: {DescribeInteraction()}");
            }
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning($"P2 interaction probe: {exception.Message}");
        }
    }

    private static void WatchMelee()
    {
        if (_p2 == null)
            return;
        if (P2Keys.Pressed(P2Keys.Action.Back))
            _meleeStartedAt = Time.realtimeSinceStartup;
        if (_meleeStartedAt < 0f || Time.realtimeSinceStartup - _meleeStartedAt < 0.7f)
            return;
        _meleeStartedAt = -1f;
        try
        {
            var stuck = _p2.IsPlayMeleeAttack || !_p2.CanMeleeAttack;
            if (_meleeLogs++ < 6)
                Plugin.Logger.LogInfo(
                    $"P2 melee after swing: playing={_p2.IsPlayMeleeAttack} canAttack={_p2.CanMeleeAttack} " +
                    $"combo={_p2.CurrentMeleeComboCount} -> {(stuck ? "resetting" : "fine")}");
            OwnCommands.ResetAfterSwing(_p1, _p2);
            if (!stuck)
                return;
            _p2.IsPlayMeleeAttack = false;
            _p2.ResetChainMeleeAttackState();
            _p2.ResetMeleeAttackResource();
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning($"P2 melee reset failed: {exception.Message}");
        }
    }

    private static int _gunGuideLogs;
    private static int _qteLogs;

    internal static void SyncWeaponVisuals()
    {
        if (_p2 == null)
            return;
        try
        {
            var arm = _p2.RangeAttackArm;
            if (arm == null)
                return;
            var armOut = arm.gameObject.activeInHierarchy;
            var guide = arm.attackRangeObject;
            var action = _p2.PlayerActionState;
            // One guide object holds both weapons' markers: its own sprite and
            // "Target_Arrow" are the harpoon's dart guide, "GunAimPoint" holds
            // the gun's barrel arrows. The game's show/hide for player 2 does
            // not arrive, so set each part from player 2's state.
            var gunAim = _p2.GetComponent<InstanceItemInventory>()?.gunHandler?.aim;
            var aimingGun = action == RangeActionType.Gun && _p2.GetRangeActionState.ToString() != "None";
            var harpoonGuide = armOut && action == RangeActionType.Harpoon;
            if (guide != null)
            {
                // Harpoon: let the game show it, only hide it when the harpoon is away.
                var wantGuide = aimingGun || (guide.activeSelf && harpoonGuide);
                if (guide.activeSelf != wantGuide)
                    guide.SetActive(wantGuide);
                var slide = guide.GetComponent<SpriteRenderer>();
                // The guide's own sprite is the curved aim line, used by both weapons.
                if (slide != null && slide.enabled != wantGuide)
                    slide.enabled = wantGuide;
                var dart = guide.transform.Find("Target_Arrow");
                if (dart != null && dart.gameObject.activeSelf != harpoonGuide)
                    dart.gameObject.SetActive(harpoonGuide);
            }
            if (gunAim != null)
            {
                var root = gunAim.aimRoot != null ? gunAim.aimRoot.gameObject : gunAim.gameObject;
                if (root.activeSelf != aimingGun)
                {
                    root.SetActive(aimingGun);
                    gunAim.SetVisibleAimPoint(aimingGun);
                    if (_gunGuideLogs++ < 10)
                        Plugin.Logger.LogInfo($"P2 gun arrows {(aimingGun ? "on" : "off")} (visible={root.activeInHierarchy}, range={_p2.GetRangeActionState})");
                }
                var aims = gunAim.m_AimTransforms;
                if (aimingGun && aims != null)
                    for (var i = 0; i < aims.Count; i++)
                        if (aims[i] != null && !aims[i].gameObject.activeSelf)
                            aims[i].gameObject.SetActive(true);
            }
            var harpoonGun = _p2.GetComponent<InstanceItemInventory>()?.harpoonHandler?.m_CurrentWeapon;
            var wantGun = armOut && _p2.PlayerActionState == RangeActionType.Harpoon;
            if (harpoonGun != null && harpoonGun.activeSelf != wantGun)
                harpoonGun.SetActive(wantGun);
        }
        catch (Exception exception)
        {
            if (_visualSyncFailures++ < 3)
                Plugin.Logger.LogWarning($"Player 2 weapon visuals: {exception.Message}");
        }
    }

    private static int _visualSyncFailures;

    // Physics joints are engine properties, not script fields: point any that
    // connect to player 1 at the same part of player 2.
    private static void RelinkJoints(GameObject root, PlayerCharacter p1, GameObject clone)
    {
        var relinked = 0;
        foreach (var joint in root.GetComponentsInChildren<Joint2D>(true))
        {
            var body = joint.connectedBody;
            if (body == null || !body.transform.IsChildOf(p1.transform))
                continue;
            var counterpart = clone.transform.Find(RelativePath(p1.transform, body.transform));
            var counterpartBody = counterpart?.GetComponent<Rigidbody2D>();
            if (counterpartBody == null)
                continue;
            joint.connectedBody = counterpartBody;
            relinked++;
        }
        Plugin.Logger.LogInfo($"Own harpoon: {relinked} rope joint(s) re-attached to player 2");
    }

    internal static string DescribeVisuals() =>
        $"P1[{DescribeVisual(_p1)}] P2[{DescribeVisual(_p2)}]";

    private static string DescribeVisual(PlayerCharacter character)
    {
        try
        {
            var inventory = character?.GetComponent<InstanceItemInventory>();
            var harpoonGun = inventory?.harpoonHandler?.m_CurrentWeapon;
            var gun = inventory?.gunHandler?.m_CurrentWeapon;
            var arm = character?.RangeAttackArm;
            var guide = arm?.attackRangeObject;
            return $"harpoonGun={Where(harpoonGun)} gun={Where(gun)} arm={Where(arm?.gameObject)} guide={Where(guide)} " +
                   $"armPlayerIsSelf={arm?.m_Player?.Pointer == character?.Pointer}";
        }
        catch (Exception exception)
        {
            return "read failed: " + exception.Message;
        }
    }

    private static string Where(GameObject go)
    {
        if (go == null)
            return "none";
        var owner = _p1 != null && go.transform.IsChildOf(_p1.transform) ? "P1"
            : _p2 != null && go.transform.IsChildOf(_p2.transform) ? "P2" : go.transform.root.name;
        return $"{go.name}@{owner}({(go.activeInHierarchy ? "shown" : "hidden")})";
    }

    internal static string DescribeHarpoons() =>
        $"P1[{DescribeHarpoon(_p1)}] P2[{DescribeHarpoon(_p2)}]";

    private static string DescribeHarpoon(PlayerCharacter character)
    {
        try
        {
            var handler = character?.GetComponent<InstanceItemInventory>()?.harpoonHandler;
            if (handler == null)
                return "no harpoon handler";
            var projectile = handler.harpoonProjectile;
            var owner = projectile?.Owner;
            return $"handler={Short(handler.Pointer)} projectile={Short(projectile?.Pointer)} " +
                   $"projectileHandler={Short(projectile?.m_HarpoonHandler?.Pointer)} " +
                   $"owner={owner?.root?.name}/{owner?.name} ownerIsP1={owner != null && _p1 != null && owner.IsChildOf(_p1.transform)} " +
                   $"ownerIsP2={owner != null && _p2 != null && owner.IsChildOf(_p2.transform)} " +
                   $"rope={Short(handler.harpoonRope?.Pointer)} projectileObject={Short(handler.m_ProjectileObject?.Pointer)}";
        }
        catch (Exception exception)
        {
            return "read failed: " + exception.Message;
        }
    }

    private static string Short(IntPtr? pointer) =>
        pointer == null || pointer == IntPtr.Zero ? "none" : (pointer.Value.ToInt64() & 0xFFFFFF).ToString("X6");

    // Test key F10: re-run player 1's own loadout equip.
    internal static void ReequipPlayerOne()
    {
        if (_p1 == null)
            return;
        var inventory = _p1.GetComponent<InstanceItemInventory>();
        Try("player 1 re-equip", () => inventory?.EquipSavedSlotData());
        Plugin.Logger.LogInfo("Harpoons after player 1 re-equip: " + DescribeHarpoons());
    }

    private static void Try(string what, System.Action action)
    {
        try
        {
            action();
            Plugin.Logger.LogInfo($"Clone {what}: ok");
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning($"Clone {what} failed: {exception.Message}");
        }
    }

    private static string RelativePath(Transform root, Transform target)
    {
        var parts = new System.Collections.Generic.List<string>();
        for (var node = target; node != null && node != root; node = node.parent)
            parts.Insert(0, node.name);
        return string.Join("/", parts);
    }

    // While player 2's diver runs its own code, the game's "the player" is
    // player 2, so things like "where does the harpoon spawn" find the clone.
    private static InGameManager _manager;
    private static int _swapDepth;

    internal static bool InPlayer2Turn => _swapDepth > 0 || P2Dispatch.Active;

    internal static bool DispatchEnabled = true;
    internal static bool CameraSecondTarget = false;

    // Test key F7: take player 2 off / put player 2 back on the camera's
    // second-target slot (the slot the game also uses for harpoon fights).
    internal static void ToggleCameraTarget()
    {
        CameraSecondTarget = !CameraSecondTarget;
        var camera = Object.FindFirstObjectByType<global::CameraManager>();
        if (camera == null || _p2 == null)
            return;
        if (CameraSecondTarget)
            camera.AddSecondTarget(_p2.transform);
        else if (camera.IsSecondTarget(_p2.transform))
            camera.RemoveSecondTarget();
        Plugin.Logger.LogInfo($"TEST F7: camera second target {(CameraSecondTarget ? "player 2" : "none")}");
    }

    private static float _nextAimLog;

    private static void LogPlayerOneAim()
    {
        var mouse = UnityEngine.InputSystem.Mouse.current;
        if (_p1 == null || mouse == null || !mouse.rightButton.isPressed || Time.realtimeSinceStartup < _nextAimLog)
            return;
        _nextAimLog = Time.realtimeSinceStartup + 0.5f;
        var camera = Object.FindFirstObjectByType<global::CameraManager>();
        Plugin.Logger.LogInfo(
            $"P1 aiming: aim={_p1.aimInput} aimRight={_p1.aimInputRight} action={_p1.PlayerActionState} " +
            $"range={_p1.GetRangeActionState} armPlayerIsP1={_p1.RangeAttackArm?.m_Player?.Pointer == _p1.Pointer} " +
            $"cameraSecond={camera?.SecondTargetTransform?.name} cameraPrimary={camera?.PrimaryTargetTransform?.name} " +
            $"mouse={mouse.position.ReadValue()}");
    }
    internal static bool TurnSwapEnabled = true;

    internal static void BeginPlayer2Turn(PlayerCharacter character)
    {
        if (!TurnSwapEnabled || !IsPlayer2(character) || _p1 == null)
            return;
        _manager ??= Object.FindFirstObjectByType<InGameManager>();
        if (_manager == null)
            return;
        if (_swapDepth++ == 0)
        {
            _manager.playerCharacter = character;
            SwapSchemeToGamePad();
        }
    }

    // Player 2 plays like a controller (stick aiming). The control mode is
    // global and follows player 1's keyboard/mouse, so switch the stored mode
    // (not the property, which would refresh every button prompt) during
    // player 2's turn only.

    private static void SwapSchemeToGamePad() => SchemeGuard.BeginPlayer2(_p1);

    private static void RestoreScheme() => SchemeGuard.EndPlayer2(_p1);

    // Called before player 1's own update: if player 2's turn did not finish
    // cleanly, the game must not see player 2 as "the player" now.
    internal static void EnsurePlayerOneTurn(PlayerCharacter character)
    {
        if (IsPlayer2(character) || _p1 == null || character.Pointer != _p1.Pointer || _swapDepth == 0)
            return;
        _swapDepth = 0;
        if (_manager != null)
            _manager.playerCharacter = _p1;
        RestoreScheme();
        if (_turnLeakWarnings++ < 5)
            Plugin.Logger.LogWarning("Player 2's turn did not end cleanly; player 1 restored before its update");
    }

    private static int _turnLeakWarnings;
    private static Animator _p2Animator;
    private static bool _manualAnimator = false;
    private static int _animatorFailures;

    private static void AdvanceAnimator()
    {
        if (!_manualAnimator || _p2Animator == null || _p2Animator.enabled)
            return;
        try
        {
            _p2Animator.Update(Time.deltaTime);
        }
        catch (Exception exception)
        {
            if (_animatorFailures++ < 5)
                Plugin.Logger.LogWarning($"Player 2 animation step failed: {exception.Message}");
        }
    }

    // Test key F8: switch between the mod advancing player 2's animator and
    // Unity doing it, to confirm the cause of the aiming problems.
    internal static void ToggleManualAnimator()
    {
        _manualAnimator = !_manualAnimator;
        if (_p2Animator != null)
            _p2Animator.enabled = !_manualAnimator;
        Plugin.Logger.LogInfo($"Player 2 animator advanced by {(_manualAnimator ? "the mod (inside player 2's turn)" : "Unity")}");
    }

    internal static void EndPlayer2Turn(PlayerCharacter character)
    {
        if (!IsPlayer2(character) || _swapDepth == 0)
            return;
        if (--_swapDepth == 0 && _manager != null && _p1 != null)
        {
            _manager.playerCharacter = _p1;
            RestoreScheme();
        }
    }

    private static object CastToManaged(Component component)
    {
        var classPtr = component.ObjectClass;
        var il2cppType = Il2CppType.TypeFromPointer(classPtr);
        var fullName = il2cppType.FullName;
        foreach (var assembly in System.AppDomain.CurrentDomain.GetAssemblies())
        {
            var type = assembly.GetType(fullName) ?? assembly.GetType("Il2Cpp" + fullName);
            if (type == null)
                continue;
            var constructor = type.GetConstructor(new[] { typeof(IntPtr) });
            return constructor?.Invoke(new object[] { component.Pointer });
        }
        return null;
    }

    private static void LogPlayerOneInput()
    {
        if (Time.realtimeSinceStartup < _nextP1InputLog)
            return;
        var held = P2Keys.IsDown(0x57) || P2Keys.IsDown(0x41) || P2Keys.IsDown(0x53) || P2Keys.IsDown(0x44);
        if (!held)
            return;
        _nextP1InputLog = Time.realtimeSinceStartup + 0.5f;
        var keyboard = UnityEngine.InputSystem.Keyboard.current;
        Plugin.Logger.LogInfo(
            $"P1 holding WASD: p1move={_p1.moveInput} keyboard={keyboard?.name} " +
            $"w={keyboard?.wKey.isPressed} d={keyboard?.dKey.isPressed} filter={KeyboardFilter.Active} " +
            $"input={DescribeInputTarget()} p1pos={_p1.transform.position} " +
            $"focused={Application.isFocused} foreground={KeyboardFilter.GameIsForeground} " +
            $"vkEnabled={keyboard?.enabled}");
    }

    private static string Describe(GameObject root)
    {
        var text = new StringBuilder();
        Describe(root.transform, 0, text);
        return text.ToString();
    }

    private static void Describe(Transform node, int depth, StringBuilder text)
    {
        if (depth > 6 || text.Length > 20000)
            return;
        text.Append(new string(' ', depth * 2)).Append(node.name).Append(node.gameObject.activeSelf ? "" : " (off)").Append(": ");
        foreach (var component in node.GetComponents<Component>())
            text.Append(component?.GetIl2CppType()?.Name ?? "?").Append(", ");
        text.Append('\n');
        for (var i = 0; i < node.childCount; i++)
            Describe(node.GetChild(i), depth + 1, text);
    }
}

[HarmonyPatch(typeof(PlayerCharacter), nameof(PlayerCharacter.Update))]
internal static class PlayerUpdateInputPatch
{
    private static void Prefix(PlayerCharacter __instance)
    {
        SecondDiver.TracePhase(__instance, "update-start");
        SecondDiver.EnsurePlayerOneTurn(__instance);
        SecondDiver.ApplyInput(__instance);
        PadAimFix.Apply(__instance);
        SecondDiver.BeginPlayer2Turn(__instance);
    }

    private static void Postfix(PlayerCharacter __instance)
    {
        SecondDiver.EndPlayer2Turn(__instance);
        SecondDiver.ApplyInput(__instance);
        SecondDiver.TracePhase(__instance, "update-end");
    }
}

[HarmonyPatch(typeof(PlayerCharacter), nameof(PlayerCharacter.FixedUpdate))]
internal static class PlayerFixedUpdateInputPatch
{
    private static void Prefix(PlayerCharacter __instance)
    {
        SecondDiver.TracePhase(__instance, "fixed-start");
        SecondDiver.EnsurePlayerOneTurn(__instance);
        SecondDiver.ApplyInput(__instance);
        SecondDiver.BeginPlayer2Turn(__instance);
    }

    private static void Postfix(PlayerCharacter __instance)
    {
        SecondDiver.EndPlayer2Turn(__instance);
        SecondDiver.TracePhase(__instance, "fixed-end");
    }
}
