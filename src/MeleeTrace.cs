using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace DaveTheDiverCoop;

// Diagnostics for player 2's knife: which melee steps run, on which diver,
// and whether the swing ever starts. Logs for 1.5 s after each P2 knife press.
internal static class MeleeTrace
{
    private static float _until = -1f;
    private static float _pressedAt;
    private static int _presses;
    private static bool _sawPlaying;
    private static readonly HashSet<string> _seen = new();

    internal static bool Watching => Time.realtimeSinceStartup < _until;
    private static float _snapshotAt = -1f;

    internal static string Layers(PlayerCharacter p)
    {
        try
        {
            var a = p?.characterAnimator;
            if (a == null)
                return "no animator";
            var sb = new System.Text.StringBuilder();
            for (var i = 0; i < a.layerCount; i++)
            {
                var info = a.GetCurrentAnimatorStateInfo(i);
                sb.Append($"[{i}:{a.GetLayerName(i)} w={a.GetLayerWeight(i):F1} state={info.shortNameHash} t={info.normalizedTime:F2}{(a.IsInTransition(i) ? " trans" : "")}]");
            }
            return sb.ToString();
        }
        catch (System.Exception e) { return "layers failed: " + e.Message; }
    }

    internal static string Who(BaseCharacter character)
    {
        if (character == null) return "null";
        var p = character.TryCast<PlayerCharacter>();
        if (p == null) return "other";
        return SecondDiver.IsPlayer2(p) ? "P2" : "P1";
    }

    internal static string Who(PlayerCharacter p) => p == null ? "null" : SecondDiver.IsPlayer2(p) ? "P2" : "P1";

    internal static void LogRaw(string text)
    {
        if (!Watching) return;
        if (!_seen.Add(text)) return;
        Plugin.Logger.LogInfo($"MeleeTrace +{Time.realtimeSinceStartup - _pressedAt:F2}s {text}");
    }

    internal static void Tick()
    {
        var p2 = SecondDiver.Player2;
        if (p2 == null)
            return;
        if (P2Keys.Pressed(P2Keys.Action.Back) && _presses < 12)
        {
            _presses++;
            _pressedAt = Time.realtimeSinceStartup;
            _until = _pressedAt + 1.5f;
            _sawPlaying = false;
            _seen.Clear();
            string command;
            try
            {
                var c2 = p2.m_MeleeActionCommand;
                var c1 = SecondDiver.Player1?.m_MeleeActionCommand;
                command = $"p2Command={(c2 != null ? c2.name : "null")} same={(c1 != null && c2 != null && c1.Pointer == c2.Pointer)}";
            }
            catch (System.Exception e) { command = "command read failed: " + e.Message; }
            _snapshotAt = _pressedAt + 0.3f;
            if (_presses <= 3) StateDiff.OnPress(p2, _presses);
            Plugin.Logger.LogInfo($"MeleeTrace press #{_presses} layers P2 {Layers(p2)}");
            Plugin.Logger.LogInfo($"MeleeTrace press #{_presses}: {command} spec={(p2.meleeSpec != null)} chargeable={p2.IsChargeableMeleeWeapon} {OwnCommands.Describe(p2)}");
            return;
        }
        if (!Watching)
            return;
        if (_snapshotAt > 0f && Time.realtimeSinceStartup >= _snapshotAt)
        {
            _snapshotAt = -1f;
            Plugin.Logger.LogInfo($"MeleeTrace +0.3s layers P2 {Layers(p2)}");
        }
        if (!_sawPlaying && p2.IsPlayMeleeAttack)
        {
            _sawPlaying = true;
            Log(p2, "IsPlayMeleeAttack became true", "");
        }
        var p1 = SecondDiver.Player1;
        if (p1 != null && p1.IsPlayMeleeAttack)
            Log(p1, "P1 IsPlayMeleeAttack is true during P2 swing", "");
    }

    internal static void Log(PlayerCharacter who, string step, string detail)
    {
        if (!Watching || who == null)
            return;
        var name = SecondDiver.IsPlayer2(who) ? "P2" : "P1";
        if (!_seen.Add(name + step))
            return;
        var t = Time.realtimeSinceStartup - _pressedAt;
        Plugin.Logger.LogInfo($"MeleeTrace +{t:F2}s {name} {step} {detail} playing={who.IsPlayMeleeAttack} combo={who.CurrentMeleeComboCount}");
    }
}

[HarmonyPatch(typeof(PlayerCharacter), nameof(PlayerCharacter.OnMelee_Performed))]
internal static class MeleeTracePerformed { private static void Prefix(PlayerCharacter __instance) => MeleeTrace.Log(__instance, "OnMelee_Performed", ""); }

[HarmonyPatch(typeof(PlayerCharacter), nameof(PlayerCharacter.OnMelee_Canceled))]
internal static class MeleeTraceCanceled { private static void Prefix(PlayerCharacter __instance) => MeleeTrace.Log(__instance, "OnMelee_Canceled", ""); }

[HarmonyPatch(typeof(PlayerCharacter), nameof(PlayerCharacter.OnMelee_Holding))]
internal static class MeleeTraceHolding { private static void Prefix(PlayerCharacter __instance) => MeleeTrace.Log(__instance, "OnMelee_Holding", ""); }

[HarmonyPatch(typeof(PlayerCharacter), nameof(PlayerCharacter.OnStartMeleeAttack))]
internal static class MeleeTraceStart { private static void Prefix(PlayerCharacter __instance) => MeleeTrace.Log(__instance, "OnStartMeleeAttack", ""); }

[HarmonyPatch(typeof(PlayerCharacter), nameof(PlayerCharacter.StartMeleeChainAttack))]
internal static class MeleeTraceChain { private static void Prefix(PlayerCharacter __instance) => MeleeTrace.Log(__instance, "StartMeleeChainAttack", ""); }

[HarmonyPatch(typeof(PlayerCharacter), nameof(PlayerCharacter.OnReleaseMeleeAttack))]
internal static class MeleeTraceRelease { private static void Prefix(PlayerCharacter __instance, int __0) => MeleeTrace.Log(__instance, "OnReleaseMeleeAttack", $"arg={__0}"); }

[HarmonyPatch(typeof(PlayerCharacter), nameof(PlayerCharacter.ResetChainMeleeAttackState))]
internal static class MeleeTraceResetChain { private static void Prefix(PlayerCharacter __instance) => MeleeTrace.Log(__instance, "ResetChainMeleeAttackState", ""); }

[HarmonyPatch(typeof(PlayerCharacter), nameof(PlayerCharacter.CancelAttack))]
internal static class MeleeTraceCancelAttack { private static void Prefix(PlayerCharacter __instance) => MeleeTrace.Log(__instance, "CancelAttack", ""); }

[HarmonyPatch(typeof(PlayerCharacter), nameof(PlayerCharacter.IsPlayMeleeAttack), MethodType.Setter)]
internal static class MeleeTraceSetPlaying
{
    private static void Prefix(PlayerCharacter __instance, bool __0)
    {
        if (!MeleeTrace.Watching) return;
        var stack = System.Environment.StackTrace.Split('\n');
        var callers = new System.Collections.Generic.List<string>();
        foreach (var line in stack)
            if (line.Contains("DMD<") || line.Contains("DaveTheDiverCoop")) callers.Add(line.Trim());
        MeleeTrace.LogRaw($"{MeleeTrace.Who(__instance)} IsPlayMeleeAttack := {__0} via {string.Join(" | ", callers.GetRange(0, System.Math.Min(4, callers.Count)))}");
    }
}

[HarmonyPatch(typeof(MeleeActionCommand_SO), nameof(MeleeActionCommand_SO.DownExecute))]
internal static class MeleeTraceDown { private static void Prefix(MeleeActionCommand_SO __instance, BaseCharacter __0) => MeleeTrace.LogRaw($"command DownExecute by {MeleeTrace.Who(__0)} active={__instance.m_IsActionActive}"); }

[HarmonyPatch(typeof(MeleeActionCommand_SO), nameof(MeleeActionCommand_SO.UpExecute))]
internal static class MeleeTraceUp { private static void Prefix(MeleeActionCommand_SO __instance, BaseCharacter __0) => MeleeTrace.LogRaw($"command UpExecute by {MeleeTrace.Who(__0)} active={__instance.m_IsActionActive}"); }

[HarmonyPatch(typeof(MeleeActionCommand_SO), nameof(MeleeActionCommand_SO.HoldExecute))]
internal static class MeleeTraceHoldCmd { private static void Prefix(BaseCharacter __0) => MeleeTrace.LogRaw($"command HoldExecute by {MeleeTrace.Who(__0)}"); }

[HarmonyPatch(typeof(MeleeActionCommand_SO), nameof(MeleeActionCommand_SO.CancelExecute))]
internal static class MeleeTraceCancelCmd { private static void Prefix(BaseCharacter __0) => MeleeTrace.LogRaw($"command CancelExecute by {MeleeTrace.Who(__0)}"); }

[HarmonyPatch(typeof(PlayerCharacter), nameof(PlayerCharacter.OnMelee))]
internal static class MeleeTraceOnMelee { private static void Prefix(PlayerCharacter __instance) => MeleeTrace.Log(__instance, "OnMelee", ""); }

[HarmonyPatch(typeof(MeleeWeaponHandler), nameof(MeleeWeaponHandler.StartChainAttack))]
internal static class MeleeTraceWStart { private static void Prefix(MeleeWeaponHandler __instance) => MeleeTrace.LogRaw($"weapon({MeleeTrace.Who(__instance.cachedPlayer)}) StartChainAttack"); }

[HarmonyPatch(typeof(MeleeWeaponHandler), nameof(MeleeWeaponHandler.CancelAction))]
internal static class MeleeTraceWCancel { private static void Prefix(MeleeWeaponHandler __instance) => MeleeTrace.LogRaw($"weapon({MeleeTrace.Who(__instance.cachedPlayer)}) CancelAction"); }

[HarmonyPatch(typeof(MeleeWeaponHandler), nameof(MeleeWeaponHandler.CancelImmediatelyAction))]
internal static class MeleeTraceWCancelNow { private static void Prefix(MeleeWeaponHandler __instance) => MeleeTrace.LogRaw($"weapon({MeleeTrace.Who(__instance.cachedPlayer)}) CancelImmediatelyAction"); }

[HarmonyPatch(typeof(MeleeWeaponHandler), nameof(MeleeWeaponHandler.OnEnableDamage))]
internal static class MeleeTraceWDamageOn { private static void Prefix(MeleeWeaponHandler __instance) { if (MeleeTrace.Watching && SecondDiver.Player2 != null) MeleeTrace.LogRaw($"weapon({MeleeTrace.Who(__instance.cachedPlayer)}) OnEnableDamage animCtrlOnP2={__instance.m_MeleeAnimCtrl != null && __instance.m_MeleeAnimCtrl.transform.IsChildOf(SecondDiver.Player2.transform)}"); } }

[HarmonyPatch(typeof(MeleeWeaponHandler), nameof(MeleeWeaponHandler.OnDisableDamage))]
internal static class MeleeTraceWDamageOff { private static void Prefix(MeleeWeaponHandler __instance) => MeleeTrace.LogRaw($"weapon({MeleeTrace.Who(__instance.cachedPlayer)}) OnDisableDamage"); }

[HarmonyPatch(typeof(MeleeWeaponHandler), nameof(MeleeWeaponHandler.IsAvailableChainAttack))]
internal static class MeleeTraceWAvail { private static void Postfix(MeleeWeaponHandler __instance, bool __result) => MeleeTrace.LogRaw($"weapon({MeleeTrace.Who(__instance.cachedPlayer)}) IsAvailableChainAttack={__result}"); }
