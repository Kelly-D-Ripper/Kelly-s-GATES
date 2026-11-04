using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

internal sealed class FakeInput : IInputSystem
{
    internal readonly HashSet<KeyCode> Held = new HashSet<KeyCode>();
    internal readonly HashSet<KeyCode> Down = new HashSet<KeyCode>();
    public Vector3 mousePosition => default;
    public Vector2 mouseScrollDelta => default;
    public bool mousePresent => false;
    public bool anyKey => Held.Count != 0;
    public bool anyKeyDown => Down.Count != 0;
    public IEnumerable<KeyCode> SupportedKeyCodes => (KeyCode[])Enum.GetValues(typeof(KeyCode));
    public bool GetKey(string key) => false;
    public bool GetKey(KeyCode key) => Held.Contains(key);
    public bool GetKeyDown(string key) => false;
    public bool GetKeyDown(KeyCode key) => Down.Contains(key);
    public bool GetKeyUp(string key) => false;
    public bool GetKeyUp(KeyCode key) => false;
    public bool GetMouseButton(int button) => false;
    public bool GetMouseButtonDown(int button) => false;
    public bool GetMouseButtonUp(int button) => false;
    public void ResetInputAxes() { }
}

internal static class ShortcutTests
{
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Run(Assembly plugin, string game)
    {
        var providerField = typeof(UnityInput).GetField("_current", BindingFlags.NonPublic | BindingFlags.Static);
        var prior = providerField.GetValue(null);
        var fake = new FakeInput();
        providerField.SetValue(null, fake);
        try
        {
            var helper = plugin.GetType("UrsaMinorThrottle.ShortcutKeys", true);
            var isDown = helper.GetMethod("IsDown", BindingFlags.Static | BindingFlags.NonPublic);
            Func<KeyboardShortcut, bool> tested = key => (bool)isDown.Invoke(null, new object[] { key });
            var f9 = new KeyboardShortcut(KeyCode.F9);
            fake.Held.Add(KeyCode.F9); fake.Down.Add(KeyCode.F9);
            Check(f9.IsDown() && tested(f9), "Both shortcut handlers should accept an ordinary F9 press.");
            fake.Held.Add(KeyCode.JoystickButton8); fake.Held.Add(KeyCode.JoystickButton15); fake.Held.Add(KeyCode.JoystickButton19);
            Check(!f9.IsDown(), "Expected the original BepInEx shortcut to reject held joystick buttons.");
            Check(tested(f9), "Held joystick buttons must not block the replacement F9 handler.");
            fake.Held.Add(KeyCode.LeftControl);
            Check(!tested(f9), "An unrequested keyboard modifier must block an unmodified shortcut.");
            var ctrlF9 = new KeyboardShortcut(KeyCode.F9, KeyCode.LeftControl);
            Check(tested(ctrlF9), "An explicitly configured keyboard modifier must work with held joystick buttons.");
            fake.Held.Remove(KeyCode.LeftControl);
            Check(!tested(ctrlF9), "A missing required keyboard modifier must prevent the shortcut.");
            fake.Held.Remove(KeyCode.F9); fake.Down.Remove(KeyCode.F9);
            foreach (var key in new[] { KeyCode.F1, KeyCode.F10 })
            {
                fake.Held.Add(key); fake.Down.Add(key);
                Check(tested(new KeyboardShortcut(key)), key + " must work with held joystick buttons.");
                fake.Held.Remove(key); fake.Down.Remove(key);
            }
            Check(!tested(new KeyboardShortcut(KeyCode.None)), "An empty shortcut must not fire.");
        }
        finally { providerField.SetValue(null, prior); }

        var manager = Assembly.LoadFrom(Path.Combine(game, "BepInEx", "plugins", "ConfigurationManager", "ConfigurationManager.dll"));
        var update = AccessTools.Method(manager.GetType("ConfigurationManager.ConfigurationManager", true), "Update");
        var original = PatchProcessor.GetOriginalInstructions(update, (ILGenerator)null);
        var patch = plugin.GetType("UrsaMinorThrottle.ConfigurationManagerShortcutPatch", true);
        var transpiler = patch.GetMethod("Transpiler", BindingFlags.Static | BindingFlags.NonPublic);
        var transformed = ((IEnumerable<CodeInstruction>)transpiler.Invoke(null, new object[] { original })).ToList();
        int index = transformed.FindIndex(c => c.opcode == OpCodes.Call && c.operand is MethodInfo m && m.Name == "IsDownByRef");
        Check(index > 0 && (transformed[index - 1].opcode == OpCodes.Ldloca || transformed[index - 1].opcode == OpCodes.Ldloca_S),
            "Configuration Manager replacement must preserve its by-reference shortcut local.");
        Check(transformed.Count(c => c.operand is MethodInfo m && m.Name == "IsDownByRef") == 1,
            "Configuration Manager must have exactly one replaced shortcut check.");
        var changed = PatchProcessor.GetOriginalInstructions(update, (ILGenerator)null);
        var target = changed.First(c => c.operand is MethodInfo m && m.DeclaringType == typeof(KeyboardShortcut) && m.Name == "IsDown");
        target.opcode = OpCodes.Nop; target.operand = null;
        try
        {
            transpiler.Invoke(null, new object[] { changed });
            throw new Exception("A changed Configuration Manager shortcut must be refused.");
        }
        catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException) { }
        Console.WriteLine("PASS: held joystick buttons reproduce the BepInEx shortcut failure; replacement accepts F1/F9/F10 and honours keyboard modifiers");
        Console.WriteLine("PASS: Configuration Manager shortcut transpiler preserves the by-reference call and refuses changed IL");
    }
}
