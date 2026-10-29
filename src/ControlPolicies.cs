namespace UrsaMinorThrottle
{
    internal static class ControlPolicies
    {
        // The game's Debug map can be disabled during flight, but this explicitly
        // assigned mod action still needs to read its own button on that map.
        internal static bool ReadAirbrakeMap(bool enabled, int category, int debugCategory) =>
            enabled || category == 3 || (debugCategory >= 0 && category == debugCategory);

        internal static bool Fresh(int age, bool focusHold) => (age >= 0 && age <= 1) || focusHold;
        internal static void ApplySplit(bool split, ref float left, ref float right)
        {
            if (!split) left = right = (left + right) * 0.5f;
        }
        internal static bool Airbrake(bool button, bool autoAtIdle, LeverOutput left, LeverOutput right) =>
            button || (autoAtIdle && ThrottleCurve.BothAtIdle(left, right));
    }
}
