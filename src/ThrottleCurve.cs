using System;

namespace UrsaMinorThrottle
{
    // Values are captured from the game's two explicitly bound throttle actions.
    internal struct Detents
    {
        internal float Off, Idle, Mil, Maximum;
        // An explicit mode keeps an accidental collapsed gate invalid for existing users.
        internal bool NoReverseGate;

        internal bool IsValid()
        {
            if (!Finite(Off) || !Finite(Idle) || !Finite(Mil) || !Finite(Maximum)) return false;
            float span = Maximum - Off;
            if (Math.Abs(span) < 0.20f) return false;
            float idle = (Idle - Off) / span;
            float mil = (Mil - Off) / span;
            bool validIdle = NoReverseGate ? Math.Abs(idle) < 0.00001f : idle >= 0.015f && idle <= 0.55f;
            return validIdle && mil >= idle + 0.10f && mil <= 0.97f;
        }

        private static bool Finite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value) && value >= -1.001f && value <= 1.001f;
        }
    }

    internal struct LeverOutput
    {
        internal readonly float Forward;
        internal readonly float Reverse;
        internal LeverOutput(float forward, float reverse) { Forward = forward; Reverse = reverse; }
    }

    internal static class ThrottleCurve
    {
        internal static bool BothAtIdle(LeverOutput left, LeverOutput right)
        {
            return IsIdle(left.Forward) && IsIdle(left.Reverse) && IsIdle(right.Forward) && IsIdle(right.Reverse);
        }

        private static bool IsIdle(float value) => !float.IsNaN(value) && !float.IsInfinity(value) && value >= 0f && value <= 0.02f;

        internal static bool TryMap(Detents d, float raw, float? afterburnerStart, out LeverOutput output)
        {
            output = new LeverOutput(0f, 0f);
            if (!d.IsValid() || float.IsNaN(raw) || float.IsInfinity(raw)) return false;

            float span = d.Maximum - d.Off;
            float idle = (d.Idle - d.Off) / span;
            float mil = (d.Mil - d.Off) / span;
            float n = Clamp01((raw - d.Off) / span);

            // A small band below the mechanical stop prevents noise at idle commanding reverse.
            float idleMargin = Math.Min(0.008f, idle * 0.12f);
            float reverseBoundary = idle - idleMargin;
            if (n < reverseBoundary)
            {
                output = new LeverOutput(0f, Clamp01((reverseBoundary - n) / reverseBoundary));
                return true;
            }
            if (n <= idle)
                return true;

            float forward;
            if (!afterburnerStart.HasValue || !FiniteThreshold(afterburnerStart.Value))
            {
                // On aircraft without the standard afterburner component, use the full forward travel.
                forward = (n - idle) / (1f - idle);
            }
            else
            {
                // At the detent itself stay at the dry-thrust limit; cross it to light the burner.
                float gate = afterburnerStart.Value;
                // The game can label the exact burner threshold as 0% AFTERBURNER.
                // Leave a small margin inside MIL and cross the threshold just past the gate.
                float dry = Math.Max(0f, gate - Math.Min(0.01f, gate * 0.1f));
                float plateau = Math.Min(0.0025f, Math.Min((mil - idle) * 0.1f, (1f - mil) * 0.1f));
                float lo = mil - plateau, hi = mil + plateau;
                float transition = Math.Min(0.005f, (1f - hi) * 0.25f);
                if (n < lo) forward = dry * (n - idle) / (lo - idle);
                else if (n <= hi) forward = dry;
                else if (n < hi + transition) forward = dry + (gate - dry) * (n - hi) / transition;
                else forward = gate + (1f - gate) * (n - hi - transition) / (1f - hi - transition);
            }
            output = new LeverOutput(Clamp01(forward), 0f);
            return true;
        }

        private static bool FiniteThreshold(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value) && value > 0.02f && value < 0.99f;
        }

        private static float Clamp01(float value) { return Math.Max(0f, Math.Min(1f, value)); }
    }
}
