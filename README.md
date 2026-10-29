# Kelly's GATES

Detent-aware throttle calibration for Nuclear Option. Client-only BepInEx 5 mod.

> **Draft / experimental:** Aaron reports the airbrake fix in the earlier URSA build appears to work. The broader 0.6.0 build has passed offline checks but has not been tested in Unity or on other hardware. Multiplayer has not been verified. See [COMPATIBILITY.md](COMPATIBILITY.md).

GATES detects an analogue throttle axis without relying on a manufacturer name, maps the physical MIL stop to the aircraft's afterburner threshold where it can find one, and supports separate left/right levers or one shared lever. It preserves the earlier URSA calibration, airbrake binding and config file. The original plugin ID, DLL and legacy control action names intentionally remain unchanged for compatibility: install only one copy.

## Requirements and installation

- Nuclear Option on Windows, BepInEx 5, and [Extra Input Framework](https://github.com/Assassin1076/NuclearOptionInputFramework/releases) for your game version. Install the framework separately, following its instructions; it is a shared dependency and is not included here.
- Close the game and install `UrsaMinorThrottle.dll` in `BepInEx/plugins/UrsaMinorThrottle/`. If updating the earlier URSA mod, replace that existing DLL rather than adding a second copy. Preserve the config and other plugins.
- Disable **Use Throttle Relative Axis** in the game. You may keep your usual game throttle binding as a fallback; the mod takes over only after valid calibration and idle re-arming. Do not run the separate SplitThrottle plugin alongside GATES.
- F9 opens setup and F10 advances it. Other mods may use the same shortcuts; change this mod's shortcuts in BepInEx configuration if needed. The panel can be opened from a menu, and the simulation does not pause when opened in flight.

## Calibration

Start with a safely stationary aircraft. Physical and software controller calibration should already be complete. Some controllers present several axes and virtual buttons: move only the intended lever when detecting it.

1. Open F9 and select **LEVERS: SINGLE** for one throttle axis, or **DUAL** for two independent axes. For dual axes, unlock mechanically linked levers during detection.
2. Select **BELOW IDLE: ANALOG GATE** only when the range behind the idle stop produces distinct values on the *same* axis. Otherwise select **NONE**. A switch-only cutoff is not an analogue reverse range.
3. Press F10 while controls are still, then move the requested lever through its travel. In dual mode, repeat for the right lever. If several axes move together, retry with only the requested lever moving.
4. Capture **OFF** behind the idle gate if enabled, **IDLE**, **MIL** at the intended dry-thrust stop, then **MAX** at full forward. Press F10 at each position. Without a physical MIL stop, select a convenient position as the software afterburner transition.
5. Return the throttle to idle and close F9. The mod waits for idle before taking over. Closing or changing mode before a successful capture leaves the previous calibration intact.

In F9 **Options**, bind an airbrake keyboard key or joystick button. The button toggles by default and can be changed to hold. Other options include airbrake at zero, split/averaged engines, native reverse and its maximum power. Reverse needs an analogue range below idle, both levers in that range, a compatible aircraft with native reverse vectoring, and a speed within the aircraft's native limit. It does not add reverse to aircraft that lack it; independent reverse per engine is not supported. Helicopter collective remains stock.

During focus loss the mod holds the last throttle command, which can still accelerate the aircraft. On focus return, aircraft change or reconnect, return the throttle to idle to re-arm. In setup, flight buttons are suppressed and last throttle held, but the simulation and flight axes remain active. Test safely.

## Multiplayer

The mod targets only the local pilot's locally simulated aircraft. Separate engine commands and the separate airbrake state are not transmitted by the stock protocol, so remote visuals can differ. Hosted and joined sessions, server acceptance, observer behaviour and network recovery have not been tested.

## Building

Build `UrsaMinorThrottle.csproj` for .NET Framework 4.7.2 with the game, BepInEx and Extra Input Framework installed. Set `NUCLEAR_OPTION_DIR` or pass `/p:GameDir=...`; use `/p:InputFrameworkPath=...` if the framework is elsewhere. Game and dependency binaries are not included in the source. Offline tests: `dotnet run --project tests/CurveTests.csproj -c Release`.

Settings are in `BepInEx/config/com.aaron.ursaminorthrottle.cfg`. To remove GATES, close the game and remove its DLL; do not remove shared dependencies needed by other mods.
