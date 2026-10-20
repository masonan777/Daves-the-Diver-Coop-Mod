using System;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DaveTheDiverCoop;

// Shared camera: follows a point midway between the two divers, and keeps
// player 2 inside the view (player 1 is never pushed, the game relies on him).
// If the game points the camera at something else (boss intro, cutscene),
// it is left alone until the camera is back on player 1.
internal static class CoopCamera
{
    private const float EdgeMargin = 0.06f;

    private static GameObject _anchor;
    private static global::CameraManager _camera;
    private static bool _following;
    private static Vector3 _offset;
    private static float _baseZ;
    private static bool _haveZ;
    private static Vector3 _shake;
    private static bool _haveOffset;
    private static Vector3 _velocity;

    internal static void Tick(PlayerCharacter p1, PlayerCharacter p2)
    {
        if (p1 == null || p2 == null)
        {
            Stop(p1);
            return;
        }
        try
        {
            if (_anchor == null)
                _anchor = new GameObject("Coop Camera Anchor");
            var a = p1.transform.position;
            var b = p2.transform.position;
            // Keep the depth fixed: some moves (like a shark bite) shift a
            // diver's depth, which made the camera zoom in and out.
            if (!_haveZ)
            {
                _baseZ = a.z;
                _haveZ = true;
            }
            _anchor.transform.position = new Vector3((a.x + b.x) * 0.5f, (a.y + b.y) * 0.5f, _baseZ);

            if (_camera == null)
                _camera = Object.FindFirstObjectByType<global::CameraManager>();
            if (_camera == null)
                return;
            var primary = _camera.PrimaryTargetTransform;
            if (!_haveOffset)
            {
                _offset = CameraTransform().position - new Vector3(p1.transform.position.x, p1.transform.position.y, _baseZ);
                _haveOffset = true;
                Plugin.Logger.LogInfo($"Co-op camera offset from player 1: {_offset}");
            }
            if (primary != null && primary.Pointer == p1.transform.Pointer)
            {
                _camera.ChangeTarget(_anchor.transform);
                if (!_following)
                    Plugin.Logger.LogInfo("Camera now follows the midpoint between the divers");
                _following = true;
            }
            else if (primary == null || primary.Pointer != _anchor.transform.Pointer)
            {
                _following = false;
            }
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning($"Co-op camera: {exception.Message}");
        }
    }

    private static Transform CameraTransform() => _camera.transform;

    internal static Transform AnchorTransform => _anchor != null ? _anchor.transform : null;
    internal static bool Following => _following;

    // After the game's own camera step: put the camera back where the mod has it.
    internal static void HoldPosition()
    {
        if (!_following || !_haveLastSet || _camera == null)
            return;
        try
        {
            var camera = CameraTransform();
            var want = _lastSet + _shake;
            if ((camera.position - want).sqrMagnitude > 0.0001f)
                camera.position = want;
            HoldZoom();
        }
        catch
        {
        }
    }
    // No zooming in on a diver (grabs, catches): keep the lens the camera
    // had when the co-op view started.
    private static Camera _view;
    private static float _baseLens = -1f;
    private static int _zoomLogs;

    private static void HoldZoom()
    {
        var view = Camera.main;
        if (view == null)
            return;
        if (_view == null || view.Pointer != _view.Pointer)
        {
            _view = view;
            _baseLens = view.orthographic ? view.orthographicSize : view.fieldOfView;
            Plugin.Logger.LogInfo($"Co-op camera: zoom held at {(view.orthographic ? "size" : "field of view")} {_baseLens:F2}");
            return;
        }
        var lens = view.orthographic ? view.orthographicSize : view.fieldOfView;
        if (Mathf.Abs(lens - _baseLens) < 0.001f)
            return;
        if (_zoomLogs++ < 5)
            Plugin.Logger.LogInfo($"Co-op camera: game zoomed to {lens:F2}, kept at {_baseLens:F2}");
        if (view.orthographic)
            view.orthographicSize = _baseLens;
        else
            view.fieldOfView = _baseLens;
    }

    private static Vector3 _lastSet;
    private static bool _haveLastSet;
    private static int _jumpLogs;

    // The game's camera stops following once player 2 exists, so move it
    // ourselves toward the midpoint (same offset it had from player 1).
    internal static void Drive()
    {
        if (!_following || !_haveOffset || _camera == null || _anchor == null)
            return;
        try
        {
            var camera = CameraTransform();
            if (_haveLastSet && (camera.position - _lastSet - _shake).sqrMagnitude > 1f && _jumpLogs++ < 15)
                Plugin.Logger.LogInfo($"Camera jumped by {(camera.position - _lastSet).magnitude:F1} since last frame (frame {Time.frameCount}, player 2 turn={SecondDiver.InPlayer2Turn})");
            var target = _anchor.transform.position + _offset;
            // Start from where the mod last put the camera, not where the game
            // moved it in between (its own idea of the target is stale).
            var from = _haveLastSet ? _lastSet : camera.position;
            _lastSet = Vector3.SmoothDamp(from, target, ref _velocity, 0.25f);
            // The game's own shake is undone by holding the camera, so shake
            // here (a small random offset that is not carried into next frame).
            _shake = Vector3.zero;
            try
            {
                if (_camera.isShaking())
                    _shake = (Vector3)(UnityEngine.Random.insideUnitCircle * 0.12f);
            }
            catch
            {
            }
            camera.position = _lastSet + _shake;
            _haveLastSet = true;
            HoldZoom();
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning($"Co-op camera drive: {exception.Message}");
            _following = false;
        }
    }

    // Called after the frame's movement: keep player 2 on screen.
    internal static void Leash(PlayerCharacter p2)
    {
        if (p2 == null || !_following || DiveIn.Active)
            return;
        var view = Camera.main;
        if (view == null)
            return;
        var position = p2.transform.position;
        var viewport = view.WorldToViewportPoint(position);
        if (viewport.z <= 0f)
            return;
        var x = Mathf.Clamp(viewport.x, EdgeMargin, 1f - EdgeMargin);
        var y = Mathf.Clamp(viewport.y, EdgeMargin, 1f - EdgeMargin);
        if (Mathf.Approximately(x, viewport.x) && Mathf.Approximately(y, viewport.y))
            return;
        var world = view.ViewportToWorldPoint(new Vector3(x, y, viewport.z));
        var target = new Vector3(world.x, world.y, position.z);
        p2.transform.position = target;
        var body = p2.GetComponent<Rigidbody2D>();
        if (body != null)
        {
            body.position = target;
            var velocity = body.velocity;
            if ((x > viewport.x && velocity.x < 0f) || (x < viewport.x && velocity.x > 0f))
                velocity.x = 0f;
            if ((y > viewport.y && velocity.y < 0f) || (y < viewport.y && velocity.y > 0f))
                velocity.y = 0f;
            body.velocity = velocity;
        }
    }

    internal static string Describe()
    {
        try
        {
            var view = Camera.main;
            return $"following={_following} primary={_camera?.PrimaryTargetTransform?.name} " +
                   $"second={_camera?.SecondTargetTransform?.name} anchor={_anchor?.transform.position} " +
                   $"view={view?.name}@{view?.transform.position} managerAt={_camera?.transform.position}";
        }
        catch (System.Exception exception)
        {
            return "camera read failed: " + exception.Message;
        }
    }

    internal static void Stop(PlayerCharacter p1)
    {
        _view = null;
        try
        {
            if (_following && _camera != null && _anchor != null && p1 != null &&
                _camera.PrimaryTargetTransform?.Pointer == _anchor.transform.Pointer)
            {
                _following = false; // so the camera guard lets this through
                _camera.ChangeTarget(p1.transform);
                Plugin.Logger.LogInfo("Camera back on player 1");
            }
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning($"Co-op camera stop: {exception.Message}");
        }
        _following = false;
        _camera = null;
        _haveOffset = false;
        _haveLastSet = false;
        _haveZ = false;
        _shake = Vector3.zero;
        _velocity = Vector3.zero;
    }
}
