# Hardware compatibility — research, not test results

**Only the earlier WINCTRL URSA MINOR Combat Throttle build has user-reported in-game feedback. GATES 0.6.0 itself and every other device listed here remain untested.** A manufacturer's description of a detent does not prove that Nuclear Option exposes its axis or that its gated range is usable. This is a non-exhaustive list, not a guarantee or a per-device preset.

| Controller | What the manufacturer describes | Suggested starting mode |
| --- | --- | --- |
| WINCTRL URSA MINOR Combat Throttle | Earlier mod developed with this dual throttle; direct axes and gates observed locally | Dual; analogue gate if OFF and IDLE differ |
| [WINCTRL Orion 2](https://winwingsim.com/mobileview/goods-details.html?id=326) | Adjustable finger lift/detent; single/dual grips vary | Single or dual per grip; verify the gate |
| [VIRPIL MongoosT-50CM3](https://virpil-controls.eu/vpc-mongoost-50cm3-throttle-b-stock.html) | Two axes and adjustable finger-lift detents | Dual; verify the gate |
| [VIRPIL CDT-VMAX](https://virpil-controls.eu/vpc-cdt-vmax-throttle.html) | Adjustable idle/afterburner detents and an optional centre detent | Verify axes and gate for the chosen setup |
| [VKB STECS Standard Mk.II](https://flightsimcontrols.com/product/stecs-throttle-system-standard-mk-ii/) | Two analogue axes and interchangeable detent frames | Dual; verify the gate |
| [Thrustmaster HOTAS Warthog dual throttle](https://www.thrustmaster.com/en-us/products/hotas-warthog-dual-throttle/) | Two axes with idle and disengageable afterburner stops | Dual; verify whether below-idle movement is analogue |
| [Thrustmaster Viper TQS](https://www.thrustmaster.com/en-us/products/viper-tqs/) | Single throttle with cutoff and removable afterburner detents | Single; verify whether below-idle movement is analogue |
| [Thrustmaster SOL-R 6](https://www.thrustmaster.com/en-us/products/sol-r-6-throttle/) | Configurable detent at 50%, 80%, or dual positions | Single; verify the gate and selected detent setup |
| [Thrustmaster TCA Quadrant Airbus](https://www.thrustmaster.com/en-us/products/tca-quadrant-airbus-edition/) | Dual levers, multiple configurable notches and reverser mechanism | Dual; verify the chosen idle and gate |
| [Logitech X52](https://www.logitechg.com/en-us/shop/p/x52-space-flight-simulator-controller) / [X52 Pro](https://www.logitechg.com/en-us/shop/p/x52-pro-space-flight-simulator-controller.945-000022) | A throttle with detents described for idle, afterburner and reverse | Single; verify axis range and gate |
| [Turtle Beach VelocityOne Flightdeck](https://ca.turtlebeach.com/products/velocity-one-flight-deck) | Dual levers and adjustable **haptic** detent cues, not a claimed physical gate | Dual; configure the cue and verify the gate |
| [Turtle Beach VelocityOne HOTAS](https://www.turtlebeach.com/products/velocity-one-hotas) | Dual throttle with swappable tactile detents | Dual; verify the gate |

GATES expects a stable, continuously reported joystick axis with a persistent instance identity that the game can see. Vendor tools and virtual controllers can change the exposed axis, identity or range. It does not infer a switch-only cutoff, handle multiple independent detents as separate commands, synchronize remote effects, or automatically configure hardware detent positions. The axis detector rejects ambiguous simultaneous movement. Please report controller, firmware/mode, axis names, observed OFF/IDLE/MIL/MAX values, aircraft and outcome after a safe stationary test.
