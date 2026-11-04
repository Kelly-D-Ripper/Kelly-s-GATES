using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;

internal static class Program
{
    private static string root, game;
    private static void Main(string[] args)
    {
        if (args.Length < 2 || args.Length > 3) throw new ArgumentException("Pass the project directory and game directory; optionally --try-runtime.");
        root = Path.GetFullPath(args[0]); game = Path.GetFullPath(args[1]);
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        Inspect(args.Length == 3 && args[2] == "--try-runtime");
    }

    private static Assembly Resolve(object sender, ResolveEventArgs args)
    {
        var name = new AssemblyName(args.Name).Name + ".dll";
        foreach (var directory in new[] { Path.Combine(game, "BepInEx", "core"), Path.Combine(game, "NuclearOption_Data", "Managed"), Path.Combine(root, "lib"), Path.Combine(root, "bin", "Release", "net472") })
        {
            string file = Path.Combine(directory, name);
            if (File.Exists(file)) return Assembly.LoadFrom(file);
        }
        return null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Inspect(bool tryRuntime)
    {
        var gameAssembly = Assembly.LoadFrom(Path.Combine(game, "NuclearOption_Data", "Managed", "Assembly-CSharp.dll"));
        var plugin = Assembly.LoadFrom(Path.Combine(root, "bin", "Release", "net472", "UrsaMinorThrottle.dll"));
        var pilotState = gameAssembly.GetType("PilotPlayerState", true);
        var baseState = gameAssembly.GetType("PilotBaseState", true);
        var pilotType = gameAssembly.GetType("Pilot", true);
        var pilotField = AccessTools.Field(baseState, "pilot");
        var inheritedAircraft = AccessTools.Field(baseState, "aircraft");
        var pilotAircraft = AccessTools.Field(pilotType, "aircraft");
        var enter = PatchProcessor.GetOriginalInstructions(AccessTools.Method(pilotState, "EnterState"), (ILGenerator)null);
        if (!enter.Any(c => c.opcode == OpCodes.Stfld && Equals(c.operand, pilotField)) ||
            enter.Any(c => c.opcode == OpCodes.Stfld && Equals(c.operand, inheritedAircraft)) || pilotAircraft == null)
            throw new Exception("Player aircraft lookup assumptions changed; inspect the game before installing.");
        var pluginType = plugin.GetType("UrsaMinorThrottle.Plugin", true);
        var lookup = pluginType.GetField("PilotField", BindingFlags.Static | BindingFlags.NonPublic);
        if (lookup == null || !Equals(lookup.GetValue(null), pilotField) ||
            pluginType.GetField("AircraftField", BindingFlags.Static | BindingFlags.NonPublic) != null ||
            pluginType.GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic) == null)
            throw new Exception("Player pilot lookup or independent shortcut update is missing.");
        var gameState = gameAssembly.GetType("GameState", true);
        var sessionGate = pluginType.GetMethod("IsFlightSession", BindingFlags.Static | BindingFlags.NonPublic);
        foreach (var state in Enum.GetValues(gameState))
        {
            bool expected = state.ToString() == "SinglePlayer" || state.ToString() == "Multiplayer";
            if ((bool)sessionGate.Invoke(null, new[] { state }) != expected)
                throw new Exception("Unexpected flight-session eligibility for " + state);
        }
        var unit = gameAssembly.GetType("Unit", true);
        var localSim = AccessTools.PropertyGetter(unit, "LocalSim");
        var localPlayer = AccessTools.Method(gameAssembly.GetType("GameManager", true), "IsLocalAircraft", new[] { gameAssembly.GetType("Aircraft", true) });
        var controlGuard = pluginType.GetMethod("ControlsAircraft", BindingFlags.Instance | BindingFlags.NonPublic);
        var guardIl = PatchProcessor.GetOriginalInstructions(controlGuard, (ILGenerator)null);
        if (localSim == null || localPlayer == null ||
            !guardIl.Any(c => Equals(c.operand, localSim)) || !guardIl.Any(c => Equals(c.operand, localPlayer)) ||
            !guardIl.Any(c => c.operand is MethodInfo m && m.Name == "CanApply" && m.DeclaringType?.Name == "ControlEligibility"))
            throw new Exception("Local ownership, simulation, or freshness guard is missing.");
        var focus = pluginType.GetMethod("OnApplicationFocus", BindingFlags.Instance | BindingFlags.NonPublic);
        var buttonPoll = pluginType.GetMethod("PollAirbrakeButton", BindingFlags.Instance | BindingFlags.NonPublic);
        var panelType = plugin.GetType("UrsaMinorThrottle.NativeThrottlePanel", true);
        if (focus == null || buttonPoll == null || panelType.GetMethod("Build", BindingFlags.Instance | BindingFlags.NonPublic) == null ||
            pluginType.GetMethod("HoldFocusOutput", BindingFlags.Instance | BindingFlags.NonPublic) == null)
            throw new Exception("Focus hold, airbrake polling or native options screen is missing.");
        var cursorFlags = gameAssembly.GetType("CursorFlags", true);
        var reserved = Convert.ToInt32(pluginType.GetField("PanelCursor", BindingFlags.Static | BindingFlags.NonPublic).GetRawConstantValue());
        foreach (var flag in Enum.GetValues(cursorFlags)) if ((Convert.ToInt32(flag) & reserved) != 0)
            throw new Exception("Throttle cursor flag collides with a game cursor flag.");
        if (reserved == (1 << 28) || reserved == (1 << 29)) throw new Exception("Throttle cursor flag collides with TALON or ReplayMod.");
        var cursorManager = gameAssembly.GetType("CursorManager", true);
        var setCursorFlag = AccessTools.Method(cursorManager, "SetFlag");
        foreach (var name in new[] { "ToggleCalibration", "ClosePanel", "LateUpdate" })
        {
            var il = PatchProcessor.GetOriginalInstructions(pluginType.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic), (ILGenerator)null);
            if (!il.Any(c => Equals(c.operand, setCursorFlag))) throw new Exception(name + " does not manage the throttle cursor flag.");
        }
        var block = pluginType.GetMethod("BlocksLocalPilotControls", BindingFlags.Instance | BindingFlags.NonPublic);
        var blockIl = PatchProcessor.GetOriginalInstructions(block, (ILGenerator)null);
        if (!blockIl.Any(c => Equals(c.operand, localPlayer)) || !blockIl.Any(c => c.operand is MethodInfo m && m.Name == "IsFlightSession"))
            throw new Exception("Setup input suppression is not restricted to the local flight pilot.");
        var setupPatch = plugin.GetType("UrsaMinorThrottle.SetupPilotControlsPatch", true);
        var setupIl = PatchProcessor.GetOriginalInstructions(setupPatch.GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic), (ILGenerator)null);
        if (!setupIl.Any(c => Equals(c.operand, block)) || AccessTools.Method(pilotState, "PlayerControls") == null)
            throw new Exception("Setup input suppression hook is missing.");
        var assetIl = PatchProcessor.GetOriginalInstructions(panelType.GetMethod("FindStockAssets", BindingFlags.Instance | BindingFlags.NonPublic), (ILGenerator)null);
        if (!assetIl.Any(c => c.operand is string text && text == "MAP OPTIONS"))
            throw new Exception("Stock MFD presentation sampling is missing.");
        var pollIl = PatchProcessor.GetOriginalInstructions(buttonPoll, (ILGenerator)null);
        var mappedIl = PatchProcessor.GetOriginalInstructions(pluginType.GetMethod("ReadMappedAirbrakeButton", BindingFlags.Instance | BindingFlags.NonPublic), (ILGenerator)null);
        if (!mappedIl.Any(c => c.operand is MethodInfo m && m.Name == "GetButton") ||
            !mappedIl.Any(c => c.operand is MethodInfo m && m.Name == "GetButtonById") ||
            !mappedIl.Any(c => c.operand is MethodInfo m && m.Name == "get_hasModifiers"))
            throw new Exception("Airbrake action or safe mapped-controller fallback is missing.");
        if (!mappedIl.Any(c => c.operand is MethodInfo m && m.Name == "ReadAirbrakeMap" && m.DeclaringType?.Name == "ControlPolicies") ||
            !pollIl.Any(c => Equals(c.operand, controlGuard)) ||
            !mappedIl.Any(c => c.operand is MethodInfo m && m.Name == "GetElementMapsWithAction") ||
            !pollIl.Any(c => c.operand is MethodInfo m && m.Name == "TryReadAirbrakeBinding"))
            throw new Exception("Bound airbrake fallback is missing its local-aircraft guard or map policy.");
        if (!guardIl.Any(c => c.operand is MethodInfo m && m.Name == "Fresh" && m.DeclaringType?.Name == "ControlPolicies"))
            throw new Exception("Focus hold does not integrate with the freshness guard.");
        var onPilot = PatchProcessor.GetOriginalInstructions(pluginType.GetMethod("OnPilot", BindingFlags.Instance | BindingFlags.NonPublic), (ILGenerator)null);
        if (!onPilot.Any(c => Equals(c.operand, localSim)) || !onPilot.Any(c => Equals(c.operand, localPlayer)) ||
            !onPilot.Any(c => c.operand is MethodInfo m && m.Name == "CanOwn" && m.DeclaringType?.Name == "ControlEligibility"))
            throw new Exception("Pilot control can bypass local aircraft ownership.");
        int localCheck = onPilot.FindIndex(c => Equals(c.operand, localPlayer));
        if (localCheck < 0 || localCheck + 2 >= onPilot.Count ||
            (onPilot[localCheck + 1].opcode != OpCodes.Brtrue && onPilot[localCheck + 1].opcode != OpCodes.Brtrue_S) ||
            (onPilot[localCheck + 2].opcode != OpCodes.Leave && onPilot[localCheck + 2].opcode != OpCodes.Leave_S))
            throw new Exception("A remote pilot state must leave before local active controls are reset.");
        foreach (var patchMethod in new[] { ("AirbrakePatch", "ShouldOpen"), ("NativeReverseScopePatch", "Prefix"), ("EngineThrottlePatch", "Prefix") })
        {
            var patch = plugin.GetType("UrsaMinorThrottle." + patchMethod.Item1, true);
            var il = PatchProcessor.GetOriginalInstructions(patch.GetMethod(patchMethod.Item2, BindingFlags.Static | BindingFlags.NonPublic), (ILGenerator)null);
            if (!il.Any(c => Equals(c.operand, controlGuard))) throw new Exception(patchMethod.Item1 + " does not use the shared local-aircraft guard.");
        }
        var aircraftType = gameAssembly.GetType("Aircraft", true);
        var statesField = AccessTools.Field(aircraftType, "engineStates");
        var enginesField = AccessTools.Field(aircraftType, "engines");
        if (statesField == null || enginesField == null || AccessTools.Field(gameAssembly.GetType("JetNozzle", true), "aircraft") == null)
            throw new Exception("Native engine or nozzle ownership registry is unavailable.");
        foreach (var spec in new[] { ("Turbojet", statesField), ("Turbofan", statesField), ("ConstantSpeedProp", statesField), ("PropFan", enginesField) })
        {
            var t = gameAssembly.GetType(spec.Item1, true);
            var awake = PatchProcessor.GetOriginalInstructions(AccessTools.Method(t, "Awake"), (ILGenerator)null);
            if (!awake.Any(c => Equals(c.operand, spec.Item2)) || AccessTools.Field(t, "aircraft") == null)
                throw new Exception(spec.Item1 + " no longer registers an owned engine.");
        }
        var nativeReflection = plugin.GetType("UrsaMinorThrottle.NativeReflection", true);
        var refreshIl = PatchProcessor.GetOriginalInstructions(pluginType.GetMethod("RefreshAircraft", BindingFlags.Instance | BindingFlags.NonPublic), (ILGenerator)null);
        foreach (var name in new[] { "RegisteredEngines", "BelongsTo", "NozzleBelongsTo", "EngineNozzles", "AfterburnerStart" })
        {
            // The ownership test is in a compiler-generated predicate; validate it separately below.
            if (name == "BelongsTo") continue;
            if (!refreshIl.Any(c => c.operand is MethodInfo m && m.Name == name && m.DeclaringType == nativeReflection))
                throw new Exception("Engine discovery no longer uses " + name);
        }
        if (AccessTools.Field(gameAssembly.GetType("Turbojet", true), "nozzles") == null ||
            AccessTools.Field(gameAssembly.GetType("Turbofan", true), "nozzles") == null)
            throw new Exception("Jet nozzle links are unavailable.");
        var checkSim = PatchProcessor.GetOriginalInstructions(AccessTools.Method(aircraftType, "CheckIfLocalSim"), (ILGenerator)null);
        var setSim = PatchProcessor.GetOriginalInstructions(AccessTools.Method(aircraftType, "SetLocalSim"), (ILGenerator)null);
        if (!checkSim.Any(c => c.operand is MethodInfo m && m.Name == "get_IsLocalPlayer") ||
            !setSim.Any(c => c.operand is MethodInfo m && m.Name == "SetComplexPhysics"))
            throw new Exception("Game local-player simulation assumptions changed.");
        var networkAircraft = gameAssembly.GetType("NuclearOption.NetworkTransforms.AircraftNetworkTransform", true);
        var createSnapshot = PatchProcessor.GetOriginalInstructions(AccessTools.Method(networkAircraft, "CreateSnapshot"), (ILGenerator)null);
        var clientAuth = PatchProcessor.GetOriginalInstructions(AccessTools.Method(networkAircraft, "ClientAuthUpdate"), (ILGenerator)null);
        if (!createSnapshot.Any(c => c.operand is ConstructorInfo m && m.DeclaringType?.Name == "CompressedInputs") ||
            !createSnapshot.Any(c => c.operand is MethodInfo m && m.Name == "GetInputs" && m.DeclaringType?.Name == "Aircraft") ||
            !clientAuth.Any(c => c.operand is MethodInfo m && m.Name == "get_HasAuthority") ||
            !clientAuth.Any(c => c.operand is MethodInfo m && m.Name == "CmdClientAuth"))
            throw new Exception("Game client-authoritative snapshot assumptions changed.");
        pluginType.GetMethod("ValidateGameHooks", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
        var advanceWizard = pluginType.GetMethod("AdvanceWizard", BindingFlags.Instance | BindingFlags.NonPublic);
        var advanceIl = PatchProcessor.GetOriginalInstructions(advanceWizard, (ILGenerator)null);
        if (!advanceIl.Any(c => c.operand is MethodInfo called && called.DeclaringType?.Name == "CalibrationWizard" && called.Name == "Next"))
            throw new Exception("The UI does not invoke the tested calibration state machine.");
        var rewired = Assembly.LoadFrom(Path.Combine(game, "NuclearOption_Data", "Managed", "Rewired_Core.dll"));
        var manager = rewired.GetType("Rewired.InputManager_Base", true);
        var userData = rewired.GetType("Rewired.Data.UserData", true);
        if (AccessTools.Method(manager, "Awake") == null || AccessTools.Field(manager, "_userData") == null ||
            new[] { "actions", "actionCategories", "actionCategoryMap" }.Any(n => AccessTools.Field(userData, n) == null))
            throw new Exception("The input framework's Rewired registration contract changed.");
        var airbrake = gameAssembly.GetType("Airbrake", true);
        var brakeStart = PatchProcessor.GetOriginalInstructions(AccessTools.Method(airbrake, "Start"), (ILGenerator)null);
        if (!brakeStart.Any(c => c.opcode == OpCodes.Stfld && Equals(c.operand, AccessTools.Field(airbrake, "attachedAircraft"))) ||
            !brakeStart.Any(c => c.opcode == OpCodes.Stfld && Equals(c.operand, AccessTools.Field(airbrake, "controlInputs"))))
            throw new Exception("Airbrake aircraft or control input initialization changed.");
        var method = AccessTools.Method(airbrake, "Update");
        var original = PatchProcessor.GetOriginalInstructions(method, (ILGenerator)null);
        var transpiler = plugin.GetType("UrsaMinorThrottle.AirbrakePatch", true).GetMethod("Transpiler", BindingFlags.Static | BindingFlags.NonPublic);
        var transformed = ((IEnumerable<CodeInstruction>)transpiler.Invoke(null, new object[] { original })).ToList();
        int calls = transformed.Count(c => c.opcode == OpCodes.Call && c.operand is MethodInfo m && m.Name == "ShouldOpen");
        if (calls != 1) throw new Exception("Expected exactly one independent-airbrake decision.");
        int callIndex = transformed.FindIndex(c => c.opcode == OpCodes.Call && c.operand is MethodInfo m && m.Name == "ShouldOpen");
        if (transformed[callIndex - 1].opcode != OpCodes.Ldarg_0 ||
            (transformed[callIndex + 1].opcode != OpCodes.Brtrue && transformed[callIndex + 1].opcode != OpCodes.Brtrue_S))
            throw new Exception("Airbrake decision has unexpected stack/branch shape.");
        var changed = PatchProcessor.GetOriginalInstructions(method, (ILGenerator)null);
        int comparison = changed.FindIndex(c => c.opcode == OpCodes.Ldc_R4 && c.operand is float f && f == 0f);
        if (comparison < 0) throw new Exception("No stock comparison to mutate");
        changed[comparison].operand = 0.5f;
        try
        {
            transpiler.Invoke(null, new object[] { changed });
            throw new Exception("Changed airbrake IL should have been refused.");
        }
        catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException) { }
        var wrongOwner = PatchProcessor.GetOriginalInstructions(method, (ILGenerator)null);
        int controlField = wrongOwner.FindIndex(c => c.opcode == OpCodes.Ldfld && Equals(c.operand, AccessTools.Field(airbrake, "controlInputs")));
        if (controlField < 0) throw new Exception("No airbrake control input field to mutate");
        wrongOwner[controlField].operand = AccessTools.Field(gameAssembly.GetType("ControlInputs", true), "throttle");
        try
        {
            transpiler.Invoke(null, new object[] { wrongOwner });
            throw new Exception("Changed airbrake control source should have been refused.");
        }
        catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException) { }

        foreach (var name in new[] { "Turbojet", "Turbofan", "ConstantSpeedProp", "PropFan" })
        {
            var type = gameAssembly.GetType(name, true);
            if (AccessTools.Method(type, "FixedUpdate") == null || AccessTools.Field(type, "aircraft") == null ||
                AccessTools.Field(type, name == "PropFan" ? "inputs" : "controlInputs") == null)
                throw new Exception("Missing engine hook on " + name);
            var engineIl = PatchProcessor.GetOriginalInstructions(AccessTools.Method(type, "FixedUpdate"), (ILGenerator)null);
            if (!engineIl.Any(c => c.opcode == OpCodes.Ldfld && Equals(c.operand, AccessTools.Field(gameAssembly.GetType("ControlInputs", true), "throttle"))))
                throw new Exception("Engine no longer reads the shared throttle: " + name);
        }
        var duct = gameAssembly.GetType("DuctedThrustSystem", true);
        foreach (var name in new[] { "aircraft", "inputs", "swivelLimit", "maxReverseSpeed", "turbojets", "turbofans" })
            if (AccessTools.Field(duct, name) == null) throw new Exception("Missing native vectoring field " + name);
        var swivel = AccessTools.Method(duct, "Swivel");
        if (AccessTools.Method(duct, "ChooseOperatingMode") == null || swivel == null) throw new Exception("Missing native vectoring method");
        var swivelIl = PatchProcessor.GetOriginalInstructions(swivel, (ILGenerator)null);
        var vectorAxis = AccessTools.Field(gameAssembly.GetType("ControlInputs", true), "customAxis1");
        if (!swivelIl.Any(c => c.opcode == OpCodes.Ldfld && Equals(c.operand, vectorAxis)))
            throw new Exception("Native swivel no longer reads the vectoring input; refusing the hook.");

        var inputsType = gameAssembly.GetType("ControlInputs", true);
        object inputs = Activator.CreateInstance(inputsType);
        var throttle = AccessTools.Field(inputsType, "throttle");
        var settingsType = gameAssembly.GetType("PlayerSettings", true);
        var invert = AccessTools.Field(settingsType, "invertCollective");
        var negative = AccessTools.Field(settingsType, "throttleUseNegative");
        var nativeThrottle = AccessTools.Field(pilotState, "simulatedThrottle");
        var writer = pluginType.GetMethod("WriteCollective", BindingFlags.Static | BindingFlags.NonPublic);
        if (invert == null || negative == null || nativeThrottle == null || writer == null || gameAssembly.GetType("SwashRotor", false) == null)
            throw new Exception("Helicopter collective contract changed.");
        var writerIl = PatchProcessor.GetOriginalInstructions(writer, (ILGenerator)null);
        if (!writerIl.Any(c => Equals(c.operand, invert)) || !writerIl.Any(c => Equals(c.operand, negative)) ||
            !writerIl.Any(c => c.operand is MethodInfo m && m.Name == "NativeThrottle") ||
            !writerIl.Any(c => Equals(c.operand, throttle)))
            throw new Exception("Helicopter collective writer no longer uses the expected fields and mapping.");
        Console.WriteLine("PASS: helicopter collective native control field and mapping contract");
        throttle.SetValue(inputs, 0.2f);
        var enginePatch = plugin.GetType("UrsaMinorThrottle.EngineThrottlePatch", true);
        var enginePrefix = PatchProcessor.GetOriginalInstructions(enginePatch.GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic), (ILGenerator)null);
        if (!enginePrefix.Any(c => c.operand is MethodInfo m && m.Name == "ReportEngineOverride"))
            throw new Exception("The engine takeover diagnostic is missing from the real throttle hook.");
        int membership = enginePrefix.FindIndex(c => c.operand is MethodInfo m && m.Name == "TryGetValue");
        int localGuard = enginePrefix.FindIndex(c => c.operand is MethodInfo m && m.Name == "ControlsAircraft");
        if (membership < 0 || localGuard < membership || enginePrefix.Any(c => c.operand is MethodInfo m && m.DeclaringType != null &&
            typeof(FieldInfo).IsAssignableFrom(m.DeclaringType) && m.Name == "GetValue"))
            throw new Exception("Engine hook must reject uncached engines before ownership checks and field reflection.");
        var classify = pluginType.GetMethod("IsHelicopter", BindingFlags.Static | BindingFlags.NonPublic);
        if (classify == null || AccessTools.Field(gameAssembly.GetType("RotorShaft", true), "aircraft") == null ||
            AccessTools.Field(gameAssembly.GetType("UnitDefinition", true), "unitPrefab") == null)
            throw new Exception("Registered rotor/prefab classification contract changed.");
        var classifyIl = PatchProcessor.GetOriginalInstructions(classify, (ILGenerator)null);
        var pilotIl = PatchProcessor.GetOriginalInstructions(pluginType.GetMethod("OnPilot", BindingFlags.Instance | BindingFlags.NonPublic), (ILGenerator)null);
        if (!classifyIl.Any(c => c.operand is FieldInfo f && f.Name == "engines") ||
            !classifyIl.Any(c => c.operand is FieldInfo f && f.Name == "unitPrefab") ||
            !pilotIl.Any(c => c.operand is FieldInfo f && f.Name == "nextHelicopterScan") ||
            !pilotIl.Any(c => Equals(c.operand, classify)))
            throw new Exception("Helicopter classification must use registered rotors, prefab fallback, and retry.");
        foreach (var pair in new[] { ("TryReadDirect", "GetAxisRaw"), ("TryReadAirbrakeBinding", "GetButton") })
        {
            var read = pluginType.GetMethod(pair.Item1, BindingFlags.Static | BindingFlags.NonPublic);
            var il = PatchProcessor.GetOriginalInstructions(read, (ILGenerator)null);
            if (!il.Any(c => c.operand is MethodInfo m && m.Name == pair.Item2) ||
                !il.Any(c => c.operand is MethodInfo m && m.Name == "get_isConnected"))
                throw new Exception(pair.Item1 + " must sample live input and reject disconnected devices.");
        }
        Console.WriteLine("PASS: AI engine cache guard, rotor fallback/retry and live input/reconnect contracts");
        var savedType = enginePatch.GetNestedType("SavedInput", BindingFlags.NonPublic);
        object saved = Activator.CreateInstance(savedType);
        AccessTools.Field(savedType, "Inputs").SetValue(saved, inputs);
        AccessTools.Field(savedType, "Value").SetValue(saved, 0.7f);
        var finalizer = enginePatch.GetMethod("Finalizer", BindingFlags.Static | BindingFlags.NonPublic);
        var sentinel = new Exception("sentinel");
        if (!ReferenceEquals(sentinel, finalizer.Invoke(null, new object[] { sentinel, saved })) || (float)throttle.GetValue(inputs) != 0.7f)
            throw new Exception("Engine input was not restored while preserving the original exception.");
        var reversePatch = plugin.GetType("UrsaMinorThrottle.NativeReverseScopePatch", true);
        var reverseSavedType = reversePatch.GetNestedType("SavedInput", BindingFlags.NonPublic);
        object reverseSaved = Activator.CreateInstance(reverseSavedType);
        AccessTools.Field(reverseSavedType, "Inputs").SetValue(reverseSaved, inputs);
        AccessTools.Field(reverseSavedType, "Value").SetValue(reverseSaved, 0.65f);
        vectorAxis.SetValue(inputs, 0f);
        var reverseFinalizer = reversePatch.GetMethod("Finalizer", BindingFlags.Static | BindingFlags.NonPublic);
        if (!ReferenceEquals(sentinel, reverseFinalizer.Invoke(null, new object[] { sentinel, reverseSaved })) || (float)vectorAxis.GetValue(inputs) != 0.65f)
            throw new Exception("Vectoring input was not restored while preserving the original exception.");
        if (plugin.GetType("UrsaMinorThrottle.NativeReversePatch", false) != null)
            throw new Exception("The old unscoped reverse override is still present.");
        var gaugeType = gameAssembly.GetType("ThrottleGauge", true);
        if (AccessTools.Method(gaugeType, "Refresh") == null || AccessTools.Field(gaugeType, "aircraft") == null ||
            AccessTools.Field(gaugeType, "inputs") == null || AccessTools.Field(gaugeType, "afterburner") == null ||
            AccessTools.Field(gaugeType, "throttleRegions") == null)
            throw new Exception("Throttle gauge contract changed.");
        var gaugePatch = plugin.GetType("UrsaMinorThrottle.ThrottleGaugePatch", true);
        var gaugeSavedType = gaugePatch.GetNestedType("SavedInput", BindingFlags.NonPublic);
        object gaugeSaved = Activator.CreateInstance(gaugeSavedType);
        AccessTools.Field(gaugeSavedType, "Inputs").SetValue(gaugeSaved, inputs);
        AccessTools.Field(gaugeSavedType, "Value").SetValue(gaugeSaved, 0.94f);
        throttle.SetValue(inputs, 0.89f);
        var gaugeFinalizer = gaugePatch.GetMethod("Finalizer", BindingFlags.Static | BindingFlags.NonPublic);
        if (!ReferenceEquals(sentinel, gaugeFinalizer.Invoke(null, new object[] { sentinel, gaugeSaved })) || (float)throttle.GetValue(inputs) != 0.94f)
            throw new Exception("Gauge input was not restored while preserving the original exception.");
        Console.WriteLine("PASS: F-22 gauge hook contract and shared-input restoration");
        Console.WriteLine("PASS: installed game player/input framework/airbrake/engine/vectoring contracts; calibration integration; changed-IL refusal; engine and vectoring restoration");
        Console.WriteLine("PASS: multiplayer eligibility, local simulation and ownership guards, and native client-authoritative snapshot contracts");
        ShortcutTests.Run(plugin, game);

        if (!tryRuntime) return;
        var harmony = new Harmony("com.aaron.ursaminorthrottle.offline.audit");
        try
        {
            foreach (var name in new[] { "PilotThrottlePatch", "AirbrakePatch", "NativeReverseScopePatch", "EngineThrottlePatch", "SetupPilotControlsPatch" })
            {
                var type = plugin.GetType("UrsaMinorThrottle." + name, true);
                var patched = harmony.CreateClassProcessor(type).Patch();
                if (patched == null || patched.Count == 0) throw new Exception("No methods patched for " + name);
            }
            Console.WriteLine("PASS: Harmony installed all patch classes in the isolated audit process");
        }
        finally { harmony.UnpatchSelf(); }
    }
}
