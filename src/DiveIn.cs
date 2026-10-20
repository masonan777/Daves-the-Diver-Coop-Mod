using System;
using UnityEngine;

namespace DaveTheDiverCoop;

// When player 2 joins, play Dave's "diving in" animation: player 2 starts a
// little above and behind player 1 and drops into place over the length of the
// dive clip, without control, then swims normally.
internal static class DiveIn
{
    // Animator states recorded from player 1's own dive entry.
    private const int DivingState = -1813975141;
    private const int IdleState = -1723333718;
    private const float Duration = 1.8f;
    private static readonly Vector3 StartOffset = new(-2.5f, 3.5f, 0f);

    private static PlayerCharacter _diver;
    private static Rigidbody2D _body;
    private static Vector3 _from, _to;
    private static float _startedAt;
    private static RigidbodyType2D _savedType;

    internal static bool Active { get; private set; }

    internal static void Start(PlayerCharacter p2)
    {
        if (p2 == null)
            return;
        try
        {
            _diver = p2;
            _body = p2.GetComponent<Rigidbody2D>();
            _to = p2.transform.position;
            _from = _to + StartOffset;
            if (_body != null)
            {
                _savedType = _body.bodyType;
                _body.bodyType = RigidbodyType2D.Kinematic;
                _body.velocity = Vector2.zero;
            }
            Move(_from);
            p2.characterAnimator?.Play(DivingState, 0, 0f);
            _startedAt = Time.time;
            Active = true;
            Plugin.Logger.LogInfo($"Player 2 diving in: {_from} -> {_to}");
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning($"Player 2 dive-in failed: {exception.Message}");
            Finish();
        }
    }

    internal static void Tick()
    {
        if (!Active)
            return;
        if (_diver == null)
        {
            Active = false;
            return;
        }
        var t = (Time.time - _startedAt) / Duration;
        if (t >= 1f)
        {
            Finish();
            return;
        }
        var eased = 1f - (1f - t) * (1f - t); // ease out: fast at first, settling in
        Move(Vector3.Lerp(_from, _to, eased));
    }

    private static void Move(Vector3 position)
    {
        _diver.transform.position = position;
        if (_body != null)
        {
            _body.position = position;
            _body.velocity = Vector2.zero;
        }
    }

    internal static void Finish()
    {
        if (!Active && _diver == null)
            return;
        Active = false;
        try
        {
            if (_diver != null)
            {
                Move(_to);
                if (_body != null)
                    _body.bodyType = _savedType;
                _diver.characterAnimator?.Play(IdleState, 0, 0f);
                Plugin.Logger.LogInfo("Player 2 dive-in finished");
            }
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning($"Player 2 dive-in finish failed: {exception.Message}");
        }
        _diver = null;
        _body = null;
    }
}
