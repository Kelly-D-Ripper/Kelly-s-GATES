using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace UrsaMinorThrottle
{
    internal static class NativeReflection
    {
        private static readonly FieldInfo EngineStates = AccessTools.Field(typeof(Aircraft), "engineStates");
        private static readonly FieldInfo Engines = AccessTools.Field(typeof(Aircraft), "engines");
        private static readonly FieldInfo NozzleAircraft = AccessTools.Field(typeof(JetNozzle), "aircraft");
        private static readonly Dictionary<Type, FieldInfo> AircraftFields = new Dictionary<Type, FieldInfo>();
        private static readonly FieldInfo Burners = AccessTools.Field(typeof(JetNozzle), "afterburners");
        private static readonly FieldInfo ThrustTransform = AccessTools.Field(typeof(JetNozzle), "thrustTransform");
        private static readonly FieldInfo SwivelLimit = AccessTools.Field(typeof(DuctedThrustSystem), "swivelLimit");
        private static readonly FieldInfo MaxReverseSpeed = AccessTools.Field(typeof(DuctedThrustSystem), "maxReverseSpeed");
        private static readonly FieldInfo ReverseTurbojets = AccessTools.Field(typeof(DuctedThrustSystem), "turbojets");
        private static readonly FieldInfo ReverseTurbofans = AccessTools.Field(typeof(DuctedThrustSystem), "turbofans");
        private static readonly Dictionary<Type, FieldInfo> NozzleFields = new Dictionary<Type, FieldInfo>();

        internal static bool DiscoveryAvailable => EngineStates != null && Engines != null && NozzleAircraft != null && Burners != null && ThrustTransform != null;

        internal static bool BelongsTo(Aircraft aircraft, Component engine)
        {
            if (engine == null || aircraft == null) return false;
            var type = engine.GetType();
            if (!AircraftFields.TryGetValue(type, out var field))
            {
                field = AccessTools.Field(type, "aircraft");
                AircraftFields[type] = field;
            }
            return field != null && (field.GetValue(engine) as Aircraft) == aircraft;
        }

        internal static bool NozzleBelongsTo(Aircraft aircraft, JetNozzle nozzle) =>
            nozzle != null && aircraft != null && (NozzleAircraft.GetValue(nozzle) as Aircraft) == aircraft;

        internal static IEnumerable<Component> RegisteredEngines(Aircraft aircraft)
        {
            foreach (var field in new[] { EngineStates, Engines })
            {
                var list = field.GetValue(aircraft) as IEnumerable;
                if (list == null) continue;
                foreach (var item in list)
                    if (item is Component component &&
                        (component is Turbojet || component is Turbofan || component is ConstantSpeedProp || component is PropFan))
                        yield return component;
            }
        }

        internal static JetNozzle[] EngineNozzles(Component engine)
        {
            if (!(engine is Turbojet) && !(engine is Turbofan)) return null;
            var type = engine.GetType();
            if (!NozzleFields.TryGetValue(type, out var field))
            {
                field = AccessTools.Field(type, "nozzles");
                NozzleFields[type] = field;
            }
            return field?.GetValue(engine) as JetNozzle[];
        }

        internal static float? AfterburnerStart(JetNozzle nozzle)
        {
            var burners = Burners?.GetValue(nozzle) as Array;
            if (burners == null) return null;
            float? best = null;
            foreach (var burner in burners)
            {
                if (burner == null) continue;
                var field = AccessTools.Field(burner.GetType(), "throttleStart");
                if (field == null) continue;
                float v = (float)field.GetValue(burner);
                if (v > 0.02f && v < 0.99f && (!best.HasValue || v < best.Value)) best = v;
            }
            return best;
        }

        internal static float LocalNozzleX(Aircraft aircraft, JetNozzle nozzle)
        {
            var transform = ThrustTransform?.GetValue(nozzle) as Transform;
            if (transform == null) transform = nozzle.transform;
            return aircraft.transform.InverseTransformPoint(transform.position).x;
        }

        internal static bool SupportsReverse(DuctedThrustSystem duct, out float maxSpeed)
        {
            maxSpeed = 0f;
            if (SwivelLimit == null || MaxReverseSpeed == null) return false;
            float limit = (float)SwivelLimit.GetValue(duct);
            maxSpeed = (float)MaxReverseSpeed.GetValue(duct);
            return limit > 90.1f && maxSpeed > 0f && !float.IsNaN(maxSpeed) && !float.IsInfinity(maxSpeed);
        }

        internal static bool AddReverseEngines(DuctedThrustSystem duct, HashSet<Component> engines)
        {
            if (ReverseTurbojets == null || ReverseTurbofans == null) return false;
            var jets = ReverseTurbojets.GetValue(duct) as Turbojet[];
            var fans = ReverseTurbofans.GetValue(duct) as Turbofan[];
            bool found = false;
            if (jets != null) foreach (var engine in jets) if (engine != null) { engines.Add(engine); found = true; }
            if (fans != null) foreach (var engine in fans) if (engine != null) { engines.Add(engine); found = true; }
            return found;
        }

        internal static float EngineX(Aircraft aircraft, Component engine)
        {
            var nozzles = EngineNozzles(engine);
            if (nozzles != null && nozzles.Length > 0)
            {
                float sum = 0f;
                int count = 0;
                foreach (var nozzle in nozzles)
                {
                    if (nozzle == null) continue;
                    sum += LocalNozzleX(aircraft, nozzle);
                    count++;
                }
                if (count > 0) return sum / count;
            }
            return aircraft.transform.InverseTransformPoint(engine.transform.position).x;
        }
    }
}
