using System;

namespace DaveTheDiverCoop;

// The knife is run by a shared "melee command" asset that remembers whether a
// swing is in progress. Its "swing finished" reset never reaches it for
// player 2, so after one swing it refuses to start another. Once player 2's
// swing has had time to finish, clear that memory (only if player 1 is not
// mid-swing, since he uses the same asset).
internal static class OwnCommands
{
    private static int _logs;

    internal static string Describe(PlayerCharacter p)
    {
        try
        {
            var command = p?.m_MeleeActionCommand?.TryCast<MeleeActionCommand_SO>();
            if (command == null)
                return "command=none";
            return $"active={command.m_IsActionActive} charging={command.m_IsAlreadyCharging} timer={command.m_commandTimer:F2}";
        }
        catch (Exception exception)
        {
            return "command read failed: " + exception.Message;
        }
    }

    // After a swing, Dave's animation "sub-state" should return from Attack to
    // LocoMotion. For player 2 the "left Attack" signal is lost, so he stays in
    // Attack and every later swing is refused. Put him back once the swing is over.
    private static void UnstickAttackState(PlayerCharacter p1, PlayerCharacter p2)
    {
        var manager = p2?.SubStatesManager;
        if (manager == null)
            return;
        var state = manager.State;
        var counts = manager.enteredCount;
        var attackCount = counts != null && counts.ContainsKey(PlayerSubSMBState.Attack) ? counts[PlayerSubSMBState.Attack] : -1;
        var locoCount = counts != null && counts.ContainsKey(PlayerSubSMBState.LocoMotion) ? counts[PlayerSubSMBState.LocoMotion] : -1;
        var own = p1?.SubStatesManager == null || p1.SubStatesManager.Pointer != manager.Pointer;
        if (_logs < 8)
            Plugin.Logger.LogInfo($"P2 sub-state after swing: {state} attackCount={attackCount} locoCount={locoCount} ownManager={own} p1State={p1?.SubStatesManager?.State}");
        if (state != PlayerSubSMBState.Attack || p2.IsPlayMeleeAttack)
            return;
        if (counts != null && counts.ContainsKey(PlayerSubSMBState.Attack))
            counts[PlayerSubSMBState.Attack] = 0;
        manager.State = PlayerSubSMBState.LocoMotion;
        if (_logs < 8)
            Plugin.Logger.LogInfo($"P2 sub-state forced back to {manager.State} (Dave reports {p2.CurrentSubSMBState})");
    }

    internal static void ResetAfterSwing(PlayerCharacter p1, PlayerCharacter p2)
    {
        try
        {
            if (p1 != null && p1.IsPlayMeleeAttack)
                return;
            var command = p2?.m_MeleeActionCommand?.TryCast<MeleeActionCommand_SO>();
            if (command == null)
                return;
            UnstickAttackState(p1, p2);
            var before = Describe(p2);
            command.m_IsActionActive = false;
            command.m_IsAlreadyCharging = false;
            command.m_commandTimer = 0f;
            if (_logs++ < 8)
                Plugin.Logger.LogInfo($"P2 knife command reset ({before})");
        }
        catch (Exception exception)
        {
            if (_logs++ < 8)
                Plugin.Logger.LogWarning($"P2 knife command reset failed: {exception.Message}");
        }
    }
}
