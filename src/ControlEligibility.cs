namespace UrsaMinorThrottle
{
    // Keep ownership and freshness decisions independent from Unity objects so their
    // complete truth table can be exercised without a running game.
    internal static class ControlEligibility
    {
        internal static bool CanOwn(bool flightSession, bool localAircraft, bool localSimulation) =>
            flightSession && localAircraft && localSimulation;

        internal static bool SuppressPilotButtons(bool panelOpen, bool flightSession, bool localAircraft) =>
            panelOpen && flightSession && localAircraft;

        internal static bool CanApply(bool flightSession, bool localAircraft, bool localSimulation,
            bool activeAircraft, bool fresh, bool calibrationOpen, bool requireLever, bool leverActive) =>
            CanOwn(flightSession, localAircraft, localSimulation) && activeAircraft && fresh && !calibrationOpen &&
            (!requireLever || leverActive);
    }
}
