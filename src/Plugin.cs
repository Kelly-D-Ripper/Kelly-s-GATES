using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using InputFramework;
using Rewired;
using UnityEngine;

namespace UrsaMinorThrottle
{
    [BepInPlugin(Id, "Kelly's GATES", "0.7.1")]
    [BepInDependency("experimental.assassin1076.extrainputframework", BepInDependency.DependencyFlags.HardDependency)]
    public sealed class Plugin : BaseUnityPlugin
    {
        internal const string Id = "com.aaron.ursaminorthrottle";
        internal const string LeftAction = "UrsaThrottle::LeftLever";
        internal const string RightAction = "UrsaThrottle::RightLever";
        internal const string AirbrakeAction = "UrsaThrottle::Airbrake";
        internal static Plugin Instance;

        private static readonly FieldInfo PlayerField = AccessTools.Field(typeof(PilotPlayerState), "player");
        private static readonly FieldInfo PilotField = AccessTools.Field(typeof(PilotBaseState), "pilot");
        private static readonly FieldInfo InputsField = AccessTools.Field(typeof(PilotBaseState), "controlInputs");
        private static readonly FieldInfo CollectiveField = AccessTools.Field(typeof(PilotPlayerState), "collective");
        private static readonly FieldInfo SimThrottleField = AccessTools.Field(typeof(PilotPlayerState), "simulatedThrottle");
        private static readonly Dictionary<string, DirectAxisId> DirectIds = new Dictionary<string, DirectAxisId>();

        private struct DirectAxisId
        {
            internal Guid Instance, Hardware;
            internal int Axis;
            internal bool Valid;
            internal Joystick Device;
            internal int Index;
            internal string Description;
        }
        private struct DirectButtonId
        {
            internal bool Valid, Keyboard;
            internal KeyCode Key;
            internal Guid Instance, Hardware;
            internal int Button, Index;
            internal Joystick Device;
            internal string Description;
        }
        private static readonly Dictionary<string, DirectButtonId> DirectButtons = new Dictionary<string, DirectButtonId>();

        private ConfigEntry<bool> packEnabled;
        private ConfigEntry<bool> calibrated;
        private ConfigEntry<bool> singleLever;
        private ConfigEntry<bool> holdAirbrake, idleAirbrake, splitThrottles, reverseEnabled;
        private ConfigEntry<string> airbrakeBinding;
        private ConfigEntry<float> reversePower;
        private ConfigEntry<bool> collectiveCalibrated;
        private ConfigEntry<string> collectiveBinding;
        private ConfigEntry<float> collectiveZero, collectiveMaximum;
        private CollectiveCalibration collectiveWizard;
        private bool collectivePending, collectiveActive, collectiveFocusFrozen, collectiveAwaitZero = true;
        private float collectiveHeld;
        private int collectiveFrame;
        private Aircraft collectiveAircraft, collectiveClassifiedAircraft;
        private bool classifiedHelicopter;
        private int nextHelicopterScan;
        private bool helicopterScanLogged;
        private ConfigEntry<KeyboardShortcut> showCalibration;
        private ConfigEntry<KeyboardShortcut> advanceCalibration;
        private LeverConfig leftConfig, rightConfig;
        private bool windowOpen, airbrakeLatched, airbrakeHeld, airbrakeCanPress, awaitIdleAfterCalibration, pendingAdvance, hooksReady;
        private bool focusFrozen, focusReverse, forcedCursor;
        private readonly FocusPickup focusPickup = new FocusPickup();
        private string lastAirbrakeBindingStatus;
        private sealed class MappedButton { internal ControllerMap Map; internal ActionElementMap Element; internal int ActionId; }
        private readonly List<MappedButton> mappedAirbrakes = new List<MappedButton>();
        private Player mappedAirbrakePlayer;
        private float nextAirbrakeMapScan;
        private int debugMapCategory = -1;
        private sealed class LegacyBindingCache
        {
            internal Player Player;
            internal Controller Controller;
            internal string Key, Physical, Description;
            internal float NextScan;
            internal bool Valid;
        }
        private readonly LegacyBindingCache legacyLeft = new LegacyBindingCache(), legacyRight = new LegacyBindingCache();
        private ButtonDetector buttonDetector;
        private string buttonCaptureMessage = "";
        private float buttonCaptureReadyAt;
        private static readonly KeyCode[] KeyboardKeys = (KeyCode[])Enum.GetValues(typeof(KeyCode));
        private Player activePlayer;
        private bool managerShortcutAttempted;
        private bool shortcutMonitorReported;
        private Harmony managerShortcutHarmony;
        private CalibrationWizard wizard;
        private NativeThrottlePanel panel;
        private const CursorFlags PanelCursor = (CursorFlags)(1 << 27);
        private string message = "Press F10 to begin detecting the left lever.";
        private string leftDescription = "not bound", rightDescription = "not bound";
        private float leftRaw, rightRaw;
        private int lastAircraftScan = -1;
        private int cachedEngineCount = -1, cachedEngineStateCount = -1;
        private Aircraft cachedAircraft, armingAircraft;
        private float? leftBurner, rightBurner;
        private bool nativeReverse;
        private float nativeReverseSpeedLimit;
        private string engineTopology = "not scanned";
        private string activeEngineStatus = "active";
        private readonly HashSet<string> reportedHookFailures = new HashSet<string>();
        private readonly HashSet<Component> reverseEngines = new HashSet<Component>();
        private readonly HashSet<Component> reportedEngines = new HashSet<Component>();
        private string lastFlightStatus;
        private bool leftMilReported, rightMilReported, leftBurnerReported, rightBurnerReported;

        internal Aircraft ActiveAircraft { get; private set; }
        internal bool Active { get; private set; }
        internal bool LeverControl { get; private set; }
        internal bool AirbrakeOpen { get; private set; }
        internal bool ReverseActive { get; private set; }
        internal float LeftPower { get; private set; }
        internal float RightPower { get; private set; }
        internal int ActiveFrame { get; private set; }
        internal bool IsNativeReverseEngine(Component engine) => reverseEngines.Contains(engine);

        internal bool TryGaugeValue(Aircraft aircraft, float input, float displayStart, out float display)
        {
            display = input;
            if (!ControlsAircraft(aircraft, true) || ReverseActive || !leftBurner.HasValue || !rightBurner.HasValue ||
                Math.Abs(leftBurner.Value - rightBurner.Value) > .001f) return false;
            return GaugeCurve.TryMap(input, (leftBurner.Value + rightBurner.Value) * .5f, displayStart, out display);
        }

        internal static bool IsFlightSession(GameState state) =>
            state == GameState.SinglePlayer || state == GameState.Multiplayer;

        // A remote aircraft or a stale update must never inherit local pilot commands.
        internal bool ControlsAircraft(Aircraft aircraft, bool requireLever)
        {
            if (!hooksReady || !packEnabled.Value || aircraft == null) return false;
            int frameAge = Time.frameCount - ActiveFrame;
            return ControlEligibility.CanApply(IsFlightSession(GameManager.gameState),
                GameManager.IsLocalAircraft(aircraft), aircraft.LocalSim,
                Active && !awaitIdleAfterCalibration && ActiveAircraft == aircraft, ControlPolicies.Fresh(frameAge, focusFrozen),
                windowOpen, requireLever, LeverControl);
        }

        private void Awake()
        {
            Instance = this;
            packEnabled = Config.Bind("General", "Enabled", true, "Enable this compatibility pack after calibration.");
            calibrated = Config.Bind("Calibration", "Complete", false, "Set by the calibration wizard.");
            singleLever = Config.Bind("Calibration", "SingleLever", false, "Set by the calibration wizard; one axis controls both engines.");
            holdAirbrake = Config.Bind("Airbrake", "HoldInsteadOfToggle", false, "True: hold the airbrake button. False: press to toggle.");
            airbrakeBinding = Config.Bind("Airbrake", "ButtonBinding", "", "Set in F9 Options. Empty uses the game's UrsaThrottle::Airbrake binding.");
            idleAirbrake = Config.Bind("Airbrake", "DeployAtZeroThrottle", false, "Deploy airbrakes automatically when both levers are at idle; independent of the button.");
            splitThrottles = Config.Bind("General", "SplitThrottles", true, "Control left and right engines separately; disabled uses the average of both levers.");
            reverseEnabled = Config.Bind("Reverse", "Enabled", true, "Allow native reverse on supported aircraft.");
            reversePower = Config.Bind("Reverse", "MaximumPower", 0.5f,
                new ConfigDescription("Maximum native engine throttle used while in reverse; range 0.05 to 0.90.", new AcceptableValueRange<float>(0.05f, 0.90f)));
            showCalibration = Config.Bind("Calibration", "ShowOrCancel", new KeyboardShortcut(KeyCode.F9), "Open or close the calibration panel.");
            advanceCalibration = Config.Bind("Calibration", "Capture", new KeyboardShortcut(KeyCode.F10), "Advance and capture the current lever positions.");
            leftConfig = new LeverConfig(Config, "Left lever");
            rightConfig = new LeverConfig(Config, "Right lever");
            collectiveCalibrated = Config.Bind("Helicopter", "Complete", false, "Set by the helicopter calibration screen.");
            collectiveBinding = Config.Bind("Helicopter", "Axis", "", "Detected helicopter collective axis.");
            collectiveZero = Config.Bind("Helicopter", "Zero", 0f, "Captured zero collective.");
            collectiveMaximum = Config.Bind("Helicopter", "Maximum", 0f, "Captured maximum collective.");
            // Do not take over at an arbitrary lever position after a game restart.
            awaitIdleAfterCalibration = calibrated.Value;

            var harmony = new Harmony(Id);
            try
            {
                ExtraInputManager.RegisterAction(LeftAction, InputActionType.Axis);
                ExtraInputManager.RegisterAction(RightAction, InputActionType.Axis);
                ExtraInputManager.RegisterAction(AirbrakeAction, InputActionType.Button);
                ValidateGameHooks();
                harmony.PatchAll();
                hooksReady = true;
                Logger.LogInfo("Ursa throttle patches installed; calibration is required before they take control.");
            }
            catch (Exception e)
            {
                try { harmony.UnpatchSelf(); }
                catch (Exception unpatchError) { Logger.LogError("Could not clean up throttle patches: " + unpatchError); }
                hooksReady = false;
                Logger.LogError("Could not install throttle patches: " + e);
            }
        }

        private static void ValidateGameHooks()
        {
            if (!NativeReflection.DiscoveryAvailable ||
                AccessTools.Field(typeof(Turbojet), "nozzles") == null || AccessTools.Field(typeof(Turbofan), "nozzles") == null ||
                PlayerField == null || PilotField == null || InputsField == null || CollectiveField == null || SimThrottleField == null ||
                AccessTools.Field(typeof(Pilot), "aircraft") == null ||
                AccessTools.Field(typeof(Airbrake), "attachedAircraft") == null || AccessTools.Field(typeof(Airbrake), "controlInputs") == null ||
                AccessTools.Field(typeof(ControlInputs), "throttle") == null || AccessTools.Field(typeof(ControlInputs), "customAxis1") == null ||
                AccessTools.Method(typeof(PilotPlayerState), "PlayerThrottleAxis1Controls") == null ||
                AccessTools.Method(typeof(PilotPlayerState), "PlayerControls") == null ||
                AccessTools.Method(typeof(Airbrake), "Update") == null || AccessTools.Method(typeof(DuctedThrustSystem), "Swivel") == null ||
                AccessTools.Field(typeof(DuctedThrustSystem), "aircraft") == null || AccessTools.Field(typeof(DuctedThrustSystem), "inputs") == null ||
                AccessTools.Field(typeof(DuctedThrustSystem), "swivelLimit") == null || AccessTools.Field(typeof(DuctedThrustSystem), "maxReverseSpeed") == null)
                throw new InvalidOperationException("The game's flight-control interfaces changed. The throttle pack is inactive.");
            foreach (var type in new[] { typeof(Turbojet), typeof(Turbofan), typeof(ConstantSpeedProp), typeof(PropFan) })
                if (AccessTools.Method(type, "FixedUpdate") == null || AccessTools.Field(type, "aircraft") == null ||
                    AccessTools.Field(type, type == typeof(PropFan) ? "inputs" : "controlInputs") == null)
                    throw new InvalidOperationException("A game engine interface changed: " + type.Name + ". The throttle pack is inactive.");
        }

        private void OnDestroy()
        {
            EngineThrottlePatch.Clear();
            try { managerShortcutHarmony?.UnpatchSelf(); }
            catch (Exception e) { Logger.LogWarning("Could not remove Configuration Manager shortcut patch: " + e); }
            ClosePanel();
            if (Instance == this) Instance = null;
        }

        private void TryPatchConfigurationManagerShortcut()
        {
            if (managerShortcutAttempted ||
                !BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue("com.bepis.bepinex.configurationmanager", out var info) ||
                info.Instance == null) return;
            managerShortcutAttempted = true;
            try
            {
                var update = AccessTools.Method(info.Instance.GetType(), "Update");
                if (update == null) throw new InvalidOperationException("Configuration Manager Update is unavailable.");
                managerShortcutHarmony = new Harmony(Id + ".configmanager");
                managerShortcutHarmony.Patch(update, transpiler: new HarmonyMethod(typeof(ConfigurationManagerShortcutPatch), nameof(ConfigurationManagerShortcutPatch.Transpiler)));
                Logger.LogInfo("Configuration Manager shortcut accepts held controller buttons.");
            }
            catch (Exception e)
            {
                try { managerShortcutHarmony?.UnpatchSelf(); }
                catch (Exception cleanup) { Logger.LogWarning("Could not clean up Configuration Manager shortcut patch: " + cleanup); }
                managerShortcutHarmony = null;
                Logger.LogWarning("Configuration Manager shortcut fix unavailable; throttle controls remain available: " + e);
            }
        }

        // Listen independently of the pilot update, so setup also works in menus and
        // reports a missing pilot hook instead of silently ignoring the hotkey.
        private void Update()
        {
            try
            {
                if (!shortcutMonitorReported)
                {
                    Logger.LogInfo("Throttle shortcut monitor active; controller buttons do not block its hotkeys.");
                    shortcutMonitorReported = true;
                }
                TryPatchConfigurationManagerShortcut();
                try { PollAirbrakeButton(); }
                catch (Exception e) { ReportHookFailure("Airbrake input", e); }
                if (showCalibration != null && ShortcutKeys.IsDown(showCalibration.Value))
                    ToggleCalibration();
                if (!windowOpen) return;
                if (panel == null || !panel.Alive) { ClosePanel(); return; }
                if (buttonDetector != null && !panel.IsOptions) buttonDetector = null;
                if (buttonDetector != null) CaptureAirbrakeButton();
                if (panel.IsHelicopter)
                {
                    if (ShortcutKeys.IsDown(advanceCalibration.Value) || collectivePending)
                    {
                        collectivePending = false;
                        if (collectiveWizard.Next(SnapshotThrottleAxes()))
                        {
                            // Save only once both endpoints and the selected axis are valid.
                            collectiveCalibrated.Value = false;
                            collectiveBinding.Value = collectiveWizard.Binding;
                            collectiveZero.Value = collectiveWizard.Zero;
                            collectiveMaximum.Value = collectiveWizard.Maximum;
                            collectiveCalibrated.Value = true;
                            Config.Save(); collectiveAwaitZero = true;
                            Logger.LogInfo("Helicopter collective calibration saved; return the lever to zero before activation.");
                        }
                    }
                    panel.Refresh(collectiveCalibrated.Value, packEnabled.Value, "", "", "", wizard.Phase, "", engineTopology, "",
                        new[] { packEnabled.Value, splitThrottles.Value, idleAirbrake.Value, holdAirbrake.Value, reverseEnabled.Value }, reversePower.Value,
                        false, "", wizard.SingleLever, wizard.HasReverseGate);
                    panel.RefreshHelicopter(collectiveCalibrated.Value, collectiveWizard.Message);
                    return;
                }
                bool advance = !panel.IsOptions && (ShortcutKeys.IsDown(advanceCalibration.Value) || pendingAdvance);
                bool detecting = !panel.IsOptions && (wizard.Phase == CalibrationPhase.DetectingLeft || wizard.Phase == CalibrationPhase.DetectingRight);
                var snapshot = advance || detecting ? SnapshotThrottleAxes() : null;
                string oldLeft = wizard.LeftKey, oldRight = wizard.RightKey;
                if (snapshot != null) wizard.Observe(snapshot);
                if (wizard.LeftKey != oldLeft) Logger.LogInfo("Detected left throttle axis: " + wizard.LeftKey);
                if (wizard.RightKey != oldRight) Logger.LogInfo("Detected right throttle axis: " + wizard.RightKey);
                if (wizard.LeftKey != null && TryReadDirect(wizard.LeftKey, out var l, out var leftName))
                { leftRaw = l; leftDescription = leftName; }
                else if (wizard.LeftKey == null) leftDescription = "not detected";
                else leftDescription = "device/axis unavailable";
                if (wizard.RightKey != null && TryReadDirect(wizard.RightKey, out var r, out var rightName))
                { rightRaw = r; rightDescription = rightName; }
                else if (wizard.RightKey == null) rightDescription = "not detected";
                else rightDescription = "device/axis unavailable";
                if (advance)
                {
                    pendingAdvance = false;
                    AdvanceWizard(snapshot);
                }
                message = wizard.Message;
                panel.Refresh(calibrated.Value, packEnabled.Value, leftDescription + "   " + leftRaw.ToString("F3"),
                    rightDescription + "   " + rightRaw.ToString("F3"), message, wizard.Phase,
                    "NEXT / CAPTURE  " + advanceCalibration.Value + "       CLOSE  " + showCalibration.Value,
                    engineTopology, "AFTERBURNER L / R  " + FormatThreshold(leftBurner) + " / " + FormatThreshold(rightBurner) +
                    "     NATIVE REVERSE  " + (nativeReverse ? "AVAILABLE" : "NOT DETECTED"),
                    new[] { packEnabled.Value, splitThrottles.Value, idleAirbrake.Value, holdAirbrake.Value, reverseEnabled.Value }, reversePower.Value,
                    buttonDetector != null, buttonDetector != null ? buttonCaptureMessage : DescribeAirbrakeBinding(),
                    wizard.SingleLever, wizard.HasReverseGate);
            }
            catch (Exception e)
            {
                message = "Calibration unavailable; see the BepInEx log.";
                if (!reportedCalibrationException) { Logger.LogWarning("Calibration panel: " + e); reportedCalibrationException = true; }
            }
        }

        private void OnApplicationFocus(bool focused)
        {
            nextAirbrakeMapScan = 0f;
            legacyLeft.NextScan = legacyRight.NextScan = 0f;
            if (!focused)
            {
                // Losing focus can neutralise inputs and make held hardware switches
                // look newly pressed on return; cancel capture without changing binding.
                buttonDetector = null;
                // Repeated focus loss while already holding must preserve the last controlled output.
                bool alreadyFrozen = focusFrozen;
                focusFrozen = alreadyFrozen || (Active && LeverControl && !windowOpen && Time.frameCount - ActiveFrame >= 0 && Time.frameCount - ActiveFrame <= 1);
                if (focusFrozen && !alreadyFrozen) focusReverse = ReverseActive;
                collectiveFocusFrozen = collectiveFocusFrozen || (collectiveActive && !windowOpen && Time.frameCount - collectiveFrame >= 0 && Time.frameCount - collectiveFrame <= 1);
                focusPickup.Lost();
                airbrakeHeld = false;
                airbrakeCanPress = false;
                if (focusFrozen) Logger.LogInfo("Focus lost: holding last throttle output until focus and valid input return.");
            }
            else if (focusFrozen || collectiveFocusFrozen) focusPickup.Restored(Time.frameCount);
        }

        private void PollAirbrakeButton()
        {
            if (!Application.isFocused || windowOpen || focusFrozen || !Active || activePlayer == null || !hooksReady || !packEnabled.Value ||
                !GameManager.flightControlsEnabled || !ControlsAircraft(ActiveAircraft, false))
            {
                nextAirbrakeMapScan = 0f;
                airbrakeHeld = false;
                airbrakeCanPress = false;
                return;
            }
            bool down;
            if (!string.IsNullOrEmpty(airbrakeBinding.Value))
            {
                bool available = TryReadAirbrakeBinding(airbrakeBinding.Value, out down, out string description);
                string directStatus = "Airbrake direct binding: " + (available ? description : "selected device/key unavailable");
                if (directStatus != lastAirbrakeBindingStatus) { lastAirbrakeBindingStatus = directStatus; Logger.LogInfo(directStatus); }
                if (!available) { airbrakeHeld = false; airbrakeLatched = false; airbrakeCanPress = false; return; }
            }
            else down = ReadMappedAirbrakeButton();
            airbrakeHeld = down;
            if (!down) { airbrakeCanPress = true; return; }
            if (airbrakeCanPress)
            {
                airbrakeCanPress = false;
                airbrakeLatched = !airbrakeLatched;
                Logger.LogInfo("Airbrake button detected; toggle state " + (airbrakeLatched ? "open" : "closed") + ".");
            }
        }

        private bool ReadMappedAirbrakeButton()
        {
            bool down = activePlayer.GetButton(AirbrakeAction);
            // The user-assigned action lives on the game's Debug map. That map can be
            // disabled during flight, so read only this explicitly mapped button even
            // then; do not enable the map or activate other Debug actions.
            if (mappedAirbrakePlayer != activePlayer || Time.unscaledTime >= nextAirbrakeMapScan)
            {
                mappedAirbrakePlayer = activePlayer; nextAirbrakeMapScan = Time.unscaledTime + .5f;
                mappedAirbrakes.Clear();
                try { debugMapCategory = ReInput.mapping.GetMapCategoryId("Debug"); }
                catch (Exception) { debugMapCategory = -1; }
                int disabledDebug = 0;
                foreach (var map in activePlayer.controllers.maps.GetAllMaps(ControllerType.Joystick))
                {
                    if (map.controller == null || !ControlPolicies.ReadAirbrakeMap(map.enabled, map.categoryId, debugMapCategory)) continue;
                    foreach (var element in map.GetElementMapsWithAction(AirbrakeAction))
                    {
                        if (!element.enabled || element.hasModifiers || element.elementType != ControllerElementType.Button) continue;
                        mappedAirbrakes.Add(new MappedButton { Map = map, Element = element, ActionId = element.actionId });
                        if (!map.enabled) disabledDebug++;
                    }
                }
                string bindingStatus = "Airbrake binding: " + mappedAirbrakes.Count + " joystick button(s), " + disabledDebug + " on an inactive action map.";
                if (bindingStatus != lastAirbrakeBindingStatus) { lastAirbrakeBindingStatus = bindingStatus; Logger.LogInfo(bindingStatus); }
            }
            foreach (var binding in mappedAirbrakes)
            {
                var controller = binding.Map.controller;
                var element = binding.Element;
                if (controller == null || !controller.isConnected) { nextAirbrakeMapScan = 0f; continue; }
                if (!ControlPolicies.ReadAirbrakeMap(binding.Map.enabled, binding.Map.categoryId, debugMapCategory) ||
                    !element.enabled || element.actionId != binding.ActionId || element.hasModifiers || element.elementType != ControllerElementType.Button) continue;
                if (controller.GetButtonById(element.elementIdentifierId)) down = true;
            }
            return down;
        }

        private void ToggleCalibration()
        {
            if (windowOpen) { ClosePanel(); return; }
            pendingAdvance = false;
            focusFrozen = false;
            collectiveFocusFrozen = false;
            collectiveAwaitZero = true;
            if (calibrated.Value) awaitIdleAfterCalibration = true;
            wizard = new CalibrationWizard(singleLever.Value, !leftConfig.Read().NoReverseGate);
            collectiveWizard = new CollectiveCalibration();
            collectivePending = false;
            message = wizard.Message;
            try
            {
                panel = new NativeThrottlePanel(() => pendingAdvance = true, ClosePanel, SetPanelOption, SetPanelReversePower,
                    StartOrCancelAirbrakeCapture, ClearAirbrakeBinding, SetCalibrationMode,
                    () => collectivePending = true, () => { collectiveWizard = new CollectiveCalibration(); collectivePending = false; });
                panel.Open();
                windowOpen = true;
                CursorManager.SetFlag(PanelCursor, true);
                Logger.LogInfo("Calibration panel opened; native font=" + panel.HasStockFont +
                    ", panel=" + panel.HasStockPanel + ", button artwork=" + panel.HasStockButtons + ".");
            }
            catch (Exception e)
            {
                ClosePanel();
                message = "Throttle panel unavailable; see the BepInEx log.";
                Logger.LogWarning("Could not open the throttle panel: " + e);
            }
        }

        private void SetCalibrationMode(bool single, bool hasReverseGate)
        {
            // Changing a draft mode restarts the wizard but leaves the saved calibration intact.
            wizard = new CalibrationWizard(single, hasReverseGate);
            pendingAdvance = false;
        }

        private void ClosePanel()
        {
            bool wasOpen = windowOpen;
            windowOpen = false;
            pendingAdvance = false;
            buttonDetector = null;
            if (panel != null) { panel.Dispose(); panel = null; }
            CursorManager.SetFlag(PanelCursor, false);
            if (forcedCursor)
            {
                // Restore the game's current cursor decision, including other menu flags.
                Cursor.visible = CursorManager.Visible;
                Cursor.lockState = CursorManager.Visible ? CursorLockMode.None : CursorLockMode.Locked;
                forcedCursor = false;
            }
            wizard = null;
            collectiveWizard = null;
            collectivePending = false;
            if (wasOpen) Logger.LogInfo("Calibration panel closed.");
        }

        private void LateUpdate()
        {
            if (windowOpen && !CursorManager.GetFlag(PanelCursor)) CursorManager.SetFlag(PanelCursor, true);
            if (windowOpen && Application.isFocused && (!Cursor.visible || Cursor.lockState != CursorLockMode.None))
            {
                // The game's force-hidden mode can override every cursor flag. Make the
                // panel usable without changing that global mode and restore it on close.
                if (!CursorManager.Visible) forcedCursor = true;
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
        }

        private void OnDisable() { if (windowOpen) ClosePanel(); }

        internal bool BlocksLocalPilotControls(PilotPlayerState state)
        {
            if (!windowOpen || state == null || !IsFlightSession(GameManager.gameState)) return false;
            var pilot = PilotField.GetValue(state) as Pilot;
            return ControlEligibility.SuppressPilotButtons(windowOpen, IsFlightSession(GameManager.gameState),
                pilot != null && pilot.aircraft != null && GameManager.IsLocalAircraft(pilot.aircraft));
        }

        private void SetPanelOption(int index, bool value)
        {
            switch (index)
            {
                case 0: packEnabled.Value = value; break;
                case 1: splitThrottles.Value = value; break;
                case 2: idleAirbrake.Value = value; break;
                case 3: holdAirbrake.Value = value; break;
                case 4: reverseEnabled.Value = value; break;
                default: return;
            }
            airbrakeLatched = false; airbrakeCanPress = false;
            Config.Save();
        }

        private void SetPanelReversePower(float value)
        {
            reversePower.Value = Math.Max(.05f, Math.Min(.90f, value));
            Config.Save();
        }

        private void StartOrCancelAirbrakeCapture()
        {
            if (buttonDetector != null) { buttonDetector = null; buttonCaptureMessage = ""; return; }
            buttonDetector = new ButtonDetector(SnapshotButtons());
            buttonCaptureReadyAt = Time.unscaledTime + .25f;
            buttonCaptureMessage = "RELEASE BUTTONS, THEN PRESS ONE KEY OR JOYSTICK BUTTON";
        }

        private void ClearAirbrakeBinding()
        {
            buttonDetector = null; buttonCaptureMessage = "";
            airbrakeBinding.Value = ""; airbrakeHeld = false; airbrakeLatched = false; airbrakeCanPress = false;
            Config.Save();
            Logger.LogInfo("Airbrake direct binding cleared; using the game controls binding.");
        }

        private void CaptureAirbrakeButton()
        {
            var pressed = SnapshotButtons();
            var selected = buttonDetector.Observe(pressed, out bool ambiguous);
            if (Time.unscaledTime < buttonCaptureReadyAt) return;
            if (ambiguous) { buttonCaptureMessage = "MULTIPLE INPUTS: RELEASE THEM AND PRESS JUST ONE"; return; }
            if (selected == null) return;
            airbrakeBinding.Value = selected;
            airbrakeHeld = false; airbrakeLatched = false; airbrakeCanPress = false;
            buttonDetector = null; buttonCaptureMessage = "";
            Config.Save();
            Logger.LogInfo("Airbrake direct button binding saved: " + DescribeAirbrakeBinding());
        }

        private static HashSet<string> SnapshotButtons()
        {
            var held = new HashSet<string>();
            if (ReInput.isReady)
                foreach (var joystick in ReInput.controllers.Joysticks)
                {
                    if (joystick == null || joystick.deviceInstanceGuid == Guid.Empty) continue;
                    for (int i = 0; i < joystick.buttonCount && i < joystick.Buttons.Count; i++)
                    {
                        var button = joystick.Buttons[i];
                        if (button?.elementIdentifier != null && joystick.GetButton(i))
                            held.Add("joy1|" + joystick.deviceInstanceGuid.ToString("D") + "|" + joystick.hardwareTypeGuid.ToString("D") + "|" + button.elementIdentifier.id);
                    }
                }
            var input = UnityInput.Current;
            if (input != null) foreach (var key in KeyboardKeys)
            {
                int code = (int)key;
                if (code < 8 || code > 319 || key == KeyCode.None || key == KeyCode.Escape ||
                    key == Instance.showCalibration.Value.MainKey || key == Instance.advanceCalibration.Value.MainKey) continue;
                if (input.GetKey(key)) held.Add("key1|" + code);
            }
            return held;
        }

        private string DescribeAirbrakeBinding()
        {
            if (string.IsNullOrEmpty(airbrakeBinding.Value)) return "USING GAME CONTROLS BINDING";
            return TryReadAirbrakeBinding(airbrakeBinding.Value, out _, out var description) ? description : "SAVED BUTTON NOT AVAILABLE";
        }

        private static bool TryReadAirbrakeBinding(string binding, out bool down, out string description)
        {
            down = false; description = "not available";
            if (string.IsNullOrEmpty(binding)) return false;
            if (!DirectButtons.TryGetValue(binding, out var saved))
            {
                var parts = binding.Split('|');
                if (parts.Length == 2 && parts[0] == "key1" && int.TryParse(parts[1], out int code) && code >= 8 && code <= 319)
                { saved.Valid = saved.Keyboard = true; saved.Key = (KeyCode)code; saved.Description = "KEYBOARD / " + saved.Key; }
                else if (parts.Length == 4 && parts[0] == "joy1" && Guid.TryParse(parts[1], out saved.Instance) && saved.Instance != Guid.Empty &&
                    Guid.TryParse(parts[2], out saved.Hardware) && int.TryParse(parts[3], out saved.Button) && saved.Button >= 0)
                    saved.Valid = true;
                if (DirectButtons.Count >= 128) DirectButtons.Clear();
                DirectButtons[binding] = saved;
            }
            if (!saved.Valid) return false;
            if (saved.Keyboard)
            {
                var input = UnityInput.Current;
                if (input == null) return false;
                down = input.GetKey(saved.Key); description = saved.Description; return true;
            }
            if (!ReInput.isReady) return false;
            var joystick = saved.Device;
            if (joystick == null || !joystick.isConnected || joystick.deviceInstanceGuid != saved.Instance || joystick.hardwareTypeGuid != saved.Hardware || saved.Index < 0 || saved.Index >= joystick.buttonCount ||
                saved.Index >= joystick.Buttons.Count || joystick.Buttons[saved.Index]?.elementIdentifier == null ||
                joystick.Buttons[saved.Index].elementIdentifier.id != saved.Button)
            {
                saved.Device = null;
                foreach (var candidate in ReInput.controllers.Joysticks)
                {
                    if (candidate == null || !candidate.isConnected || candidate.deviceInstanceGuid != saved.Instance || candidate.hardwareTypeGuid != saved.Hardware) continue;
                    int index = candidate.GetButtonIndexById(saved.Button);
                    if (index < 0 || index >= candidate.buttonCount || index >= candidate.Buttons.Count ||
                        candidate.Buttons[index]?.elementIdentifier == null || candidate.Buttons[index].elementIdentifier.id != saved.Button) continue;
                    saved.Device = candidate; saved.Index = index;
                    saved.Description = candidate.name + " / " + candidate.Buttons[index].elementIdentifier.name;
                    break;
                }
                DirectButtons[binding] = saved;
                joystick = saved.Device;
                if (joystick == null) return false;
            }
            down = joystick.GetButton(saved.Index); description = saved.Description; return true;
        }

        private void AdvanceWizard(IDictionary<string, float> snapshot)
        {
            if (wizard != null && (wizard.Phase == CalibrationPhase.Off || wizard.Phase == CalibrationPhase.Idle ||
                wizard.Phase == CalibrationPhase.Mil || wizard.Phase == CalibrationPhase.Maximum))
            {
                // The same lookup used during flight must resolve both axes before any sample is accepted.
                var verified = new Dictionary<string, float>();
                if (TryReadDirect(wizard.LeftKey, out var l, out _)) verified[wizard.LeftKey] = l;
                if (TryReadDirect(wizard.RightKey, out var r, out _)) verified[wizard.RightKey] = r;
                snapshot = verified;
            }
            if (wizard == null || !wizard.Next(snapshot)) return;
            // Preserve the old calibration until every new sample has passed validation.
            calibrated.Value = false;
            leftConfig.Write(wizard.LeftKey, wizard.LeftDetents);
            rightConfig.Write(wizard.RightKey, wizard.RightDetents);
            singleLever.Value = wizard.SingleLever;
            calibrated.Value = true;
            Config.Save();
            awaitIdleAfterCalibration = true;
            leftMilReported = rightMilReported = leftBurnerReported = rightBurnerReported = false;
            Logger.LogInfo("Both throttle axes calibrated; return both levers to idle before activation.");
        }

        private static Dictionary<string, float> SnapshotThrottleAxes()
        {
            var result = new Dictionary<string, float>();
            if (!ReInput.isReady) return result;
            foreach (var joystick in ReInput.controllers.Joysticks)
            {
                if (joystick == null || joystick.deviceInstanceGuid == Guid.Empty) continue;
                for (int i = 0; i < joystick.axisCount && i < joystick.Axes.Count; i++)
                {
                    var axis = joystick.Axes[i];
                    if (axis?.elementIdentifier == null) continue;
                    string key = DirectKey(joystick, axis.elementIdentifier.id);
                    result[key] = joystick.GetAxisRaw(i);
                }
            }
            return result;
        }

        private static string DirectKey(Joystick joystick, int axisId) =>
            "direct1|" + joystick.deviceInstanceGuid.ToString("D") + "|" +
            joystick.hardwareTypeGuid.ToString("D") + "|" + axisId;

        private static bool TryReadDirect(string key, out float value, out string description)
        {
            value = 0f;
            description = "not detected";
            if (key == null || !ReInput.isReady) return false;
            if (!DirectIds.TryGetValue(key, out var id))
            {
                var parts = key.Split('|');
                id.Valid = parts.Length == 4 && parts[0] == "direct1" &&
                    Guid.TryParse(parts[1], out id.Instance) && Guid.TryParse(parts[2], out id.Hardware) &&
                    int.TryParse(parts[3], out id.Axis) && id.Axis >= 0 && id.Instance != Guid.Empty;
                if (DirectIds.Count >= 128) DirectIds.Clear();
                DirectIds[key] = id;
            }
            if (!id.Valid) return false;
            var joystick = id.Device;
            if (joystick == null || !joystick.isConnected || joystick.deviceInstanceGuid != id.Instance || joystick.hardwareTypeGuid != id.Hardware || id.Index < 0 || id.Index >= joystick.axisCount ||
                id.Index >= joystick.Axes.Count || joystick.Axes[id.Index]?.elementIdentifier == null ||
                joystick.Axes[id.Index].elementIdentifier.id != id.Axis)
            {
                id.Device = null;
                foreach (var candidate in ReInput.controllers.Joysticks)
                {
                    if (candidate == null || !candidate.isConnected || candidate.deviceInstanceGuid != id.Instance || candidate.hardwareTypeGuid != id.Hardware) continue;
                    int index = candidate.GetAxisIndexById(id.Axis);
                    if (index < 0 || index >= candidate.axisCount || index >= candidate.Axes.Count ||
                        candidate.Axes[index]?.elementIdentifier == null || candidate.Axes[index].elementIdentifier.id != id.Axis) continue;
                    id.Device = candidate; id.Index = index;
                    id.Description = candidate.name + " / " + candidate.Axes[index].elementIdentifier.name;
                    break;
                }
                DirectIds[key] = id; joystick = id.Device;
                if (joystick == null) return false;
            }
            value = joystick.GetAxisRaw(id.Index); description = id.Description;
            return !float.IsNaN(value) && !float.IsInfinity(value) && value >= -1.001f && value <= 1.001f;
        }

        internal void OnPilot(PilotPlayerState state)
        {
            try
            {
                if (!hooksReady || !packEnabled.Value || !IsFlightSession(GameManager.gameState))
                {
                    EngineThrottlePatch.Clear(); cachedAircraft = null;
                    Active = false; LeverControl = false; ReverseActive = false; focusFrozen = false;
                    collectiveActive = false; collectiveFocusFrozen = false; collectiveAwaitZero = true;
                    awaitIdleAfterCalibration = true; airbrakeLatched = false;
                    ReportFlightStatus("inactive: disabled, missing hooks, or outside a flight session");
                    return;
                }
                var player = PlayerField.GetValue(state) as Player;
                // Player EnterState sets PilotBaseState.pilot, not its inherited aircraft field.
                var pilot = PilotField.GetValue(state) as Pilot;
                var aircraft = pilot != null ? pilot.aircraft : null;
                var inputs = InputsField.GetValue(state) as ControlInputs;
                // Other player states must not clear the current local aircraft's controls.
                if (player == null || aircraft == null || inputs == null || !GameManager.IsLocalAircraft(aircraft)) return;
                if (collectiveFocusFrozen && collectiveAircraft == aircraft && !windowOpen && aircraft.LocalSim &&
                    (!Application.isFocused || !focusPickup.Ready(Time.frameCount)))
                {
                    WriteCollective(state, inputs, collectiveHeld);
                    collectiveActive = true; collectiveFrame = Time.frameCount;
                    ReportFlightStatus("holding helicopter collective while focus returns");
                    return;
                }
                if (collectiveFocusFrozen && (collectiveAircraft != aircraft || windowOpen || !aircraft.LocalSim))
                    collectiveFocusFrozen = false;
                collectiveActive = false;
                if (focusFrozen && ActiveAircraft == aircraft && !windowOpen && aircraft.LocalSim)
                {
                    if (!Application.isFocused)
                    {
                        // The game's stock input code may update while unfocused; overwrite it
                        // with the last live command rather than accepting neutralised axes.
                        float held = (LeftPower + RightPower) * 0.5f;
                        SimThrottleField.SetValue(state, PlayerSettings.throttleUseNegative ? held * 2f - 1f : held);
                        inputs.throttle = held;
                        Active = true; LeverControl = true; ActiveFrame = Time.frameCount;
                        ReportFlightStatus("holding throttle while unfocused");
                        return;
                    }
                    // Keep the prior output in the engine hook until the physical levers are reacquired.
                }
                else focusFrozen = false;
                Active = false;
                LeverControl = false;
                ReverseActive = false;
                if (!ControlEligibility.CanOwn(IsFlightSession(GameManager.gameState), true, aircraft.LocalSim))
                {
                    airbrakeLatched = false;
                    awaitIdleAfterCalibration = true;
                    ReportFlightStatus("stock: local aircraft is not locally simulated");
                    return;
                }
                if (!reportedLocalPilot) { Logger.LogInfo("Throttle controls reached the local player aircraft."); reportedLocalPilot = true; }
                if (armingAircraft != aircraft)
                {
                    EngineThrottlePatch.Clear(); cachedAircraft = null;
                    armingAircraft = aircraft;
                    awaitIdleAfterCalibration = true;
                    collectiveAwaitZero = true;
                    airbrakeLatched = false;
                }

                if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.noms.splitthrottle"))
                {
                    message = "The separate SplitThrottle plugin is also loaded; this pack is disabled to prevent conflicting engine controls.";
                    ReportFlightStatus("stock: conflicting SplitThrottle plugin is loaded");
                    return;
                }

                if (collectiveClassifiedAircraft != aircraft)
                {
                    collectiveClassifiedAircraft = aircraft;
                    classifiedHelicopter = false; nextHelicopterScan = 0; helicopterScanLogged = false;
                }
                bool usesCollective = (bool)CollectiveField.GetValue(state);
                if (usesCollective && !classifiedHelicopter && Time.frameCount >= nextHelicopterScan)
                {
                    nextHelicopterScan = Time.frameCount + 120;
                    classifiedHelicopter = IsHelicopter(aircraft, out string evidence);
                    if (classifiedHelicopter || !helicopterScanLogged)
                        Logger.LogInfo("Collective aircraft " + (aircraft.definition != null ? aircraft.definition.jsonKey : aircraft.name) +
                            ": " + (classifiedHelicopter ? "helicopter detected via " + evidence : "rotor not yet detected; will retry"));
                    helicopterScanLogged = true;
                }
                if (usesCollective && classifiedHelicopter)
                {
                    if (windowOpen) { collectiveFocusFrozen = false; ReportFlightStatus("stock: calibration panel is open"); return; }
                    HandleCollective(state, inputs, aircraft, player);
                    return;
                }
                collectiveFocusFrozen = false;
                RefreshAircraft(aircraft);
                if (windowOpen) { ReportFlightStatus("stock: calibration panel is open"); return; }

                string leftKey, rightKey, leftPhysical, rightPhysical;
                if (leftConfig.Binding.Value.StartsWith("direct1|", StringComparison.Ordinal) &&
                    rightConfig.Binding.Value.StartsWith("direct1|", StringComparison.Ordinal))
                {
                    leftKey = leftPhysical = leftConfig.Binding.Value;
                    rightKey = rightPhysical = rightConfig.Binding.Value;
                    if ((leftPhysical == rightPhysical) != singleLever.Value ||
                        !TryReadDirect(leftKey, out leftRaw, out leftDescription) ||
                        !TryReadDirect(rightKey, out rightRaw, out rightDescription))
                    {
                        if (focusFrozen) { HoldFocusOutput(state, inputs); ReportFlightStatus("holding throttle: a calibrated axis is unavailable after focus return"); return; }
                        message = "A calibrated throttle axis is unavailable; game controls are in use."; airbrakeLatched = false; awaitIdleAfterCalibration = true; ReportFlightStatus("stock: a calibrated throttle axis is unavailable"); return;
                    }
                }
                else if (!TryCachedBinding(legacyLeft, player, LeftAction, out leftKey, out leftPhysical, out leftDescription) ||
                    !TryCachedBinding(legacyRight, player, RightAction, out rightKey, out rightPhysical, out rightDescription) || leftPhysical == rightPhysical)
                {
                    if (focusFrozen) { HoldFocusOutput(state, inputs); ReportFlightStatus("holding throttle: axis bindings are unavailable after focus return"); return; }
                    message = "Open the calibration panel to detect the two throttle axes.";
                    airbrakeLatched = false;
                    awaitIdleAfterCalibration = true;
                    ReportFlightStatus("stock: throttle axes are not bound or are duplicated");
                    return;
                }
                else
                {
                    leftRaw = player.GetAxisRaw(LeftAction);
                    rightRaw = player.GetAxisRaw(RightAction);
                }
                if (!calibrated.Value || leftConfig.Binding.Value != leftKey || rightConfig.Binding.Value != rightKey)
                {
                    message = "Calibration required or bindings changed. Open the calibration panel.";
                    airbrakeLatched = false;
                    ReportFlightStatus("stock: calibration is incomplete or the selected axis changed");
                    return;
                }
                if ((bool)CollectiveField.GetValue(state))
                {
                    message = "Helicopter collective is left to the game.";
                    airbrakeLatched = false;
                    ReportFlightStatus("stock: helicopter collective");
                    return;
                }
                if (PlayerSettings.throttleUseRelative)
                {
                    message = "Disable Use Throttle Relative Axis for this pack.";
                    airbrakeLatched = false;
                    ReportFlightStatus("stock: Use Throttle Relative Axis is enabled");
                    return;
                }

                if (!ThrottleCurve.TryMap(leftConfig.Read(), leftRaw, leftBurner, out var l) ||
                    !ThrottleCurve.TryMap(rightConfig.Read(), rightRaw, rightBurner, out var r))
                {
                    if (focusFrozen) { HoldFocusOutput(state, inputs); ReportFlightStatus("holding throttle: calibration is unavailable after focus return"); return; }
                    message = "Calibration data is invalid; game controls are in use.";
                    awaitIdleAfterCalibration = true;
                    ReportFlightStatus("stock: invalid calibration");
                    return;
                }

                if (focusFrozen)
                {
                    if (!focusPickup.Ready(Time.frameCount))
                    {
                        HoldFocusOutput(state, inputs);
                        ReportFlightStatus("holding throttle while input focus returns");
                        return;
                    }
                    focusFrozen = false;
                    Logger.LogInfo("Focus restored; throttle control resumed at current lever positions.");
                }

                if (awaitIdleAfterCalibration)
                {
                    if (!ThrottleCurve.BothAtIdle(l, r))
                    {
                        message = "Return both levers to IDLE to activate the new calibration.";
                        ReportFlightStatus("stock: waiting for both levers at idle");
                        return;
                    }
                    awaitIdleAfterCalibration = false;
                }

                if (ActiveAircraft != aircraft) airbrakeLatched = false;

                activePlayer = player;
                AirbrakeOpen = ControlPolicies.Airbrake(holdAirbrake.Value ? airbrakeHeld : airbrakeLatched, idleAirbrake.Value, l, r);
                ActiveAircraft = aircraft;
                Active = true;
                ActiveFrame = Time.frameCount;

                if (player.GetButton("Axis Modifier"))
                {
                    message = "Axis Modifier held: throttle and vectoring use the game's controls.";
                    ReportFlightStatus("stock throttle: Axis Modifier is held");
                    return;
                }

                bool withinSpeed = nativeReverseSpeedLimit > 0f && aircraft.speed < nativeReverseSpeedLimit;
                ReverseActive = reverseEnabled.Value && nativeReverse && withinSpeed && l.Reverse > 0f && r.Reverse > 0f;
                if (ReverseActive)
                {
                    LeftPower = l.Reverse * ReverseLimit(leftBurner);
                    RightPower = r.Reverse * ReverseLimit(rightBurner);
                    float leftPower = LeftPower, rightPower = RightPower;
                    ControlPolicies.ApplySplit(splitThrottles.Value, ref leftPower, ref rightPower);
                    LeftPower = leftPower; RightPower = rightPower;
                    message = "NATIVE REVERSE: both levers past the gate.";
                }
                else
                {
                    LeftPower = l.Forward;
                    RightPower = r.Forward;
                    float leftPower = LeftPower, rightPower = RightPower;
                    ControlPolicies.ApplySplit(splitThrottles.Value, ref leftPower, ref rightPower);
                    LeftPower = leftPower; RightPower = rightPower;
                    message = (l.Reverse > 0f || r.Reverse > 0f)
                        ? (nativeReverse ? "Reverse requires both levers past the gate and the native speed limit." : "This aircraft has no supported native reverser; gated lever stays at idle.")
                        : "Calibrated. Airbrake: " + (AirbrakeOpen ? "OPEN" : "closed");
                }
                float output = (LeftPower + RightPower) * 0.5f;
                SimThrottleField.SetValue(state, PlayerSettings.throttleUseNegative ? output * 2f - 1f : output);
                inputs.throttle = output;
                LeverControl = true;
                ReportFlightStatus(activeEngineStatus);
                if (!ReverseActive)
                {
                    ReportMilSample(true, leftRaw, leftConfig.Read(), LeftPower, leftBurner);
                    ReportMilSample(false, rightRaw, rightConfig.Read(), RightPower, rightBurner);
                    ReportBurnerCrossing(true, leftRaw, leftConfig.Read(), LeftPower, leftBurner);
                    ReportBurnerCrossing(false, rightRaw, rightConfig.Read(), RightPower, rightBurner);
                }
            }
            catch (Exception e)
            {
                Active = false;
                LeverControl = false;
                ReverseActive = false;
                EngineThrottlePatch.Clear(); cachedAircraft = null;
                collectiveActive = false; collectiveFocusFrozen = false; collectiveAwaitZero = true;
                message = "Control unavailable; game controls are in use. Check the BepInEx log.";
                ReportFlightStatus("stock: control update raised an exception");
                if (!reportedPilotException) { Logger.LogWarning("Throttle control skipped: " + e); reportedPilotException = true; }
            }
        }

        private static bool IsHelicopter(Aircraft aircraft, out string evidence)
        {
            evidence = null;
            if (aircraft == null) return false;
            // RotorShaft registers itself with the owning aircraft during Awake;
            // unlike a child-only lookup, this also handles detached hierarchies.
            if (aircraft.engines != null)
                foreach (var item in aircraft.engines)
                    if (item is RotorShaft shaft && shaft != null && shaft.aircraft == aircraft)
                    { evidence = "registered rotor shaft"; return true; }
            var prefab = aircraft.definition != null ? aircraft.definition.unitPrefab : null;
            if (prefab != null && prefab.GetComponentInChildren<RotorShaft>(true) != null)
            { evidence = "aircraft prefab"; return true; }
            if (aircraft.GetComponentInChildren<RotorShaft>(true) != null || aircraft.GetComponentInChildren<SwashRotor>(true) != null)
            { evidence = "live rotor hierarchy"; return true; }
            return false;
        }

        private void HandleCollective(PilotPlayerState state, ControlInputs inputs, Aircraft aircraft, Player player)
        {
            if (!collectiveCalibrated.Value || string.IsNullOrEmpty(collectiveBinding.Value))
            {
                collectiveAwaitZero = true;
                ReportFlightStatus("stock: helicopter calibration required");
                return;
            }
            if (!TryReadDirect(collectiveBinding.Value, out float raw, out _) ||
                !CollectiveCalibration.TryMap(collectiveZero.Value, collectiveMaximum.Value, raw, out float output))
            {
                if (collectiveFocusFrozen)
                {
                    WriteCollective(state, inputs, collectiveHeld);
                    collectiveActive = true; collectiveFrame = Time.frameCount;
                    ReportFlightStatus("holding helicopter collective: axis unavailable after focus return");
                }
                else { collectiveAwaitZero = true; ReportFlightStatus("stock: helicopter axis unavailable or invalid"); }
                return;
            }
            if (collectiveFocusFrozen)
            {
                collectiveFocusFrozen = false;
                Logger.LogInfo("Focus restored; helicopter collective resumed at current lever position.");
            }
            if (collectiveAwaitZero)
            {
                if (output > .02f) { ReportFlightStatus("stock: return helicopter collective to zero to activate"); return; }
                collectiveAwaitZero = false;
            }
            if (player.GetButton("Axis Modifier")) { ReportFlightStatus("stock: Axis Modifier is held"); return; }
            WriteCollective(state, inputs, output);
            collectiveHeld = output; collectiveAircraft = aircraft;
            collectiveActive = true; collectiveFrame = Time.frameCount;
            ReportFlightStatus("active: helicopter collective");
        }

        private static void WriteCollective(PilotPlayerState state, ControlInputs inputs, float output)
        {
            // The game applies these settings to simulatedThrottle before writing the
            // effective collective; retain its internal state in the corresponding domain.
            SimThrottleField.SetValue(state, CollectiveCalibration.NativeThrottle(output, PlayerSettings.invertCollective, PlayerSettings.throttleUseNegative));
            inputs.throttle = output;
        }

        private void HoldFocusOutput(PilotPlayerState state, ControlInputs inputs)
        {
            float held = (LeftPower + RightPower) * 0.5f;
            SimThrottleField.SetValue(state, PlayerSettings.throttleUseNegative ? held * 2f - 1f : held);
            inputs.throttle = held;
            Active = true; LeverControl = true; ReverseActive = focusReverse; ActiveFrame = Time.frameCount;
        }

        private bool reportedCalibrationException, reportedPilotException, reportedLocalPilot;

        internal void ReportHookFailure(string hook, Exception error)
        {
            if (reportedHookFailures.Add(hook)) Logger.LogWarning(hook + " fell back to the game's controls: " + error);
        }

        private void ReportFlightStatus(string status)
        {
            if (lastFlightStatus == status) return;
            lastFlightStatus = status;
            Logger.LogInfo("Throttle control status: " + status);
        }

        private static string FormatThreshold(float? value) => value.HasValue ? value.Value.ToString("F3") : "not detected";

        private void ReportMilSample(bool left, float raw, Detents detents, float command, float? threshold)
        {
            if ((left ? leftMilReported : rightMilReported) || Math.Abs(raw - detents.Mil) > Math.Abs(detents.Maximum - detents.Off) * 0.005f) return;
            if (left) leftMilReported = true; else rightMilReported = true;
            Logger.LogInfo("Near MIL detent " + (left ? "left" : "right") + ": raw=" + raw.ToString("F3") +
                ", engine command=" + command.ToString("F3") + ", burner start=" + FormatThreshold(threshold) + ".");
        }

        private void ReportBurnerCrossing(bool left, float raw, Detents detents, float command, float? threshold)
        {
            if ((left ? leftBurnerReported : rightBurnerReported) || !threshold.HasValue || command <= threshold.Value) return;
            if (left) leftBurnerReported = true; else rightBurnerReported = true;
            Logger.LogInfo("Burner threshold crossed " + (left ? "left" : "right") + ": raw=" + raw.ToString("F3") +
                ", calibrated MIL=" + detents.Mil.ToString("F3") + ", engine command=" + command.ToString("F3") +
                ", burner start=" + threshold.Value.ToString("F3") + ". Flame visibility also depends on engine RPM and smoothing.");
        }

        internal void ReportEngineOverride(Component engine, float x)
        {
            if (reportedEngines.Add(engine))
                Logger.LogInfo("Throttle override reached engine " + engine.name + ": " +
                    (x < -0.1f ? "left" : x > 0.1f ? "right" : "centre/shared") + ".");
        }

        private float ReverseLimit(float? burner)
        {
            float requested = reversePower.Value;
            if (float.IsNaN(requested) || float.IsInfinity(requested)) return 0f;
            requested = Math.Max(0f, Math.Min(0.90f, requested));
            return burner.HasValue ? Math.Min(requested, Math.Max(0f, burner.Value - 0.01f)) : requested;
        }

        private static bool TryCachedBinding(LegacyBindingCache cache, Player player, string action, out string key, out string physicalKey, out string description)
        {
            float now = Time.unscaledTime;
            if (cache.Player != player || now >= cache.NextScan || (cache.Controller != null && !cache.Controller.isConnected))
            {
                cache.Player = player; cache.NextScan = now + .5f;
                cache.Valid = TryBinding(player, action, out cache.Key, out cache.Physical, out cache.Description, out cache.Controller);
            }
            key = cache.Key; physicalKey = cache.Physical; description = cache.Description;
            return cache.Valid;
        }

        private static bool TryBinding(Player player, string action, out string key, out string physicalKey, out string description, out Controller resolvedController)
        {
            key = null;
            physicalKey = null;
            resolvedController = null;
            description = "not bound";
            foreach (var map in player.controllers.maps.GetAllMaps(ControllerType.Joystick))
            {
                if (!map.enabled || map.controller == null) continue;
                foreach (var element in map.GetElementMapsWithAction(action))
                {
                    if (!element.enabled || element.elementType != ControllerElementType.Axis || element.axisRange != AxisRange.Full) continue;
                    var controller = map.controller;
                    string physical = controller.deviceInstanceGuid + "|" + controller.hardwareTypeGuid + "|" +
                        controller.hardwareIdentifier + "|" + element.elementIdentifierId;
                    string candidate = physical + "|" + element.invert;
                    if (key != null) { description = "more than one active axis binding"; return false; }
                    key = candidate;
                    physicalKey = physical;
                    resolvedController = controller;
                    description = controller.name + " / " + element.elementIdentifierName;
                }
            }
            return key != null;
        }

        private void RefreshAircraft(Aircraft aircraft)
        {
            int engineCount = aircraft.engines != null ? aircraft.engines.Count : 0;
            int stateCount = aircraft.engineStates != null ? aircraft.engineStates.Count : 0;
            if (cachedAircraft == aircraft && Time.frameCount - lastAircraftScan < 120 &&
                cachedEngineCount == engineCount && cachedEngineStateCount == stateCount) return;
            if (cachedAircraft != aircraft)
            {
                reportedEngines.Clear();
                leftMilReported = rightMilReported = leftBurnerReported = rightBurnerReported = false;
            }
            cachedAircraft = aircraft;
            lastAircraftScan = Time.frameCount;
            cachedEngineCount = engineCount; cachedEngineStateCount = stateCount;
            leftBurner = rightBurner = null;
            nativeReverse = false;
            nativeReverseSpeedLimit = 0f;
            reverseEngines.Clear();
            var children = new List<Component>();
            children.AddRange(aircraft.GetComponentsInChildren<Turbojet>(true));
            children.AddRange(aircraft.GetComponentsInChildren<Turbofan>(true));
            children.AddRange(aircraft.GetComponentsInChildren<ConstantSpeedProp>(true));
            children.AddRange(aircraft.GetComponentsInChildren<PropFan>(true));
            var engines = OwnedDiscovery.Merge(NativeReflection.RegisteredEngines(aircraft), children,
                engine => NativeReflection.BelongsTo(aircraft, engine));
            EngineThrottlePatch.Rebuild(aircraft, engines);
            var nozzles = new HashSet<JetNozzle>();
            foreach (var engine in engines)
            {
                var linked = NativeReflection.EngineNozzles(engine);
                if (linked != null) foreach (var nozzle in linked)
                    if (NativeReflection.NozzleBelongsTo(aircraft, nozzle)) nozzles.Add(nozzle);
            }
            foreach (var nozzle in aircraft.GetComponentsInChildren<JetNozzle>(true))
                if (NativeReflection.NozzleBelongsTo(aircraft, nozzle)) nozzles.Add(nozzle);
            foreach (var nozzle in nozzles)
            {
                float? start = NativeReflection.AfterburnerStart(nozzle);
                if (!start.HasValue) continue;
                float x = NativeReflection.LocalNozzleX(aircraft, nozzle);
                if (float.IsNaN(x) || float.IsInfinity(x)) continue;
                if (x <= 0.1f) leftBurner = Minimum(leftBurner, start.Value);
                if (x >= -0.1f) rightBurner = Minimum(rightBurner, start.Value);
            }
            foreach (var duct in aircraft.GetComponentsInChildren<DuctedThrustSystem>(true))
            {
                if (NativeReflection.SupportsReverse(duct, out float speed) && NativeReflection.AddReverseEngines(duct, reverseEngines))
                {
                    nativeReverse = true;
                    nativeReverseSpeedLimit = nativeReverseSpeedLimit <= 0f ? speed : Math.Min(nativeReverseSpeedLimit, speed);
                }
            }
            int left = 0, right = 0, center = 0;
            foreach (var engine in engines)
            {
                float x = NativeReflection.EngineX(aircraft, engine);
                if (float.IsNaN(x) || float.IsInfinity(x)) continue;
                if (x < -0.1f) left++; else if (x > 0.1f) right++; else center++;
            }
            engineTopology = "Engines L/R/centre: " + left + "/" + right + "/" + center + " (centre/shared uses average)";
            activeEngineStatus = "active; afterburner starts L/R " + FormatThreshold(leftBurner) + "/" + FormatThreshold(rightBurner) + "; " + engineTopology;
        }

        private static float? Minimum(float? a, float b) => !a.HasValue ? b : Math.Min(a.Value, b);

        private void OnGUI()
        {
            if (!windowOpen)
            {
                if (!hooksReady && IsFlightSession(GameManager.gameState))
                    GUI.Box(new Rect(24, Screen.height - 72, 360, 38), "THROTTLE PACK INACTIVE — CHECK BEPINEX LOG");
                else if (classifiedHelicopter && collectiveClassifiedAircraft == armingAircraft && IsFlightSession(GameManager.gameState))
                {
                    if (!collectiveCalibrated.Value)
                        GUI.Box(new Rect(24, Screen.height - 72, 330, 38), "HELICOPTER: OPEN F9 TO CALIBRATE");
                    else if (collectiveFocusFrozen && Application.isFocused)
                        GUI.Box(new Rect(24, Screen.height - 72, 370, 38), "WAITING FOR VALID COLLECTIVE INPUT");
                    else if (collectiveAwaitZero)
                        GUI.Box(new Rect(24, Screen.height - 72, 330, 38), "RETURN COLLECTIVE TO ZERO");
                }
                else if (calibrated.Value && PlayerSettings.throttleUseRelative && IsFlightSession(GameManager.gameState))
                    GUI.Box(new Rect(24, Screen.height - 72, 360, 38), "DISABLE USE THROTTLE RELATIVE AXIS");
                else if (focusFrozen && Application.isFocused && IsFlightSession(GameManager.gameState))
                    GUI.Box(new Rect(24, Screen.height - 72, 370, 38), "WAITING FOR VALID THROTTLE INPUT");
                else if (awaitIdleAfterCalibration && calibrated.Value && IsFlightSession(GameManager.gameState))
                    GUI.Box(new Rect(24, Screen.height - 72, 250, 38), "RETURN BOTH LEVERS TO IDLE");
                else if (Active && Time.frameCount - ActiveFrame <= 1 && (AirbrakeOpen || ReverseActive))
                    GUI.Box(new Rect(24, Screen.height - 72, 190, 38), ReverseActive && AirbrakeOpen ? "REVERSE  |  AIRBRAKE" : ReverseActive ? "REVERSE" : "AIRBRAKE");
                return;
            }
            // The interactive panel is a private uGUI canvas using native MFD assets.
        }

        private sealed class LeverConfig
        {
            internal readonly ConfigEntry<string> Binding;
            private readonly ConfigEntry<float> off, idle, mil, maximum;
            private readonly ConfigEntry<bool> noReverseGate;
            internal LeverConfig(ConfigFile config, string section)
            {
                Binding = config.Bind(section, "Binding", "", "Captured controller and axis identity.");
                off = config.Bind(section, "Off", 0f, "Captured gated endpoint.");
                idle = config.Bind(section, "Idle", 0f, "Captured normal idle stop.");
                mil = config.Bind(section, "Mil", 0f, "Captured afterburner detent.");
                maximum = config.Bind(section, "Maximum", 0f, "Captured full-forward endpoint.");
                noReverseGate = config.Bind(section, "NoReverseGate", false, "Calibration has no analog range below idle.");
            }
            internal Detents Read() => new Detents { Off = off.Value, Idle = idle.Value, Mil = mil.Value, Maximum = maximum.Value, NoReverseGate = noReverseGate.Value };
            internal void Write(string binding, Detents d)
            {
                Binding.Value = binding; off.Value = d.Off; idle.Value = d.Idle; mil.Value = d.Mil; maximum.Value = d.Maximum; noReverseGate.Value = d.NoReverseGate;
            }
        }
    }
}
