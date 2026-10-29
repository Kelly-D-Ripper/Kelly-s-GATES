using System;
using System.Collections.Generic;
using UrsaMinorThrottle;

static class Program
{
    static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    static LeverOutput Map(Detents d, float x, float? gate = 0.82f)
    {
        Assert(ThrottleCurve.TryMap(d, x, gate, out var o), "mapping rejected valid input");
        return o;
    }
    static void Near(float actual, float expected, string name, float tolerance = 0.0001f)
        => Assert(Math.Abs(actual - expected) <= tolerance, $"{name}: expected {expected}, got {actual}");

    static void TestDirection(Detents d)
    {
        Assert(d.IsValid(), "calibration should be valid");
        var off = Map(d, d.Off); Near(off.Forward, 0, "off forward"); Near(off.Reverse, 1, "off reverse");
        var idle = Map(d, d.Idle); Near(idle.Forward, 0, "idle forward"); Near(idle.Reverse, 0, "idle reverse");
        var mil = Map(d, d.Mil); Near(mil.Forward, .81f, "MIL"); Near(mil.Reverse, 0, "MIL reverse");
        var max = Map(d, d.Maximum); Near(max.Forward, 1, "maximum"); Near(max.Reverse, 0, "max reverse");
        Near(Map(d, d.Mil, null).Forward, (d.Mil - d.Idle) / (d.Maximum - d.Idle), "non-afterburner");
        float last = -1;
        float lastReverse = 2;
        for (int i = 0; i <= 10000; i++)
        {
            float x = d.Off + (d.Maximum - d.Off) * i / 10000f;
            var o = Map(d, x);
            Assert(o.Forward >= -1e-6 && o.Forward <= 1 && o.Reverse >= 0 && o.Reverse <= 1, "range");
            Assert(o.Forward >= last - 1e-6, "forward must be monotonic");
            Assert(o.Reverse <= lastReverse + 1e-6, "reverse must diminish toward idle");
            Assert(o.Forward == 0 || o.Reverse == 0, "forward and reverse must not overlap");
            last = o.Forward;
            lastReverse = o.Reverse;
        }
        float noise = d.Idle + (d.Off - d.Idle) * .05f;
        Near(Map(d, noise).Reverse, 0, "idle noise");
    }

    static Dictionary<string, float> Axes(float left, float right) => new Dictionary<string, float> { ["left"] = left, ["right"] = right };

    static void TestMultiplayerOwnership()
    {
        Assert(ControlEligibility.CanOwn(true, true, true), "local simulated player can own controls");
        Assert(!ControlEligibility.CanOwn(false, true, true), "non-flight session must not own controls");
        Assert(!ControlEligibility.CanOwn(true, false, true), "another aircraft must not own controls");
        Assert(!ControlEligibility.CanOwn(true, true, false), "remote simulation or lost authority must not own controls");

        // Airbrake can work while the axis modifier gives throttle back to the game;
        // engines and reverse additionally need live lever control. Exercise every
        // combination so no remote, stale or setup case can bypass those conditions.
        for (int bits = 0; bits < 256; bits++)
        {
            bool mode = (bits & 1) != 0, local = (bits & 2) != 0, sim = (bits & 4) != 0;
            bool active = (bits & 8) != 0, fresh = (bits & 16) != 0, panelOpen = (bits & 32) != 0;
            bool requireLever = (bits & 64) != 0, leverActive = (bits & 128) != 0;
            bool expected = mode && local && sim && active && fresh && !panelOpen && (!requireLever || leverActive);
            Assert(ControlEligibility.CanApply(mode, local, sim, active, fresh, panelOpen, requireLever, leverActive) == expected,
                "ownership and freshness guard mismatch at flags " + bits);
        }
    }

    static void TestFullWizard()
    {
        var wizard = new CalibrationWizard();
        Assert(!wizard.Next(Axes(0, 0)) && wizard.Phase == CalibrationPhase.DetectingLeft, "start left detection");
        wizard.Observe(Axes(.45f, 0));
        Assert(wizard.Phase == CalibrationPhase.ReadyForRight && wizard.LeftKey == "left", "left lever detected");
        Assert(!wizard.Next(Axes(.45f, 0)) && wizard.Phase == CalibrationPhase.DetectingRight, "start right detection");
        wizard.Observe(Axes(.45f, .45f));
        Assert(wizard.Phase == CalibrationPhase.Off && wizard.RightKey == "right", "right lever detected");
        Assert(!wizard.Next(new Dictionary<string, float> { ["left"] = 0 }) && wizard.Phase == CalibrationPhase.Off, "missing axis must not capture");
        Assert(!wizard.Next(Axes(0, 0)) && wizard.Phase == CalibrationPhase.Idle, "capture OFF");
        Assert(!wizard.Next(Axes(.019f, .020f)) && wizard.Phase == CalibrationPhase.Mil, "capture narrow measured IDLE gate");
        Assert(!wizard.Next(Axes(.759f, .76f)) && wizard.Phase == CalibrationPhase.Maximum, "capture MIL");
        Assert(wizard.Next(Axes(1, 1)) && wizard.Phase == CalibrationPhase.Complete, "valid full calibration commits");
        Assert(wizard.LeftKey == "left" && wizard.RightKey == "right", "axis identities survive the full capture sequence");
        Near(wizard.LeftDetents.Idle, .019f, "saved idle");
        Near(Map(wizard.LeftDetents, .019f).Reverse, 0, "measured idle has no reverse");
        Assert(!wizard.Next(Axes(1, 1)), "completed calibration must not commit twice");

        var invalid = new CalibrationWizard();
        invalid.Next(Axes(0, 0)); invalid.Observe(Axes(.45f, 0));
        invalid.Next(Axes(.45f, 0)); invalid.Observe(Axes(.45f, .45f));
        invalid.Next(Axes(0, 0)); invalid.Next(Axes(0, 0)); invalid.Next(Axes(.75f, .75f));
        Assert(!invalid.Next(Axes(1, 1)) && invalid.Phase == CalibrationPhase.Invalid, "collapsed OFF and IDLE must never commit");
    }

    static void TestControllerModes()
    {
        foreach (bool single in new[] { false, true })
        foreach (bool gated in new[] { false, true })
        foreach (float direction in new[] { -1f, 1f })
        {
            var w = new CalibrationWizard(single, gated);
            w.Next(Axes(0, 0)); w.Observe(Axes(.45f, 0));
            if (!single) { w.Next(Axes(.45f, 0)); w.Observe(Axes(.45f, .45f)); }
            Assert(w.Phase == (gated ? CalibrationPhase.Off : CalibrationPhase.Idle), "mode selects correct first step");
            if (gated) w.Next(Axes(-direction, -direction));
            w.Next(Axes(-.6f * direction, -.6f * direction));
            w.Next(Axes(.6f * direction, .6f * direction));
            Assert(w.Next(Axes(direction, direction)), "valid controller mode commits");
            Assert((w.LeftKey == w.RightKey) == single, "single mode alone shares axis");
            var d = w.LeftDetents;
            Assert(d.NoReverseGate == !gated, "gate mode survives capture");
            Near(Map(d, d.Idle).Reverse, 0, "idle has no reverse");
            Near(Map(d, d.Mil).Forward, .81f, "MIL remains dry");
            Near(Map(d, d.Maximum).Forward, 1, "full forward remains full");
            if (gated) Near(Map(d, d.Off).Reverse, 1, "gate commands reverse");
            else
            {
                Near(Map(d, d.Idle - direction * .2f).Reverse, 0, "no-gate mode never commands reverse below idle");
                Near(Map(d, d.Idle - direction * .2f).Forward, 0, "no-gate input clamps below idle");
            }
        }
    }

    sealed class FakeEngine { public object Owner; }
    static void TestRegisteredEngineDiscovery()
    {
        var owner = new object(); var other = new object();
        var detached = new FakeEngine { Owner = owner }; var child = new FakeEngine { Owner = owner };
        var foreign = new FakeEngine { Owner = other };
        var result = OwnedDiscovery.Merge(new[] { detached, detached, foreign }, new[] { child, detached, foreign }, e => ReferenceEquals(e.Owner, owner));
        Assert(result.Count == 2 && result.Contains(detached) && result.Contains(child), "registered detached and child engines are found once, foreign engines excluded");
        result = OwnedDiscovery.Merge(new FakeEngine[0], new[] { child }, e => ReferenceEquals(e.Owner, owner));
        Assert(result.Count == 1 && result[0] == child, "hierarchy fallback works with an empty registry");
    }

    static void TestUserControlOptions()
    {
        var detector = new ButtonDetector(new[] { "held-switch" });
        Assert(detector.Observe(new[] { "held-switch" }, out var many) == null && !many, "held switches must not bind themselves");
        Assert(detector.Observe(new[] { "held-switch", "joy:27" }, out many) == "joy:27" && !many, "new joystick button binds");
        detector = new ButtonDetector(new[] { "held-switch" });
        Assert(detector.Observe(new[] { "held-switch", "joy:27", "joy:28" }, out many) == null && many, "simultaneous new buttons are rejected");
        Assert(detector.Observe(new[] { "held-switch" }, out many) == null && !many, "release after ambiguity re-arms detection");
        Assert(detector.Observe(new[] { "held-switch", "key:65" }, out many) == "key:65", "a subsequent single keyboard key binds");
        Assert(ControlPolicies.ReadAirbrakeMap(false, 3, 3), "read the assigned button on the flight-disabled Debug map");
        Assert(!ControlPolicies.ReadAirbrakeMap(false, 2, 3), "do not read a different disabled map");
        Assert(ControlPolicies.ReadAirbrakeMap(false, 3, -1), "recognize the observed category 3 binding when no named Debug category exists");
        Assert(ControlPolicies.ReadAirbrakeMap(true, 2, 3), "continue accepting enabled maps");
        for (int bits = 0; bits < 8; bits++)
        {
            bool open = (bits & 1) != 0, flight = (bits & 2) != 0, local = (bits & 4) != 0;
            Assert(ControlEligibility.SuppressPilotButtons(open, flight, local) == (open && flight && local),
                "setup suppresses pilot buttons only for the local flight pilot");
        }
        float left = .2f, right = .8f;
        ControlPolicies.ApplySplit(true, ref left, ref right);
        Near(left, .2f, "split left"); Near(right, .8f, "split right");
        ControlPolicies.ApplySplit(false, ref left, ref right);
        Near(left, .5f, "linked left average"); Near(right, .5f, "linked right average");
        var idle = new LeverOutput(0f, 0f); var forward = new LeverOutput(.2f, 0f); var reverse = new LeverOutput(0f, .4f);
        Assert(!ControlPolicies.Airbrake(false, false, idle, idle), "idle airbrake defaults off");
        Assert(ControlPolicies.Airbrake(true, false, forward, forward), "button opens airbrake above idle");
        Assert(ControlPolicies.Airbrake(false, true, idle, idle), "optional automatic airbrake opens at idle");
        Assert(!ControlPolicies.Airbrake(false, true, idle, forward), "one lever above idle prevents auto brake");
        Assert(!ControlPolicies.Airbrake(false, true, reverse, reverse), "reverse is not normal idle");
        Assert(!ControlPolicies.Fresh(4, false) && ControlPolicies.Fresh(4, true), "only an explicit focus hold permits a stale frame");
        Assert(!ControlPolicies.Fresh(-1, false) && ControlPolicies.Fresh(1, false), "ordinary frame guard remains in place");
    }

    public static void Main()
    {
        TestUserControlOptions();
        TestRegisteredEngineDiscovery();
        TestControllerModes();
        TestDirection(new Detents { Off = -1f, Idle = -.72f, Mil = .60f, Maximum = 1f });
        TestDirection(new Detents { Off = 1f, Idle = .72f, Mil = -.60f, Maximum = -1f });
        var f99Calibration = new Detents { Off = -1f, Idle = -.962005f, Mil = .517998f, Maximum = 1f };
        Near(Map(f99Calibration, f99Calibration.Mil, .9f).Forward, .89f, "saved physical MIL stays in the F-16M MIL display region");
        Assert(Map(f99Calibration, f99Calibration.Mil - .006f, .9f).Forward < .9f, "F-99 stays below burner threshold just before the gate");
        Assert(Map(f99Calibration, f99Calibration.Mil + .03f, .9f).Forward > .9f, "afterburner crosses threshold shortly after the gate");
        Assert(!new Detents { Off = 0, Idle = 0, Mil = .8f, Maximum = 1 }.IsValid(), "collapsed off/idle must fail");
        Assert(!new Detents { Off = -1, Idle = -.8f, Mil = .2f, Maximum = float.NaN }.IsValid(), "NaN must fail");
        Assert(!new Detents { Off = -1, Idle = -.8f, Mil = -.9f, Maximum = 1 }.IsValid(), "order must fail");
        Assert(!ThrottleCurve.TryMap(new Detents { Off = -1, Idle = -.8f, Mil = .6f, Maximum = 1 }, float.NaN, .8f, out _), "NaN input must fail");
        Assert(ThrottleCurve.BothAtIdle(new LeverOutput(0, 0), new LeverOutput(.01f, 0)), "both near-idle levers may arm");
        Assert(!ThrottleCurve.BothAtIdle(new LeverOutput(.4f, 0), new LeverOutput(0, 0)), "forward lever must not arm");
        Assert(!ThrottleCurve.BothAtIdle(new LeverOutput(0, 0), new LeverOutput(0, .3f)), "reverse lever must not arm");
        Assert(!ThrottleCurve.BothAtIdle(new LeverOutput(float.NaN, 0), new LeverOutput(0, 0)), "invalid lever must not arm");
        Near(EngineRouting.Power(-2, .2f, .8f, false, false), .2f, "left engine");
        Near(EngineRouting.Power(2, .2f, .8f, false, false), .8f, "right engine");
        Near(EngineRouting.Power(0, .2f, .8f, false, false), .5f, "shared engine");
        Near(EngineRouting.Power(2, .2f, .8f, true, false), 0, "non-reversing engine is idle during reverse");
        Near(EngineRouting.Power(2, .2f, .8f, true, true), .8f, "native reversing engine retains reverse command");
        var detector = new AxisDetector(new Dictionary<string, float> { ["left"] = 0, ["right"] = 0 }, null);
        Assert(!detector.TrySelect(new Dictionary<string, float> { ["left"] = .05f, ["right"] = 0 }, out _, out _), "small axis noise must not bind");
        Assert(detector.TrySelect(new Dictionary<string, float> { ["left"] = .8f, ["right"] = .02f }, out var chosen, out _) && chosen == "left", "independent lever must bind");
        Assert(!detector.TrySelect(new Dictionary<string, float> { ["left"] = .8f, ["right"] = .8f }, out _, out var ambiguous) && ambiguous, "linked levers must be rejected");
        var rightDetector = new AxisDetector(new Dictionary<string, float> { ["left"] = 0, ["right"] = 0 }, "left");
        Assert(rightDetector.TrySelect(new Dictionary<string, float> { ["left"] = .9f, ["right"] = .5f }, out chosen, out _) && chosen == "right", "left axis must not be selected twice");
        Assert(!rightDetector.TrySelect(new Dictionary<string, float> { ["right"] = float.NaN }, out _, out _), "invalid axis samples must be ignored");
        TestFullWizard();
        TestMultiplayerOwnership();
        Console.WriteLine("PASS: detent calibration, both axis orientations, reverse, afterburner and monotonic mapping");
        Console.WriteLine("PASS: focus hold freshness, split/linked throttle and airbrake option policies");
        Console.WriteLine("PASS: separate axis detection, jitter rejection, linked-axis rejection and invalid samples");
        Console.WriteLine("PASS: full axis detection and four-position calibration, missing axis, narrow gate and invalid calibration");
        Console.WriteLine("PASS: local aircraft, simulation authority, stale-frame, calibration-panel and lever-control guard combinations");
    }
}
