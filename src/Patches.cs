using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace UrsaMinorThrottle
{
    [HarmonyPatch(typeof(PilotPlayerState), "PlayerThrottleAxis1Controls")]
    internal static class PilotThrottlePatch
    {
        [HarmonyPostfix, HarmonyPriority(Priority.Last)]
        private static void Postfix(PilotPlayerState __instance) => Plugin.Instance?.OnPilot(__instance);
    }

    // Prevent clicks on the panel from also firing weapons or activating pilot buttons.
    // Other pilots and the game's separate flight-axis update are untouched.
    [HarmonyPatch(typeof(PilotPlayerState), "PlayerControls")]
    internal static class SetupPilotControlsPatch
    {
        private static bool Prefix(PilotPlayerState __instance)
        {
            try { return Plugin.Instance == null || !Plugin.Instance.BlocksLocalPilotControls(__instance); }
            catch (Exception e) { Plugin.Instance?.ReportHookFailure("Setup input", e); return true; }
        }
    }

    [HarmonyPatch(typeof(Airbrake), "Update")]
    internal static class AirbrakePatch
    {
        private static readonly FieldInfo Aircraft = AccessTools.Field(typeof(Airbrake), "attachedAircraft");

        private static bool ShouldOpen(float nativeThrottle, Airbrake brake)
        {
            var plugin = Plugin.Instance;
            try
            {
                if (plugin != null && plugin.ControlsAircraft(Aircraft?.GetValue(brake) as Aircraft, false))
                    return plugin.AirbrakeOpen;
            }
            catch (Exception e) { plugin?.ReportHookFailure("Airbrake", e); }
            return nativeThrottle == 0f;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            var throttle = AccessTools.Field(typeof(ControlInputs), "throttle");
            var controls = AccessTools.Field(typeof(Airbrake), "controlInputs");
            int found = -1;
            for (int i = 2; i < codes.Count - 2; i++)
            {
                if (codes[i - 2].opcode == OpCodes.Ldarg_0 && codes[i - 1].opcode == OpCodes.Ldfld && Equals(codes[i - 1].operand, controls) &&
                    codes[i].opcode == OpCodes.Ldfld && Equals(codes[i].operand, throttle) &&
                    codes[i + 1].opcode == OpCodes.Ldc_R4 && codes[i + 1].operand is float value && value == 0f &&
                    (codes[i + 2].opcode == OpCodes.Beq || codes[i + 2].opcode == OpCodes.Beq_S))
                {
                    if (found >= 0) throw new InvalidOperationException("Airbrake zero-throttle comparison is ambiguous.");
                    found = i;
                }
            }
            if (found < 0) throw new InvalidOperationException("Airbrake zero-throttle comparison changed; patch refused.");
            codes[found + 1].opcode = OpCodes.Ldarg_0;
            codes[found + 1].operand = null;
            codes[found + 2].opcode = codes[found + 2].opcode == OpCodes.Beq_S ? OpCodes.Brtrue_S : OpCodes.Brtrue;
            codes.Insert(found + 2, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(AirbrakePatch), nameof(ShouldOpen))));
            return codes;
        }
    }

    // Override the native swivel only for its own physics call. Restore the shared input
    // even on exception so returning through the gate cannot leave vectoring stuck.
    [HarmonyPatch(typeof(DuctedThrustSystem), "Swivel")]
    internal static class NativeReverseScopePatch
    {
        internal struct SavedInput { internal ControlInputs Inputs; internal float Value; }
        private static readonly FieldInfo Aircraft = AccessTools.Field(typeof(DuctedThrustSystem), "aircraft");
        private static readonly FieldInfo Inputs = AccessTools.Field(typeof(DuctedThrustSystem), "inputs");

        private static void Prefix(DuctedThrustSystem __instance, out SavedInput __state)
        {
            __state = default;
            var plugin = Plugin.Instance;
            try
            {
                if (plugin == null || !plugin.ReverseActive ||
                    !plugin.ControlsAircraft(Aircraft?.GetValue(__instance) as Aircraft, true) ||
                    !NativeReflection.SupportsReverse(__instance, out _))
                    return;
                var inputs = Inputs?.GetValue(__instance) as ControlInputs;
                if (inputs == null) return;
                __state = new SavedInput { Inputs = inputs, Value = inputs.customAxis1 };
                inputs.customAxis1 = 0f;
            }
            catch (Exception e)
            {
                if (__state.Inputs != null) __state.Inputs.customAxis1 = __state.Value;
                __state = default;
                plugin?.ReportHookFailure("Reverse", e);
            }
        }

        private static Exception Finalizer(Exception __exception, SavedInput __state)
        {
            if (__state.Inputs != null) __state.Inputs.customAxis1 = __state.Value;
            return __exception;
        }
    }

    // The game shares a single throttle value; temporarily supply each engine its own
    // lever value while that engine calculates thrust, then restore the original value.
    [HarmonyPatch]
    internal static class EngineThrottlePatch
    {
        internal struct SavedInput { internal ControlInputs Inputs; internal float Value; }
        private struct EngineFields { internal FieldInfo Aircraft, Inputs; }
        private static readonly Dictionary<Type, EngineFields> Fields = new Dictionary<Type, EngineFields>();

        private static EngineFields GetFields(Type type)
        {
            if (Fields.TryGetValue(type, out var fields)) return fields;
            fields = new EngineFields
            {
                Aircraft = AccessTools.Field(type, "aircraft"),
                Inputs = AccessTools.Field(type, type == typeof(PropFan) ? "inputs" : "controlInputs")
            };
            Fields[type] = fields;
            return fields;
        }

        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Turbojet), "FixedUpdate");
            yield return AccessTools.Method(typeof(Turbofan), "FixedUpdate");
            yield return AccessTools.Method(typeof(ConstantSpeedProp), "FixedUpdate");
            yield return AccessTools.Method(typeof(PropFan), "FixedUpdate");
        }

        private static void Prefix(Component __instance, out SavedInput __state)
        {
            __state = default;
            var plugin = Plugin.Instance;
            try
            {
                if (plugin == null) return;
                var type = __instance.GetType();
                var fields = GetFields(type);
                var aircraft = fields.Aircraft?.GetValue(__instance) as Aircraft;
                var inputs = fields.Inputs?.GetValue(__instance) as ControlInputs;
                if (inputs == null || !plugin.ControlsAircraft(aircraft, true)) return;

                float x = NativeReflection.EngineX(aircraft, __instance);
                if (float.IsNaN(x) || float.IsInfinity(x)) throw new InvalidOperationException("Engine position is invalid; using game throttle.");
                float value = EngineRouting.Power(x, plugin.LeftPower, plugin.RightPower, plugin.ReverseActive, plugin.IsNativeReverseEngine(__instance));
                __state = new SavedInput { Inputs = inputs, Value = inputs.throttle };
                inputs.throttle = value;
                plugin.ReportEngineOverride(__instance, x);
            }
            catch (Exception e)
            {
                if (__state.Inputs != null) __state.Inputs.throttle = __state.Value;
                __state = default;
                plugin?.ReportHookFailure("Split throttle", e);
            }
        }

        private static Exception Finalizer(Exception __exception, SavedInput __state)
        {
            if (__state.Inputs != null) __state.Inputs.throttle = __state.Value;
            return __exception;
        }
    }
}
