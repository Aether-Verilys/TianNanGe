using System;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;

public static class SetupLittleGreenAndPrefabs
{
    const string ScenePath = "Assets/TianNanGe/Scenes/TianNanGe_Main.unity";
    const string LittleGreenPath = "Assets/Character/little+green/tripo_convert_4dee5643-20cb-465c-a529-88f2794a3fe2.fbx";
    const string WalkingPath = "Assets/Animations/X Bot@Walking.fbx";
    const string InputPath = "Assets/InputSystem_Actions.inputactions";
    const string ControllerPath = "Assets/TianNanGe/Characters/LittleGreen/LittleGreen.controller";

    public static string Setup()
    {
        var scene = EditorSceneManager.GetActiveScene();
        if (EditorApplication.isPlaying || scene.path != ScenePath)
            throw new Exception("Open TianNanGe_Main in Edit Mode.");
        if (AssetDatabase.LoadAssetAtPath<GameObject>("Assets/TianNanGe/Prefabs/Characters/LittleGreen.prefab") != null)
            throw new Exception("Little Green prefab already exists. Edit the existing prefab to preserve manual changes.");

        Directory.CreateDirectory("Assets/TianNanGe/Characters/LittleGreen");
        Directory.CreateDirectory("Assets/TianNanGe/Prefabs/Characters");
        Directory.CreateDirectory("Assets/TianNanGe/Prefabs/Environment");
        Directory.CreateDirectory("Assets/TianNanGe/Prefabs/Architecture");
        Directory.CreateDirectory("Assets/TianNanGe/Prefabs/Lighting");
        Directory.CreateDirectory("Assets/TianNanGe/Prefabs/Cameras");
        AssetDatabase.Refresh();

        var littleModel = AssetDatabase.LoadAssetAtPath<GameObject>(LittleGreenPath);
        var character = GameObject.Find("TianNanGe_Blockout/Character");
        if (littleModel == null || character == null)
            throw new Exception("Little Green FBX or scene Character root is missing.");

        var walkingClip = ConfigureWalkingImport();
        var controller = CreateWalkingController(walkingClip);
        var visual = EnsureLittleGreenVisual(character.transform, littleModel, controller);
        ConfigureCharacterMovement(character, visual);
        ConfigureBridgeCollider();

        SavePrefab(character, "Assets/TianNanGe/Prefabs/Characters/LittleGreen.prefab");
        SavePrefab(GameObject.Find("TianNanGe_Blockout/Bridge"), "Assets/TianNanGe/Prefabs/Environment/Bridge.prefab");
        SavePrefab(GameObject.Find("TianNanGe_Blockout/Dragon"), "Assets/TianNanGe/Prefabs/Environment/Dragon.prefab");

        string[] architecture = { "MainPavilion", "LeftPavilion", "FarPavilion", "FarRight", "SnowCourtyard" };
        foreach (string name in architecture)
            SavePrefab(GameObject.Find("TianNanGe_Blockout/Architecture/" + name),
                "Assets/TianNanGe/Prefabs/Architecture/" + name + ".prefab");

        SavePrefab(GameObject.Find("TianNanGe_Blockout/Lighting"), "Assets/TianNanGe/Prefabs/Lighting/LightingRig.prefab");
        SavePrefab(GameObject.Find("TianNanGe_Blockout/Cameras"), "Assets/TianNanGe/Prefabs/Cameras/CameraRig.prefab");
        SavePrefab(GameObject.Find("StaticLightingSky"), "Assets/TianNanGe/Prefabs/Lighting/StaticLightingSky.prefab");

        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.Refresh();

        var cc = character.GetComponent<CharacterController>();
        var movement = character.GetComponent<CharacterWASDController>();
        var animator = visual.GetComponentInChildren<Animator>();
        return $"Little Green installed; walking={walkingClip.name} {walkingClip.length:0.###}s human={walkingClip.isHumanMotion}; " +
               $"characterPrefab=Assets/TianNanGe/Prefabs/Characters/LittleGreen.prefab; " +
               $"controller={cc != null}; animator={(animator != null && animator.runtimeAnimatorController != null)}; " +
               $"movement={movement != null}; prefabs=Bridge,Dragon,5Architecture,LightingRig,CameraRig";
    }

    static AnimationClip ConfigureWalkingImport()
    {
        var importer = (ModelImporter)AssetImporter.GetAtPath(WalkingPath);
        if (importer == null) throw new Exception("Walking FBX importer not found.");
        importer.animationType = ModelImporterAnimationType.Human;
        importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        importer.importAnimation = true;
        importer.optimizeGameObjects = false;
        var source = importer.defaultClipAnimations;
        if (source == null || source.Length == 0) throw new Exception("Walking clip missing.");
        var walking = source.FirstOrDefault(c => c.name == "Walking") ?? source[0];
        walking.name = "Walking";
        walking.loopTime = true;
        walking.loopPose = true;
        walking.firstFrame = 0;
        walking.lastFrame = 29;
        importer.clipAnimations = new[] { walking };
        importer.SaveAndReimport();
        var clip = AssetDatabase.LoadAllAssetsAtPath(WalkingPath)
            .OfType<AnimationClip>().FirstOrDefault(c => c.name == "Walking");
        if (clip == null || !clip.isHumanMotion)
            throw new Exception("Walking clip did not import as Humanoid.");
        return clip;
    }

    static AnimatorController CreateWalkingController(AnimationClip walking)
    {
        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
        if (controller == null) controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
        foreach (var state in controller.layers[0].stateMachine.states)
            controller.layers[0].stateMachine.RemoveState(state.state);
        controller.parameters = new AnimatorControllerParameter[0];
        controller.AddParameter("Speed", AnimatorControllerParameterType.Float);
        var machine = controller.layers[0].stateMachine;
        var idle = machine.AddState("Idle");
        // Hold the first frame of the supplied humanoid walk as the neutral pose.
        // This preserves the author's authored stance; the controller switches to
        // the same clip at normal speed once Speed exceeds the transition threshold.
        idle.motion = walking;
        idle.speed = 0f;
        var walk = machine.AddState("Walking");
        walk.motion = walking;
        walk.speed = 1f;
        machine.defaultState = idle;
        var start = idle.AddTransition(walk);
        start.hasExitTime = false;
        start.duration = .12f;
        start.AddCondition(AnimatorConditionMode.Greater, .1f, "Speed");
        var stop = walk.AddTransition(idle);
        stop.hasExitTime = false;
        stop.duration = .12f;
        stop.AddCondition(AnimatorConditionMode.Less, .08f, "Speed");
        EditorUtility.SetDirty(controller);
        return controller;
    }

    static GameObject EnsureLittleGreenVisual(Transform character, GameObject model, AnimatorController controller)
    {
        var visual = character.Find("Visual_LittleGreen");
        if (visual == null)
        {
            visual = ((GameObject)PrefabUtility.InstantiatePrefab(model, character)).transform;
            visual.name = "Visual_LittleGreen";
            visual.localPosition = Vector3.zero;
            visual.localRotation = Quaternion.identity;
            visual.localScale = Vector3.one;
        }
        var animator = visual.GetComponent<Animator>();
        if (animator == null) animator = visual.gameObject.AddComponent<Animator>();
        animator.runtimeAnimatorController = controller;
        animator.applyRootMotion = false;
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        visual.localPosition = Vector3.zero;
        visual.localScale = Vector3.one * 2f;
        var renderers = visual.GetComponentsInChildren<Renderer>();
        var bounds = renderers[0].bounds;
        foreach (var r in renderers) bounds.Encapsulate(r.bounds);
        visual.position += Vector3.up * (character.position.y - bounds.min.y);
        return visual.gameObject;
    }

    static void ConfigureCharacterMovement(GameObject character, GameObject visual)
    {
        var oldProxy = character.transform.Find("Proxy_ReplaceWithTripo");
        if (oldProxy != null) oldProxy.gameObject.SetActive(false);
        var animator = visual.GetComponentInChildren<Animator>();
        var cc = character.GetComponent<CharacterController>();
        if (cc == null) cc = character.AddComponent<CharacterController>();
        cc.center = new Vector3(0f, 1.05f, 0f);
        cc.height = 2.1f;
        cc.radius = .25f;
        cc.skinWidth = .03f;
        cc.stepOffset = .28f;
        cc.slopeLimit = 45f;

        var movement = character.GetComponent<CharacterWASDController>();
        if (movement == null) movement = character.AddComponent<CharacterWASDController>();
        var so = new SerializedObject(movement);
        var input = AssetDatabase.LoadAssetAtPath<InputActionAsset>(InputPath);
        so.FindProperty("inputActions").objectReferenceValue = input;
        so.FindProperty("animator").objectReferenceValue = animator;
        so.FindProperty("movementCamera").objectReferenceValue = null;
        so.FindProperty("moveSpeed").floatValue = 1.5f;
        so.FindProperty("rotationSpeed").floatValue = 720f;
        so.FindProperty("cameraRelative").boolValue = true;
        so.FindProperty("disableRootMotion").boolValue = true;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    static void ConfigureBridgeCollider()
    {
        var bridge = GameObject.Find("TianNanGe_Blockout/Bridge");
        if (bridge == null) return;
        var collider = bridge.GetComponent<BoxCollider>();
        if (collider == null) collider = bridge.AddComponent<BoxCollider>();
        collider.center = new Vector3(.35f, .02f, 4f);
        collider.size = new Vector3(2.76f, .10f, 17f);
        collider.isTrigger = false;
        foreach (float x in new[] { -1.08f, 1.78f })
        {
            string name = x < 0 ? "LeftRailCollision" : "RightRailCollision";
            var t = bridge.transform.Find(name);
            if (t == null) { t = new GameObject(name).transform; t.SetParent(bridge.transform, false); }
            var rail = t.GetComponent<BoxCollider>();
            if (rail == null) rail = t.gameObject.AddComponent<BoxCollider>();
            rail.center = new Vector3(x, .8f, 4f);
            rail.size = new Vector3(.12f, 1.6f, 17f);
        }
        var courtyard = GameObject.Find("TianNanGe_Blockout/Architecture/SnowCourtyard");
        if (courtyard != null && courtyard.GetComponent<BoxCollider>() == null) courtyard.AddComponent<BoxCollider>();
    }

    public static string RefineInstalledCharacter()
    {
        if (EditorApplication.isPlaying) throw new Exception("Exit Play Mode first.");
        var c = GameObject.Find("TianNanGe_Blockout/Character");
        var walk = AssetDatabase.LoadAllAssetsAtPath(WalkingPath).OfType<AnimationClip>().First(x => x.name == "Walking");
        var controller = CreateWalkingController(walk);
        var visual = EnsureLittleGreenVisual(c.transform, AssetDatabase.LoadAssetAtPath<GameObject>(LittleGreenPath), controller);
        ConfigureCharacterMovement(c, visual);
        c.transform.rotation = Quaternion.Euler(0, 210, 0);
        ConfigureBridgeCollider();
        SavePrefab(c, "Assets/TianNanGe/Prefabs/Characters/LittleGreen.prefab");
        SavePrefab(GameObject.Find("TianNanGe_Blockout/Bridge"), "Assets/TianNanGe/Prefabs/Environment/Bridge.prefab");
        SavePrefab(GameObject.Find("TianNanGe_Blockout/Architecture/SnowCourtyard"), "Assets/TianNanGe/Prefabs/Architecture/SnowCourtyard.prefab");
        AssetDatabase.SaveAssets();EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
        return "Refined scale, grounded collision, frozen walk stance, and disabled original proxy.";
    }

    static void SavePrefab(GameObject instance, string path)
    {
        if (instance == null) throw new Exception("Missing scene object for prefab: " + path);
        string dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        PrefabUtility.SaveAsPrefabAssetAndConnect(instance, path, InteractionMode.AutomatedAction);
    }
}
