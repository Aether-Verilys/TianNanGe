using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.InputSystem.Utilities;
using Newtonsoft.Json.Linq;

// Run with Pipeline run_script in Play Mode. This never saves scene or asset changes.
// Start returns immediately; read ReportPath when status changes from "running".
public static class VerifyLittleGreenMovement
{
    const string ReportPath = "/tmp/tiannange-player-verification.json";
    const string SessionKey = "TianNanGe.VerifyLittleGreenMovement.Running";
    const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    static readonly Stage[] Stages = {
        new Stage("settle", .3f),
        new Stage("forward_W", .3f, Key.W),
        new Stage("backward_S", .3f, Key.S),
        new Stage("left_A", .3f, Key.A),
        new Stage("right_D", .3f, Key.D),
        new Stage("released", .3f),
        new Stage("diagonal_WD", .3f, Key.W, Key.D),
        new Stage("released_after_diagonal", .3f),
        new Stage("left_rail", 2f, Key.A),
        new Stage("final_release", .5f)
    };

    static CharacterWASDController movement;
    static CharacterController capsule;
    static Animator animator;
    static Transform hips;
    static InputActionAsset runtimeInput;
    static InputActionMap moveMap;
    static Keyboard keyboard, previousKeyboard;
    static ReadOnlyArray<InputDevice>? previousAssetDevices, previousMapDevices;
    static Vector3 originalPosition;
    static Quaternion originalRotation;
    static float originalVerticalVelocity, originalSpeed, originalNormalizedTime;
    static int originalStateHash;
    static bool originalCapsuleEnabled;
    static FieldInfo verticalVelocityField;
    static Vector3 cameraForward, cameraRight;
    static Report report;
    static int stageIndex, lastFrame, queuedFrame;
    static float stageStartTime;
    static double realStart;
    static bool waitingForInput, running;
    static StageResult result;

    [Serializable] sealed class Stage
    {
        public string name;
        public float duration;
        public Key[] keys;
        public Stage(string name, float duration, params Key[] keys)
        { this.name = name; this.duration = duration; this.keys = keys; }
        public Vector2 ExpectedInput()
        {
            Vector2 value = Vector2.zero;
            foreach (var key in keys)
            { if (key == Key.W) value.y++; if (key == Key.S) value.y--; if (key == Key.A) value.x--; if (key == Key.D) value.x++; }
            return Vector2.ClampMagnitude(value, 1f);
        }
    }
    [Serializable] sealed class Sample
    {
        public int frame;
        public float time;
        public Vector3 position;
        public Vector2 input;
        public Vector3 planarVelocity;
        public bool grounded, animatorTransition;
        public string animatorState, nextAnimatorState;
        public float animatorNormalizedTime, animatorSpeed;
        public Quaternion hipsLocalRotation;
    }
    [Serializable] sealed class StageResult
    {
        public string name;
        public float requestedDuration, elapsed;
        public Vector3 start, end, displacement;
        public float forwardDisplacement, rightDisplacement;
        public List<Sample> samples = new List<Sample>();
    }
    [Serializable] sealed class Check
    {
        public string name, detail;
        public bool passed;
        public Check(string name, bool passed, string detail)
        { this.name = name; this.passed = passed; this.detail = detail; }
    }
    [Serializable] sealed class Report
    {
        public string status = "running", startedUtc, finishedUtc, error;
        public bool passed, inputRestrictionRestored, temporaryKeyboardRemoved, characterTransformRestored;
        public Vector3 originalPosition, verificationStart = new Vector3(.38f, .1f, .1f);
        public float configuredSpeed, capsuleRadius;
        public List<StageResult> stages = new List<StageResult>();
        public List<Check> checks = new List<Check>();
    }

    public static string Start()
    {
        if (!EditorApplication.isPlaying || EditorApplication.isPaused)
            throw new InvalidOperationException("Run Start only in unpaused Play Mode.");
        if (running || SessionState.GetBool(SessionKey, false))
            throw new InvalidOperationException("Movement verification is already running.");
        var root = GameObject.Find("TianNanGe_Blockout/Character");
        movement = root == null ? null : root.GetComponent<CharacterWASDController>();
        capsule = root == null ? null : root.GetComponent<CharacterController>();
        animator = root == null ? null : root.GetComponentInChildren<Animator>();
        if (movement == null || !movement.enabled || capsule == null || !capsule.enabled || animator == null || !animator.isHuman)
            throw new InvalidOperationException("An enabled CharacterWASDController, CharacterController and Humanoid Animator are required.");
        runtimeInput = typeof(CharacterWASDController).GetField("runtimeInputActions", PrivateInstance).GetValue(movement) as InputActionAsset;
        if (runtimeInput == null)
            throw new InvalidOperationException("The character must have its own runtime input asset; wait for Awake before starting.");
        moveMap = runtimeInput.FindActionMap("Player", true);
        if (!moveMap.enabled) throw new InvalidOperationException("Player action map is disabled.");
        hips = animator.GetBoneTransform(HumanBodyBones.Hips);
        if (hips == null) throw new InvalidOperationException("Humanoid Hips bone is missing.");

        originalPosition = root.transform.position; originalRotation = root.transform.rotation;
        originalCapsuleEnabled = capsule.enabled;
        verticalVelocityField = typeof(CharacterWASDController).GetField("verticalVelocity", PrivateInstance);
        originalVerticalVelocity = (float)verticalVelocityField.GetValue(movement);
        originalSpeed = animator.GetFloat("Speed");
        var originalState = animator.GetCurrentAnimatorStateInfo(0);
        originalStateHash = originalState.fullPathHash; originalNormalizedTime = originalState.normalizedTime;
        previousAssetDevices = runtimeInput.devices; previousMapDevices = moveMap.devices;
        previousKeyboard = Keyboard.current;
        report = new Report { startedUtc = DateTime.UtcNow.ToString("o"), originalPosition = originalPosition,
            capsuleRadius = capsule.radius,
            configuredSpeed = (float)typeof(CharacterWASDController).GetField("moveSpeed", PrivateInstance).GetValue(movement) };
        var camera = Camera.main;
        cameraForward = camera == null ? Vector3.forward : Vector3.ProjectOnPlane(camera.transform.forward, Vector3.up).normalized;
        cameraRight = camera == null ? Vector3.right : Vector3.ProjectOnPlane(camera.transform.right, Vector3.up).normalized;

        running = true; SessionState.SetBool(SessionKey, true);
        try
        {
            keyboard = InputSystem.AddDevice<Keyboard>("TianNanGeMovementVerificationKeyboard");
            runtimeInput.devices = new InputDevice[] { keyboard };
            moveMap.devices = new InputDevice[] { keyboard };
            InputSystem.onAfterUpdate += RestorePhysicalKeyboardCurrent;
            RestorePhysicalKeyboardCurrent();
            Teleport(report.verificationStart, originalRotation);
            verticalVelocityField.SetValue(movement, 0f);
            stageIndex = 0; lastFrame = -1; realStart = EditorApplication.timeSinceStartup;
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeReload;
            BeginStage();
            SaveReport();
            return "Scheduled WASD/diagonal/release/rail verification; poll " + ReportPath + " for completion (~5 game seconds).";
        }
        catch (Exception exception) { Finish(exception.ToString()); throw; }
    }

    public static string Cancel()
    {
        if (running) Finish("Cancelled explicitly.");
        return "Verification stopped; see " + ReportPath;
    }

    static void BeginStage()
    {
        var stage = Stages[stageIndex];
        result = new StageResult { name = stage.name, requestedDuration = stage.duration };
        report.stages.Add(result);
        queuedFrame = Time.frameCount; waitingForInput = true;
        InputSystem.QueueStateEvent(keyboard, new KeyboardState(stage.keys));
    }

    static void Tick()
    {
        if (!running) return;
        try
        {
            if (!EditorApplication.isPlaying || movement == null) { Finish("Play Mode ended or character disappeared."); return; }
            if (EditorApplication.timeSinceStartup - realStart > 45d) { Finish("Timed out waiting for game frames/input; enable autotick and unpause Play Mode."); return; }
            if (Time.frameCount == lastFrame) return;
            lastFrame = Time.frameCount;
            var stage = Stages[stageIndex];
            if (waitingForInput)
            {
                if (Time.frameCount <= queuedFrame || (movement.MoveInput - stage.ExpectedInput()).sqrMagnitude > .001f) return;
                waitingForInput = false; stageStartTime = Time.time; result.start = movement.transform.position;
            }
            result.samples.Add(Capture());
            if (Time.time - stageStartTime < stage.duration) return;
            result.elapsed = Time.time - stageStartTime; result.end = movement.transform.position;
            result.displacement = result.end - result.start;
            result.forwardDisplacement = Vector3.Dot(result.displacement, cameraForward);
            result.rightDisplacement = Vector3.Dot(result.displacement, cameraRight);
            stageIndex++;
            if (stageIndex == Stages.Length) { Evaluate(); Finish(null); return; }
            BeginStage();
        }
        catch (Exception exception) { Finish(exception.ToString()); }
    }

    static Sample Capture()
    {
        var state = animator.GetCurrentAnimatorStateInfo(0);
        var next = animator.GetNextAnimatorStateInfo(0);
        return new Sample { frame = Time.frameCount, time = Time.time, position = movement.transform.position,
            input = movement.MoveInput, planarVelocity = movement.PlanarVelocity, grounded = capsule.isGrounded,
            animatorState = StateName(state), nextAnimatorState = StateName(next), animatorTransition = animator.IsInTransition(0),
            animatorNormalizedTime = state.normalizedTime, animatorSpeed = animator.GetFloat("Speed"), hipsLocalRotation = hips.localRotation };
    }
    static string StateName(AnimatorStateInfo state)
    { return state.IsName("Walking") ? "Walking" : state.IsName("Idle") ? "Idle" : state.fullPathHash.ToString(); }
    static StageResult StageNamed(string name) { return report.stages.Find(stage => stage.name == name); }
    static void AddCheck(string name, bool passed, string detail) { report.checks.Add(new Check(name, passed, detail)); }
    static void Evaluate()
    {
        var w = StageNamed("forward_W"); var s = StageNamed("backward_S");
        var a = StageNamed("left_A"); var d = StageNamed("right_D");
        AddCheck("W moves forward", w.forwardDisplacement > .2f, w.forwardDisplacement.ToString("F4") + " m");
        AddCheck("S moves backward", s.forwardDisplacement < -.2f, s.forwardDisplacement.ToString("F4") + " m");
        AddCheck("A moves left", a.rightDisplacement < -.2f, a.rightDisplacement.ToString("F4") + " m");
        AddCheck("D moves right", d.rightDisplacement > .2f, d.rightDisplacement.ToString("F4") + " m");
        foreach (string name in new[] { "released", "released_after_diagonal", "final_release" })
        {
            var release = StageNamed(name);
            float planarDistance = Vector3.ProjectOnPlane(release.displacement, Vector3.up).magnitude;
            AddCheck(name + " stops translation", planarDistance < .025f, planarDistance.ToString("F4") + " m after input reached zero");
        }
        var diagonal = StageNamed("diagonal_WD"); float maximumInput = 0f, maximumVelocity = 0f;
        foreach (var sample in diagonal.samples) { maximumInput = Mathf.Max(maximumInput, sample.input.magnitude); maximumVelocity = Mathf.Max(maximumVelocity, sample.planarVelocity.magnitude); }
        AddCheck("diagonal input is normalized", maximumInput > .98f && maximumInput <= 1.01f, "max input=" + maximumInput.ToString("F4"));
        AddCheck("diagonal speed equals configured speed", Mathf.Abs(maximumVelocity - report.configuredSpeed) < .03f, "max speed=" + maximumVelocity.ToString("F4"));
        AddCheck("diagonal moves both axes", diagonal.forwardDisplacement > .15f && diagonal.rightDisplacement > .15f,
            "forward=" + diagonal.forwardDisplacement.ToString("F4") + ", right=" + diagonal.rightDisplacement.ToString("F4"));

        float baselineY = StageNamed("settle").end.y, maxHeightError = 0f, maxHipsAngle = 0f, minRailX = float.MaxValue;
        int grounded = 0, count = 0, walkingSamples = 0;
        Quaternion initialHips = w.samples[0].hipsLocalRotation;
        foreach (var stage in report.stages)
        {
            if (stage.name == "settle") continue;
            foreach (var sample in stage.samples)
            {
                maxHeightError = Mathf.Max(maxHeightError, Mathf.Abs(sample.position.y - baselineY));
                if (sample.grounded) grounded++; count++;
                if (sample.animatorState == "Walking") walkingSamples++;
                maxHipsAngle = Mathf.Max(maxHipsAngle, Quaternion.Angle(initialHips, sample.hipsLocalRotation));
                if (stage.name == "left_rail") minRailX = Mathf.Min(minRailX, sample.position.x);
            }
        }
        AddCheck("ground height remains stable", maxHeightError < .06f, "max y deviation=" + maxHeightError.ToString("F4") + " m");
        AddCheck("character remains grounded", count > 0 && grounded >= count * .9f, grounded + "/" + count + " observed frames grounded");
        AddCheck("walking animator becomes active", walkingSamples > 1, walkingSamples + " observed Walking frames");
        AddCheck("humanoid hips animate", maxHipsAngle > .1f, "max hips rotation delta=" + maxHipsAngle.ToString("F3") + " degrees");
        var final = StageNamed("final_release").samples;
        var finalSample = final[final.Count - 1];
        AddCheck("release returns toward Idle", finalSample.animatorState == "Idle" || (finalSample.animatorTransition && finalSample.nextAnimatorState == "Idle"),
            finalSample.animatorState + " -> " + finalSample.nextAnimatorState + "; Speed=" + finalSample.animatorSpeed.ToString("F4"));
        var rail = StageNamed("left_rail");
        AddCheck("left rail blocks crossing", minRailX > -.9f && rail.end.x < -.65f,
            "min x=" + minRailX.ToString("F4") + ", final x=" + rail.end.x.ToString("F4") + "; rail center=-1.08, radius=" + report.capsuleRadius);
    }

    static void RestorePhysicalKeyboardCurrent() { if (previousKeyboard != null && previousKeyboard.added) previousKeyboard.MakeCurrent(); }
    static void OnPlayModeChanged(PlayModeStateChange state) { if (running && state == PlayModeStateChange.ExitingPlayMode) Finish("Play Mode stopped before completion."); }
    static void OnBeforeReload() { if (running) Finish("Assembly reload interrupted verification."); }
    static void Teleport(Vector3 position, Quaternion rotation)
    { capsule.enabled = false; movement.transform.SetPositionAndRotation(position, rotation); capsule.enabled = originalCapsuleEnabled; Physics.SyncTransforms(); }
    static void SaveReport()
    {
        // Unity's serializer omits Lists whose item types came from run_script's
        // in-memory assembly. Serialize those collections explicitly so evidence
        // and failed checks are retained in the report.
        var json = JObject.Parse(JsonUtility.ToJson(report));
        var checks = new JArray();
        foreach (var check in report.checks) checks.Add(JObject.Parse(JsonUtility.ToJson(check)));
        var stages = new JArray();
        foreach (var stage in report.stages)
        {
            var item = JObject.Parse(JsonUtility.ToJson(stage));
            var samples = new JArray();
            foreach (var sample in stage.samples) samples.Add(JObject.Parse(JsonUtility.ToJson(sample)));
            item["samples"] = samples;
            stages.Add(item);
        }
        json["checks"] = checks;
        json["stages"] = stages;
        File.WriteAllText(ReportPath, json.ToString());
    }
    static void Finish(string error)
    {
        if (!running) return;
        running = false; SessionState.SetBool(SessionKey, false);
        EditorApplication.update -= Tick; EditorApplication.playModeStateChanged -= OnPlayModeChanged;
        AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeReload; InputSystem.onAfterUpdate -= RestorePhysicalKeyboardCurrent;
        report.error = error;
        try
        {
            if (runtimeInput != null) runtimeInput.devices = previousAssetDevices;
            if (moveMap != null) moveMap.devices = previousMapDevices;
            report.inputRestrictionRestored = true;
            if (keyboard != null && keyboard.added) InputSystem.RemoveDevice(keyboard);
            report.temporaryKeyboardRemoved = keyboard == null || !keyboard.added;
            RestorePhysicalKeyboardCurrent();
            if (movement != null && capsule != null)
            {
                if (animator != null)
                { animator.SetFloat("Speed", originalSpeed); animator.Play(originalStateHash, 0, originalNormalizedTime); animator.Update(0f); }
                Teleport(originalPosition, originalRotation);
                verticalVelocityField.SetValue(movement, originalVerticalVelocity);
                report.characterTransformRestored = (movement.transform.position - originalPosition).sqrMagnitude < .000001f && Quaternion.Angle(movement.transform.rotation, originalRotation) < .001f;
            }
        }
        catch (Exception exception) { report.error = (report.error ?? "") + "\nCleanup: " + exception; }
        report.passed = string.IsNullOrEmpty(report.error) && report.checks.Count > 0 && report.checks.TrueForAll(check => check.passed)
            && report.inputRestrictionRestored && report.temporaryKeyboardRemoved && report.characterTransformRestored;
        report.status = report.passed ? "passed" : "failed"; report.finishedUtc = DateTime.UtcNow.ToString("o");
        SaveReport();
        Debug.Log("Little Green movement verification " + report.status + ". Report: " + ReportPath);
    }
}
