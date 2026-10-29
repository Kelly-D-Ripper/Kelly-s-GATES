using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace UrsaMinorThrottle
{
    // BepInEx 5 treats every held joystick button as an extra shortcut modifier.
    // A physical throttle can hold several buttons continuously at a detent.
    internal static class ShortcutKeys
    {
        private static readonly KeyCode[] KeyboardModifiers =
        {
            KeyCode.LeftShift, KeyCode.RightShift, KeyCode.LeftControl, KeyCode.RightControl,
            KeyCode.LeftAlt, KeyCode.RightAlt, KeyCode.LeftCommand, KeyCode.RightCommand, KeyCode.AltGr
        };

        internal static bool IsDown(KeyboardShortcut shortcut)
        {
            try
            {
                if (shortcut.MainKey == KeyCode.None) return false;
                var input = UnityInput.Current;
                if (input == null || !input.GetKeyDown(shortcut.MainKey)) return false;
                var required = new HashSet<KeyCode>(shortcut.Modifiers);
                foreach (var key in required)
                    if (!input.GetKey(key)) return false;
                foreach (var key in KeyboardModifiers)
                    if (key != shortcut.MainKey && !required.Contains(key) && input.GetKey(key)) return false;
                return true;
            }
            catch (Exception e)
            {
                Plugin.Instance?.ReportHookFailure("Keyboard shortcut", e);
                return false;
            }
        }

        // The manager calls the instance method on a by-reference struct local.
        internal static bool IsDownByRef(ref KeyboardShortcut shortcut) => IsDown(shortcut);
    }

    // Patch only Configuration Manager's key check, not BepInEx globally or other mods.
    internal static class ConfigurationManagerShortcutPatch
    {
        internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            var original = AccessTools.Method(typeof(KeyboardShortcut), nameof(KeyboardShortcut.IsDown));
            var replacement = AccessTools.Method(typeof(ShortcutKeys), nameof(ShortcutKeys.IsDownByRef));
            int found = 0;
            foreach (var code in codes)
            {
                if (code.opcode == OpCodes.Call && Equals(code.operand, original))
                {
                    code.operand = replacement;
                    found++;
                }
            }
            if (found != 1) throw new InvalidOperationException("Configuration Manager shortcut shape changed; optional fix not applied.");
            return codes;
        }
    }
}
