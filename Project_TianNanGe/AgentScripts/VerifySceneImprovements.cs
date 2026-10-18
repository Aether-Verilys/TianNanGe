using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Newtonsoft.Json;

// Read-only verification for the bridge, mountain supports, and hand sword.
// Run with Unity Pipeline run_script. The method never creates, destroys,
// saves, or applies an object; it only inspects the active scene and assets.
public static class VerifySceneImprovements
{
    const string ScenePath = "Assets/TianNanGe/Scenes/TianNanGe_Main.unity";
    const string MountainsPrefabPath = "Assets/TianNanGe/Prefabs/Environment/Mountains.prefab";
    const string SwordPrefabPath = "Assets/TianNanGe/Prefabs/Props/HandSword.prefab";
    const string ReportPath = "/tmp/tiannange-scene-improvements-verification.json";
    const float Epsilon = 0.08f;

    [Serializable]
    sealed class Check
    {
        public string name;
        public bool passed;
        public string detail;
        public Check(string name, bool passed, string detail)
        { this.name = name; this.passed = passed; this.detail = detail; }
    }

    [Serializable]
    sealed class Report
    {
        public string status;
        public string scene;
        public string generatedUtc;
        public bool passed;
        public List<Check> checks = new List<Check>();
    }

    public static string Verify()
    {
        var report = new Report
        {
            status = "completed",
            scene = SceneManager.GetActiveScene().path,
            generatedUtc = DateTime.UtcNow.ToString("o")
        };

        try
        {
            VerifyActiveScene(report);
        }
        catch (Exception exception)
        {
            Add(report, "verification did not throw", false, exception.GetType().Name + ": " + exception.Message);
        }

        report.passed = report.checks.Count > 0 && AllPassed(report.checks);
        // JsonUtility can omit lists from run_script's ephemeral assembly.
        File.WriteAllText(ReportPath, JsonConvert.SerializeObject(report, Formatting.Indented));
        return FormatSummary(report) + "; report=" + ReportPath;
    }

    static void VerifyActiveScene(Report report)
    {
        var scene = EditorSceneManager.GetActiveScene();
        Add(report, "active scene is TianNanGe_Main", scene.path == ScenePath,
            "active=" + scene.path);
        if (scene.path != ScenePath)
            return;

        var blockout = GameObject.Find("TianNanGe_Blockout");
        Add(report, "TianNanGe_Blockout exists", blockout != null,
            blockout == null ? "missing root" : "root found");
        if (blockout == null)
            return;

        VerifyBridge(report, blockout.transform);
        var mountains = VerifyMountains(report, blockout.transform);
        VerifyPavilionSupports(report, blockout.transform, mountains);
        VerifyHandSword(report, blockout.transform);
        VerifySceneReferences(report, blockout.transform);
        VerifyPrefabReferences(report);
    }

    static void VerifyBridge(Report report, Transform blockout)
    {
        var bridge = blockout.Find("Bridge");
        Add(report, "Bridge exists", bridge != null, bridge == null ? "missing" : bridge.name);
        if (bridge == null)
            return;

        var deck = bridge.Find("BridgeDeck");
        var snowDeck = bridge.Find("SnowDeck");
        var deckRenderer = deck == null ? null : deck.GetComponent<Renderer>();
        bool deckLength = deckRenderer != null && Near(deckRenderer.bounds.size.z, 38f, 0.12f);
        bool deckStart = deckRenderer != null && Near(deckRenderer.bounds.min.z, -16.5f, 0.12f);
        bool deckEnd = deckRenderer != null && Near(deckRenderer.bounds.max.z, 21.5f, 0.12f);
        Add(report, "Bridge deck length is 38m", deckLength,
            BoundsText(deckRenderer) + "; expected z size=38");
        Add(report, "Bridge deck starts at z=-16.5", deckStart,
            BoundsText(deckRenderer) + "; expected min z=-16.5");
        Add(report, "Bridge deck ends at z=21.5", deckEnd,
            BoundsText(deckRenderer) + "; expected max z=21.5");
        Add(report, "Bridge snow deck exists", snowDeck != null && snowDeck.GetComponent<Renderer>() != null,
            snowDeck == null ? "missing" : BoundsText(snowDeck.GetComponent<Renderer>()));
        var deckCollider = bridge.GetComponent<BoxCollider>();
        Add(report, "Bridge ground collision spans full deck", deckCollider != null &&
            deckCollider.enabled && !deckCollider.isTrigger && Near(deckCollider.size.z, 38f, .01f) &&
            Near(deckCollider.center.z, 2.5f, .01f),
            deckCollider == null ? "missing BoxCollider" : "center=" + deckCollider.center + ", size=" + deckCollider.size);
        foreach (var name in new[] { "LeftRailCollision", "RightRailCollision" })
        {
            var rail = bridge.Find(name);
            var collider = rail == null ? null : rail.GetComponent<BoxCollider>();
            Add(report, name + " spans full deck", collider != null && collider.enabled &&
                collider.gameObject.activeInHierarchy && !collider.isTrigger &&
                Near(collider.size.z, 38f, .01f) && Near(collider.center.z, 2.5f, .01f),
                collider == null ? "missing" : "center=" + collider.center + ", size=" + collider.size);
        }
        var bridgeSource = PrefabUtility.GetCorrespondingObjectFromSource(bridge.gameObject);
        string bridgeSourcePath = bridgeSource == null ? "<none>" : AssetDatabase.GetAssetPath(bridgeSource);
        Add(report, "Bridge scene object references Bridge.prefab",
            bridgeSourcePath == "Assets/TianNanGe/Prefabs/Environment/Bridge.prefab", bridgeSourcePath);

        var connections = bridge.Find("BridgeConnections");
        Add(report, "BridgeConnections exists", connections != null,
            connections == null ? "missing" : connections.name);
        if (connections == null)
            return;

        VerifyAbutment(report, connections, "NearAbutment", -18f);
        VerifyAbutment(report, connections, "FarAbutment", 23f);
        Add(report, "both landing collision objects exist",
            connections.Find("NearLandingCollision") != null && connections.Find("FarLandingCollision") != null,
            "near=" + (connections.Find("NearLandingCollision") != null) +
            ", far=" + (connections.Find("FarLandingCollision") != null));
    }

    static void VerifyAbutment(Report report, Transform connections, string name, float expectedZ)
    {
        var t = connections.Find(name);
        var renderer = t == null ? null : t.GetComponent<Renderer>();
        bool center = renderer != null && Near(renderer.bounds.center.z, expectedZ, Epsilon);
        bool size = renderer != null && Near(renderer.bounds.size.x, 6f, Epsilon) &&
            Near(renderer.bounds.size.z, 4.5f, Epsilon);
        Add(report, name + " center z=" + expectedZ, center,
            BoundsText(renderer) + "; expected center z=" + expectedZ.ToString("0.##"));
        Add(report, name + " size is 6x4.5", size,
            BoundsText(renderer) + "; expected x=6,z=4.5");
    }

    static Transform VerifyMountains(Report report, Transform blockout)
    {
        var mountains = blockout.Find("Mountains");
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(MountainsPrefabPath);
        Add(report, "Mountains scene object exists", mountains != null,
            mountains == null ? "missing" : mountains.name);
        Add(report, "Mountains prefab exists", prefab != null,
            prefab == null ? "missing " + MountainsPrefabPath : MountainsPrefabPath);
        if (mountains != null && prefab != null)
        {
            var source = PrefabUtility.GetCorrespondingObjectFromSource(mountains.gameObject);
            string sourcePath = source == null ? "<none>" : AssetDatabase.GetAssetPath(source);
            Add(report, "Mountains scene object references Mountains.prefab",
                sourcePath == MountainsPrefabPath, "source=" + sourcePath);
            Add(report, "Mountains has mesh colliders",
                mountains.GetComponentsInChildren<MeshCollider>(true).Length > 0,
                "meshColliders=" + mountains.GetComponentsInChildren<MeshCollider>(true).Length);
        }
        return mountains;
    }

    static void VerifyPavilionSupports(Report report, Transform blockout, Transform mountains)
    {
        var architecture = blockout.Find("Architecture");
        string[] names = { "MainPavilion", "LeftPavilion", "FarPavilion", "FarRight" };
        string[] supportNames = { "Mountain_Main", "Mountain_Left", "Mountain_Far", "Mountain_FarRight" };
        if (architecture == null)
        {
            Add(report, "Architecture exists for mountain support checks", false, "missing Architecture");
            return;
        }
        for (int index = 0; index < names.Length; index++)
        {
            string name = names[index];
            var pavilion = architecture.Find(name);
            var baseTransform = pavilion == null ? null : pavilion.Find("Proxy_ReplaceWithTripo/StoneBase");
            var stone = baseTransform == null ? null : baseTransform.GetComponent<Renderer>();
            var expectedMountain = mountains == null ? null : mountains.Find(supportNames[index]);
            bool exists = expectedMountain != null && pavilion != null && stone != null;
            bool supported = false;
            string detail = exists ? BoundsText(stone) : "pavilion, stone base, or Mountains missing";
            if (exists)
            {
                Bounds b = stone.bounds;
                var offsets = new[] { Vector2.zero, new Vector2(-1,-1), new Vector2(-1,1),
                    new Vector2(1,-1), new Vector2(1,1) };
                int supportedPoints = 0;
                var evidence = new List<string>();
                foreach (var offset in offsets)
                {
                    var origin = new Vector3(b.center.x + offset.x * b.extents.x * .95f,
                        b.min.y + 1f, b.center.z + offset.y * b.extents.z * .95f);
                    var ray = new Ray(origin, Vector3.down);
                    float highestSupport = float.NegativeInfinity;
                    foreach (var collider in expectedMountain.GetComponentsInChildren<Collider>())
                    {
                        RaycastHit hit;
                        if (collider.enabled && !collider.isTrigger && collider.Raycast(ray, out hit, 2f) &&
                            hit.normal.y > .75f && hit.point.y > highestSupport)
                            highestSupport = hit.point.y;
                    }
                    if (Near(highestSupport, b.min.y, .20f)) supportedPoints++;
                    evidence.Add(float.IsNegativeInfinity(highestSupport) ? "miss" : highestSupport.ToString("F3"));
                }
                supported = supportedPoints == offsets.Length;
                detail += "; expected=" + expectedMountain.name + "; supported center/corners=" +
                    supportedPoints + "/5; baseMinY=" + b.min.y.ToString("F3") +
                    "; hitYs=" + string.Join(",", evidence);
            }
            Add(report, name + " is supported by a mountain", supported, detail);
        }
    }

    static void VerifyHandSword(Report report, Transform blockout)
    {
        var character = blockout.Find("Character");
        var visual = character == null ? null : FindChild(character, "Visual_LittleGreen");
        var hand = visual == null ? null : FindChildByNames(visual, "mixamorig:RightHand", "RightHand");
        var socket = hand == null ? null : hand.Find("SwordSocket");
        var sword = socket == null ? null : socket.Find("HandSword");
        var swordPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(SwordPrefabPath);
        Add(report, "character Visual_LittleGreen exists", visual != null,
            visual == null ? "missing" : visual.name);
        Add(report, "character right hand exists", hand != null,
            hand == null ? "missing mixamorig:RightHand/RightHand" : hand.name);
        Add(report, "RightHand/SwordSocket exists", socket != null,
            socket == null ? "missing" : socket.name);
        Add(report, "SwordSocket/HandSword exists", sword != null,
            sword == null ? "missing" : sword.name);
        Add(report, "HandSword prefab exists", swordPrefab != null,
            swordPrefab == null ? "missing " + SwordPrefabPath : SwordPrefabPath);
        if (sword != null)
        {
            var source = PrefabUtility.GetCorrespondingObjectFromOriginalSource(sword.gameObject);
            string sourcePath = source == null ? "<none>" : AssetDatabase.GetAssetPath(source);
            // Resolve through the outer character prefab to the reusable prop.
            Add(report, "HandSword scene object references HandSword.prefab",
                sourcePath == SwordPrefabPath, "original source=" + sourcePath);
        }
        var characterPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/TianNanGe/Prefabs/Characters/LittleGreen.prefab");
        var prefabHand = characterPrefab == null ? null : FindChildByNames(characterPrefab.transform, "mixamorig:RightHand", "RightHand");
        var prefabSword = prefabHand == null ? null : prefabHand.Find("SwordSocket/HandSword");
        Add(report, "LittleGreen prefab retains RightHand/SwordSocket/HandSword", prefabSword != null,
            prefabSword == null ? "missing hand sword in character asset" : "saved in LittleGreen.prefab");
    }

    static void VerifySceneReferences(Report report, Transform blockout)
    {
        int missingScripts = 0, meshFilters = 0, missingMeshes = 0, renderers = 0, missingMaterials = 0;
        var sceneTransforms = new List<Transform>();
        foreach (var root in blockout.gameObject.scene.GetRootGameObjects())
            sceneTransforms.AddRange(root.GetComponentsInChildren<Transform>(true));
        foreach (var go in sceneTransforms)
        {
            missingScripts += GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go.gameObject);
            foreach (var filter in go.GetComponents<MeshFilter>())
            {
                meshFilters++;
                if (filter.sharedMesh == null) missingMeshes++;
            }
            foreach (var collider in go.GetComponents<MeshCollider>())
            {
                if (collider.sharedMesh == null) missingMeshes++;
            }
            foreach (var skin in go.GetComponents<SkinnedMeshRenderer>())
                if (skin.sharedMesh == null) missingMeshes++;
            foreach (var renderer in go.GetComponents<Renderer>())
            {
                renderers++;
                if (renderer.sharedMaterials.Length == 0) missingMaterials++;
                foreach (var material in renderer.sharedMaterials)
                    if (material == null || material.shader == null) missingMaterials++;
            }
        }
        Add(report, "scene has no missing scripts", missingScripts == 0, "missingScripts=" + missingScripts);
        Add(report, "scene mesh references are valid", missingMeshes == 0,
            "meshFilters=" + meshFilters + ", missingMeshes=" + missingMeshes);
        Add(report, "scene material references are valid", missingMaterials == 0,
            "renderers=" + renderers + ", missingMaterials=" + missingMaterials);
    }

    static void VerifyPrefabReferences(Report report)
    {
        int prefabCount = 0, missingScripts = 0, meshFilters = 0, missingMeshes = 0;
        int renderers = 0, missingMaterials = 0;
        foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/TianNanGe" }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) continue;
            prefabCount++;
            foreach (var go in prefab.GetComponentsInChildren<Transform>(true))
            {
                missingScripts += GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go.gameObject);
                foreach (var filter in go.GetComponents<MeshFilter>())
                {
                    meshFilters++;
                    if (filter.sharedMesh == null) missingMeshes++;
                }
                foreach (var collider in go.GetComponents<MeshCollider>())
                    if (collider.sharedMesh == null) missingMeshes++;
                foreach (var skin in go.GetComponents<SkinnedMeshRenderer>())
                    if (skin.sharedMesh == null) missingMeshes++;
                foreach (var renderer in go.GetComponents<Renderer>())
                {
                    renderers++;
                    if (renderer.sharedMaterials.Length == 0) missingMaterials++;
                    foreach (var material in renderer.sharedMaterials)
                        if (material == null || material.shader == null) missingMaterials++;
                }
            }
        }
        Add(report, "TianNanGe prefab assets have no missing scripts", missingScripts == 0,
            "prefabs=" + prefabCount + ", missingScripts=" + missingScripts);
        Add(report, "TianNanGe prefab mesh references are valid", missingMeshes == 0,
            "meshFilters=" + meshFilters + ", missingMeshes=" + missingMeshes);
        Add(report, "TianNanGe prefab material references are valid", missingMaterials == 0,
            "renderers=" + renderers + ", missingMaterials=" + missingMaterials);
    }

    static Transform FindChild(Transform root, string name)
    {
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
            if (t.name == name) return t;
        return null;
    }

    static Transform FindChildByNames(Transform root, params string[] names)
    {
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
            foreach (var name in names)
                if (t.name == name) return t;
        return null;
    }

    static bool Near(float actual, float expected, float tolerance)
    { return Mathf.Abs(actual - expected) <= tolerance; }

    static string BoundsText(Renderer renderer)
    {
        if (renderer == null) return "renderer=<none>";
        Bounds b = renderer.bounds;
        return "center=" + b.center.ToString("F3") + ", size=" + b.size.ToString("F3");
    }

    static void Add(Report report, string name, bool passed, string detail)
    { report.checks.Add(new Check(name, passed, detail)); }

    static bool AllPassed(List<Check> checks)
    {
        foreach (var check in checks)
            if (!check.passed) return false;
        return true;
    }

    static string FormatSummary(Report report)
    {
        int passed = 0;
        foreach (var check in report.checks) if (check.passed) passed++;
        return "VerifySceneImprovements " + (report.passed ? "PASS" : "FAIL") +
            ": " + passed + "/" + report.checks.Count + " checks";
    }
}
