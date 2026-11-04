# Kelly's GATES 0.7.1 — helicopter control and performance

Adds a separate **HELICOPTER** page in F9. Capture zero collective and maximum collective; GATES detects the moved axis and supports either direction. Helicopter calibration is independent of fixed-wing detents, and existing calibration and airbrake bindings are preserved.

Helicopter recognition now checks registered aircraft-owned rotor shafts, the aircraft prefab and the live rotor hierarchy, with retries while the aircraft loads. This addresses the path where a calibration could save while the aircraft remained on stock collective controls. The log now identifies the aircraft and detection result.

AI and nonlocal engine updates return before engine-field reflection. Local engine inputs and nozzle transforms are cached, while live positions and lever/button values are still sampled. Controller identity, index and description lookups are cached with disconnect checks. Legacy bindings and mapped airbrake buttons are rescanned every half second and on focus return; cached button presses continue to be sampled every frame.

Also included since 0.6.0:

- Alt-Tab resumes at the current physical lever position after input focus returns, without requiring idle solely for a focus change. An aircraft change, reconnection or new calibration still requires idle/zero to arm.
- The local throttle gauge can account for different HUD and engine afterburner boundaries, including the F-22E's 0.90 HUD boundary and 0.95 engine threshold.
- Setup enumerates all axes only during detection or capture.

Install only the `UrsaMinorThrottle.dll` from `Kellys-GATES-0.7.1.zip`, replacing the existing copy while the game is closed. BepInEx 5 and Extra Input Framework remain separate prerequisites. Keep `BepInEx/config/com.aaron.ursaminorthrottle.cfg`; no database migration or configuration reset is needed. The optional embedded DOORMAN card retains the author and GitHub/Discord links; artwork is still omitted.

Validation: build, calibration/curve tests, game-hook contract checks and archive/hash verification passed against the locally installed game. The DLL is byte-for-byte the local 0.7.1 test candidate. Helicopter takeover, auto-hover, controller reconnect, focus recovery and multiplayer need in-game acceptance; no measured FPS gain is claimed. Broader throttle hardware remains untested. See `README.md` and `COMPATIBILITY.md`.
