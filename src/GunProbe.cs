using System;
using Il2CppInterop.Runtime;
using Object = UnityEngine.Object;
using UnityEngine;

namespace DaveTheDiverCoop;

// Diagnostics: when either diver fires the gun, log its burst settings and how
// many shots actually came out (ammo before / 0.6 s later).
internal static class GunProbe
{
    private static float _p1At = -1f, _p2At = -1f, _bulletsAt = -1f;
    private static string _bulletsWho;

    private static string Bullets()
    {
        var all = Object.FindObjectsOfType(Il2CppType.Of<GunBullet>(), false);
        var text = $"{all.Length} active:";
        for (var i = 0; i < all.Length && i < 8; i++)
        {
            var b = all[i]?.TryCast<GunBullet>();
            if (b != null)
                text += $" [{b.name} at {b.transform.position} parent={b.transform.parent?.name}]";
        }
        return text;
    }
    private static int _p1Ammo, _p2Ammo, _logs;
    private static bool _mouseWasDown;

    private static GunWeaponHandler Gun(PlayerCharacter p) => p?.GetComponent<InstanceItemInventory>()?.gunHandler;

    private static string Describe(PlayerCharacter p)
    {
        try
        {
            var gun = Gun(p);
            var spec = gun?.GunSpec;
            var aims = "";
            var list = gun?.aim?.m_AimTransforms;
            if (list != null)
                for (var i = 0; i < list.Count; i++)
                {
                    var t = list[i];
                    aims += t == null ? "[null]" : $"[{t.name} z={t.eulerAngles.z:F0} local={t.localEulerAngles.z:F0} active={t.gameObject.activeInHierarchy}]";
                }
            var init = gun?.aim?.m_InitAimRotations;
            var inits = "";
            if (init != null)
                for (var i = 0; i < init.Count; i++)
                    inits += $"{init[i].eulerAngles.z:F0} ";
            return $"aims={aims} init=[{inits}] ammo={gun?.m_CurrentAmmo} firing={gun?.IsFiring} burst={spec?.BurstCount}x every {spec?.BurstTerm:F2}s muzzles={spec?.MuzzleCount} players={gun?.IsPlayersHandler} " +
                   $"range={p.GetRangeActionState} action={p.PlayerActionState} clip={ShortDashProbe.Clip(p)}";
        }
        catch (Exception exception)
        {
            return "?" + exception.Message;
        }
    }

    internal static void Tick(PlayerCharacter p1, PlayerCharacter p2)
    {
        if (p1 == null || p2 == null || _logs > 30)
            return;
        try
        {
            var mouse = UnityEngine.InputSystem.Mouse.current?.leftButton.isPressed == true;
            if (mouse && !_mouseWasDown && Gun(p1) != null)
            {
                _p1Ammo = Gun(p1).m_CurrentAmmo;
                _p1At = Time.realtimeSinceStartup + 0.6f;
                _bulletsAt = Time.realtimeSinceStartup + 0.12f;
                _bulletsWho = "P1";
                _logs++;
                Plugin.Logger.LogInfo($"Gun P1 fire pressed: {Describe(p1)}");
            }
            _mouseWasDown = mouse;
            if (P2Keys.Pressed(P2Keys.Action.Fire) && Gun(p2) != null)
            {
                _p2Ammo = Gun(p2).m_CurrentAmmo;
                _p2At = Time.realtimeSinceStartup + 0.6f;
                _bulletsAt = Time.realtimeSinceStartup + 0.12f;
                _bulletsWho = "P2";
                _logs++;
                Plugin.Logger.LogInfo($"Gun P2 fire pressed: {Describe(p2)} keyHeld={P2Keys.Held(P2Keys.Action.Fire)}");
            }
            if (_bulletsAt > 0f && Time.realtimeSinceStartup >= _bulletsAt)
            {
                _bulletsAt = -1f;
                Plugin.Logger.LogInfo($"Gun {_bulletsWho} bullets 0.12 s after fire: {Bullets()}");
            }
            if (_p1At > 0f && Time.realtimeSinceStartup >= _p1At)
            {
                _p1At = -1f;
                Plugin.Logger.LogInfo($"Gun P1 0.6 s later: shots={_p1Ammo - Gun(p1).m_CurrentAmmo} {Describe(p1)}");
            }
            if (_p2At > 0f && Time.realtimeSinceStartup >= _p2At)
            {
                _p2At = -1f;
                Plugin.Logger.LogInfo($"Gun P2 0.6 s later: shots={_p2Ammo - Gun(p2).m_CurrentAmmo} {Describe(p2)}");
            }
        }
        catch (Exception exception)
        {
            _logs++;
            Plugin.Logger.LogWarning($"Gun probe: {exception.Message}");
        }
    }
}
