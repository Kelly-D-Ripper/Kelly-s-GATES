using System;
using System.Collections.Generic;

namespace UrsaMinorThrottle
{
    // Two captures also identify an axis: no separate binding step is required.
    internal sealed class CollectiveCalibration
    {
        private Dictionary<string, float> zeroSamples;
        internal bool WaitingForMaximum => zeroSamples != null;
        internal string Binding { get; private set; }
        internal float Zero { get; private set; }
        internal float Maximum { get; private set; }
        internal string Message { get; private set; } = "Move the helicopter lever to ZERO, then press F10.";

        internal bool Next(IDictionary<string, float> axes)
        {
            if (axes == null || axes.Count == 0) { Message = "No controller axes found. Connect a controller and retry."; return false; }
            if (zeroSamples == null)
            {
                zeroSamples = new Dictionary<string, float>(axes);
                Message = "Move ONLY the helicopter lever to MAXIMUM, then press F10. Unlock linked levers first.";
                return false;
            }
            var detector = new AxisDetector(zeroSamples, null);
            if (!detector.TrySelect(axes, out string key, out bool ambiguous))
            {
                Message = ambiguous ? "Multiple axes moved. Unlock the levers and restart ZERO capture." : "No clear axis movement. Move the lever from ZERO to MAXIMUM and retry.";
                return false;
            }
            if (!zeroSamples.TryGetValue(key, out float zero) || !axes.TryGetValue(key, out float maximum) || !TryMap(zero, maximum, zero, out _))
            { Message = "Invalid axis range. Restart ZERO capture and try again."; return false; }
            Binding = key; Zero = zero; Maximum = maximum;
            Message = "Helicopter calibration saved. Return the lever to ZERO, then close this panel.";
            return true;
        }

        internal static bool TryMap(float zero, float maximum, float raw, out float value)
        {
            value = 0f;
            if (!Finite(zero) || !Finite(maximum) || !Finite(raw) || Math.Abs(maximum - zero) < .25f) return false;
            value = Math.Max(0f, Math.Min(1f, (raw - zero) / (maximum - zero)));
            return true;
        }

        internal static float NativeThrottle(float output, bool invert, bool negative)
        {
            float native = invert ? 1f - output : output;
            return negative ? native * 2f - 1f : native;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value) && value >= -1.001f && value <= 1.001f;
    }
}
