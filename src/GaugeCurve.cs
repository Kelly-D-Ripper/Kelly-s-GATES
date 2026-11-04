using System;

namespace UrsaMinorThrottle
{
    // Translate only the gauge reading; never change the engine command or thrust.
    internal static class GaugeCurve
    {
        internal static bool TryMap(float input, float engineStart, float gaugeStart, out float display)
        {
            display = input;
            if (!Valid(input) || input < 0f || input > 1f || !Valid(engineStart) || !Valid(gaugeStart) ||
                engineStart <= .02f || engineStart >= .99f || gaugeStart <= .02f || gaugeStart >= .99f ||
                Math.Abs(engineStart - gaugeStart) < .001f) return false;
            display = input <= engineStart ? input * gaugeStart / engineStart :
                gaugeStart + (input - engineStart) * (1f - gaugeStart) / (1f - engineStart);
            return true;
        }
        private static bool Valid(float f) => !float.IsNaN(f) && !float.IsInfinity(f);
    }
}
