using System;
using System.Collections.Generic;

namespace UrsaMinorThrottle
{
    internal enum CalibrationPhase
    {
        Ready, DetectingLeft, ReadyForRight, DetectingRight,
        Off, Idle, Mil, Maximum, Complete, Invalid
    }

    // Commit only a complete validated calibration; a single lever deliberately shares one axis.
    internal sealed class CalibrationWizard
    {
        private AxisDetector detector;
        private readonly float[] left = new float[4], right = new float[4];
        internal bool SingleLever { get; }
        internal bool HasReverseGate { get; }
        internal CalibrationWizard(bool singleLever = false, bool hasReverseGate = true)
        { SingleLever = singleLever; HasReverseGate = hasReverseGate; }
        internal CalibrationPhase Phase { get; private set; } = CalibrationPhase.Ready;
        internal string LeftKey { get; private set; }
        internal string RightKey { get; private set; }
        internal Detents LeftDetents { get; private set; }
        internal Detents RightDetents { get; private set; }
        internal string Message { get; private set; } = "Press F10 to detect the LEFT lever. Leave both levers still until prompted.";

        internal void Observe(IDictionary<string, float> axes)
        {
            if (detector == null || (Phase != CalibrationPhase.DetectingLeft && Phase != CalibrationPhase.DetectingRight)) return;
            if (!detector.TrySelect(axes, out var selected, out var ambiguous))
            {
                if (ambiguous) Message = "Multiple axes moved. Unlock the levers, hold them still and press F10 to restart detection.";
                return;
            }
            if (Phase == CalibrationPhase.DetectingLeft)
            {
                LeftKey = selected;
                if (SingleLever)
                {
                    RightKey = selected;
                    Phase = HasReverseGate ? CalibrationPhase.Off : CalibrationPhase.Idle;
                    Message = HasReverseGate ? "Lever detected. Move fully back beyond the idle gate (OFF), then press F10." : "Lever detected. Move to IDLE, then press F10.";
                }
                else
                {
                    Phase = CalibrationPhase.ReadyForRight;
                    Message = "LEFT detected. Stop both levers, then press F10 to detect RIGHT.";
                }
            }
            else
            {
                RightKey = selected;
                Phase = HasReverseGate ? CalibrationPhase.Off : CalibrationPhase.Idle;
                Message = HasReverseGate ? "Both detected. Move BOTH fully back past the idle gates (OFF), then press F10." : "Both detected. Move BOTH to IDLE, then press F10.";
            }
            detector = null;
        }

        // Returns true only once, when a complete valid calibration is ready to commit.
        internal bool Next(IDictionary<string, float> axes)
        {
            if (Phase == CalibrationPhase.Ready || Phase == CalibrationPhase.DetectingLeft || Phase == CalibrationPhase.Invalid)
            {
                LeftKey = RightKey = null;
                return Arm(axes, false);
            }
            if (Phase == CalibrationPhase.ReadyForRight || Phase == CalibrationPhase.DetectingRight)
                return Arm(axes, true);
            if (Phase == CalibrationPhase.Complete) return false;

            if (LeftKey == null || RightKey == null || (LeftKey == RightKey) != SingleLever ||
                !axes.TryGetValue(LeftKey, out var l) || !axes.TryGetValue(RightKey, out var r) || !FiniteAxis(l) || !FiniteAxis(r))
            {
                Message = "A selected axis is unavailable. Reconnect it, or close and restart calibration.";
                return false;
            }

            int index = Phase == CalibrationPhase.Off ? 0 : Phase == CalibrationPhase.Idle ? 1 : Phase == CalibrationPhase.Mil ? 2 : 3;
            left[index] = l;
            right[index] = r;
            if (Phase == CalibrationPhase.Idle && !HasReverseGate) { left[0] = l; right[0] = r; }
            if (Phase == CalibrationPhase.Off) { Phase = CalibrationPhase.Idle; Message = "Move BOTH levers to IDLE, then press F10."; }
            else if (Phase == CalibrationPhase.Idle) { Phase = CalibrationPhase.Mil; Message = "Move to the MIL detent (or desired burner start), then press F10."; }
            else if (Phase == CalibrationPhase.Mil) { Phase = CalibrationPhase.Maximum; Message = "Move BOTH levers to full forward, then press F10."; }
            else
            {
                var leftDetents = FromSamples(left, !HasReverseGate);
                var rightDetents = FromSamples(right, !HasReverseGate);
                if (!leftDetents.IsValid() || !rightDetents.IsValid())
                {
                    Phase = CalibrationPhase.Invalid;
                    Message = HasReverseGate ? "Calibration rejected: OFF, IDLE, MIL and MAX must be distinct and in order. Press F10 to retry." : "Calibration rejected: IDLE, MIL and MAX must be distinct and in order. Press F10 to retry.";
                    return false;
                }
                LeftDetents = leftDetents;
                RightDetents = rightDetents;
                Phase = CalibrationPhase.Complete;
                Message = "Calibration saved. Return the throttle to IDLE, then close this panel.";
                return true;
            }
            return false;
        }

        private bool Arm(IDictionary<string, float> axes, bool rightSide)
        {
            if (axes == null || axes.Count == 0)
            {
                Message = "No controller axes found. Check the connection, then press F10.";
                return false;
            }
            detector = new AxisDetector(axes, rightSide ? LeftKey : null);
            Phase = rightSide ? CalibrationPhase.DetectingRight : CalibrationPhase.DetectingLeft;
            Message = SingleLever ? "Move the throttle lever through its travel. Buttons are ignored." : "Move ONLY the " + (rightSide ? "RIGHT" : "LEFT") + " lever through its travel. Buttons are ignored.";
            return false;
        }

        private static bool FiniteAxis(float value) => !float.IsNaN(value) && !float.IsInfinity(value) && value >= -1.001f && value <= 1.001f;
        private static Detents FromSamples(float[] a, bool noReverse) => new Detents { Off = a[0], Idle = a[1], Mil = a[2], Maximum = a[3], NoReverseGate = noReverse };
    }
}
