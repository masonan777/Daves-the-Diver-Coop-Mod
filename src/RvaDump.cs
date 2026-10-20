using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;

namespace DaveTheDiverCoop;

// Diagnostics: write where some of the game's functions live inside
// GameAssembly.dll, so they can be read offline. Runs before any patching.
internal static class RvaDump
{
    private static readonly Type[] Types =
    {
        typeof(PlayerCharacter), typeof(SubHelperItemInventory), typeof(SubHelperHandler), typeof(BoosterHandler),
        typeof(CharacterAbilityHandler), typeof(DashAbility), typeof(ShortDashAbility), typeof(SubHelperSlotData),
        typeof(SubHelperSpecData), typeof(PlayerBreathHandler), typeof(InstanceItemInventory),
        typeof(GunWeaponHandler), typeof(GunAim), typeof(DefaultGunGear), typeof(InstanceItemHandler), typeof(RangeAttackArmHandler), typeof(GunBullet),
    };

    internal static unsafe void Write(string path)
    {
        try
        {
            IntPtr baseAddress = IntPtr.Zero;
            foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
                if (string.Equals(module.ModuleName, "GameAssembly.dll", StringComparison.OrdinalIgnoreCase))
                    baseAddress = module.BaseAddress;
            var text = new StringBuilder();
            text.AppendLine($"base {baseAddress.ToInt64():X}");
            foreach (var type in Types)
            {
                foreach (var field in type.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
                {
                    if (!field.Name.StartsWith("NativeMethodInfoPtr_") || field.FieldType != typeof(IntPtr))
                        continue;
                    try
                    {
                        var info = (IntPtr)field.GetValue(null);
                        if (info == IntPtr.Zero)
                            continue;
                        var code = *(IntPtr*)info;
                        if (code == IntPtr.Zero)
                            continue;
                        text.AppendLine($"{code.ToInt64() - baseAddress.ToInt64():X} {type.Name}.{field.Name.Substring("NativeMethodInfoPtr_".Length)}");
                    }
                    catch
                    {
                    }
                }
            }
            File.WriteAllText(path, text.ToString());
            Plugin.Logger.LogInfo($"Function addresses written to {path}");
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning($"Function address dump failed: {exception.Message}");
        }
    }
}
