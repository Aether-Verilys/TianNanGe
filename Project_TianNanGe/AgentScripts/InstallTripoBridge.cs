using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;

/// <summary>
/// Explicit Pipeline run_script entry: InstallTripoBridge.Install.
/// Reads ProjectRoot/Temp/tripo-bridge-install.json when present, otherwise
/// ../SourceAssets/Composition/bridge-layout.json. Writing this file never runs it.
/// {
///   "assetFolder":"Assets/TianNanGe/Tripo/WindingBridge10", "name":"WindingBridge_H31_8K",
///   "sourcePlan":"../SourceAssets/Tripo/WindingBridge10/model-plan.json", "upAxis":"Y",
///   "expectedTextureSize":8192, "position":[0,-5,2.5], "scale":[20,20,20], "yaw":0,
///   "anchors":[{"name":"nearDeck","local":[0,0.5,-0.5]}], "notes":"placement explanation"
/// }
/// position is the WORLD location of the standard prefab's centered, grounded pivot.
/// scale is the WORLD axis multiplier applied AFTER model yaw (scalar, XYZ object, or array).
/// Only the scene instance changes during placement updates; standard prefab stays native.
/// No geometry is reduced. Actual static mesh triangle count must equal ImportReport.
/// Actual mesh vertices determine bounds; each visual mesh has its matching MeshCollider.
/// Old bridge Renderers and Colliders (including root colliders) are disabled, never deleted.
/// BridgeConnections and FarPavilionStoneStairs subtrees remain unchanged.
/// </summary>
public static class InstallTripoBridge
{
    const string MainScene = "Assets/TianNanGe/Scenes/TianNanGe_Main.unity";
    const string BridgePath = "TianNanGe_Blockout/Bridge";
    const string BridgePrefab = "Assets/TianNanGe/Prefabs/Environment/Bridge.prefab";
    const string InstanceName = "Tripo_WindingBridge";
    const string DefaultFolder = "Assets/TianNanGe/Tripo/WindingBridge10";
    const string DefaultName = "WindingBridge_H31_8K";
    static readonly string[] PreservedRoots = { "BridgeConnections", "FarPavilionStoneStairs" };

    public static string Install()
    {
        Scene scene = SceneManager.GetActiveScene();
        GuardScene(scene);
        string project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string configPath = Path.Combine(project, "Temp/tripo-bridge-install.json");
        if (!File.Exists(configPath)) configPath = Path.GetFullPath(Path.Combine(project, "../SourceAssets/Composition/bridge-layout.json"));
        JObject config = ReadObject(configPath);
        Vector3 position = Vector(config["position"], "position");
        Vector3 scale = Vector(config["scale"], "scale", true);
        Positive(scale, "Bridge scale");
        float yaw = config["yaw"] == null ? 0 : Number(config, "yaw");
        string folder = AssetFolder((string)config["assetFolder"] ?? DefaultFolder, project);
        string name = Segment((string)config["name"] ?? DefaultName);
        string fbxPath = folder + "/" + name + ".fbx";
        string importPath = folder + "/" + name + "_ImportReport.json";
        string materialPath = folder + "/" + name + "_HDRP.mat";
        string prefabPath = folder + "/" + name + ".prefab";
        string sourcePlanPath = Path.GetFullPath(Path.Combine(project,
            (string)config["sourcePlan"] ?? "../SourceAssets/Tripo/WindingBridge10/model-plan.json"));
        JObject sourcePlan = ReadObject(sourcePlanPath);
        ValidatePlan(sourcePlan);
        JObject imported = ReadObject(Path.Combine(project, importPath));
        long reportedTriangles = (long?)imported["geometry"]?["triangles"] ?? -1;
        if (reportedTriangles <= 0) throw new InvalidOperationException("ImportReport must contain positive geometry.triangles.");
        if ((string)imported["fbx"] != name + ".fbx") throw new InvalidOperationException("ImportReport names a different FBX.");
        if ((bool?)imported["images_resized"] != false || (bool?)imported["uvs_repacked"] != false)
            throw new InvalidOperationException("ImportReport must confirm native image dimensions and preserved UVs.");
        var materialReports = imported["materials"] as JArray;
        if (materialReports == null || materialReports.Count != 1)
            throw new InvalidOperationException("This single-texture-set installer requires exactly one imported material.");
        JObject sourceMaterial = (JObject)materialReports[0];
        Transform bridge = Find(scene, BridgePath);
        if (bridge == null || !bridge.gameObject.activeInHierarchy)
            throw new InvalidOperationException("Active original bridge scene root is required.");
        RequireUnrotatedPositive(bridge);
        string sourcePrefabBefore = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(bridge.gameObject);
        if (sourcePrefabBefore != BridgePrefab) throw new InvalidOperationException("Expected original Bridge.prefab connection.");
        string originalPrefabHash = HashFile(Path.Combine(project, BridgePrefab));
        string rootBefore = TransformFingerprint(bridge);
        Transform existing = bridge.Find(InstanceName);
        if (existing != null && PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(existing.gameObject) != prefabPath)
            throw new InvalidOperationException("Instance name is occupied by an unrelated object: " + InstanceName);
        foreach (Transform child in bridge)
            if (child != existing && PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(child.gameObject) == prefabPath)
                throw new InvalidOperationException("Bridge already has a Tripo instance with a different name: " + child.name);
        var originalObjects = bridge.GetComponentsInChildren<Transform>(true).Where(t => existing == null || !Within(t, existing)).ToArray();
        var preserved = PreservedRoots.Select(n => bridge.Find(n)).Where(t => t != null).ToArray();
        var preserveBefore = preserved.ToDictionary(t => t, SubtreeFingerprint);
        string standardPrefabBefore = File.Exists(Path.Combine(project, prefabPath)) ? HashFile(Path.Combine(project, prefabPath)) : null;

        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        ConfigureModel(fbxPath);
        GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
        if (model == null) throw new InvalidOperationException("FBX was not imported: " + fbxPath);
        long actualTriangles = CountTriangles(model);
        if (actualTriangles != reportedTriangles)
            throw new InvalidOperationException("Unity triangle count " + actualTriangles + " differs from ImportReport " + reportedTriangles + ".");
        var texturePaths = new Dictionary<string, string>
        {
            { "baseColor", folder + "/" + name + "_BaseColor.png" },
            { "normal", folder + "/" + name + "_Normal.png" },
            { "metallic", folder + "/" + name + "_Metallic.png" },
            { "roughness", folder + "/" + name + "_Roughness.png" },
            { "hdrpMask", folder + "/" + name + "_MaskMap.png" }
        };
        var reportKeys = new Dictionary<string, string>
        { {"baseColor", "base_color"}, {"normal", "normal"}, {"metallic", "metallic"}, {"roughness", "roughness"}, {"hdrpMask", "mask"} };
        int expectedSize = (int?)config["expectedTextureSize"] ?? 8192;
        if (expectedSize < 1 || expectedSize > 8192) throw new ArgumentException("expectedTextureSize must be 1..8192.");
        var textures = new Dictionary<string, Texture2D>();
        var textureReports = new List<object>();
        foreach (var pair in texturePaths)
        {
            TextureImporter importer = AssetImporter.GetAtPath(pair.Value) as TextureImporter;
            if (importer == null) throw new InvalidOperationException("Missing imported texture: " + pair.Value);
            int width, height;
            importer.GetSourceTextureWidthAndHeight(out width, out height);
            JToken report = sourceMaterial["maps"]?[reportKeys[pair.Key]];
            if (report == null || (string)report["file"] != Path.GetFileName(pair.Value) ||
                (int?)report["width"] != width || (int?)report["height"] != height)
                throw new InvalidOperationException("Texture source differs from ImportReport: " + pair.Value);
            if (width < 1 || height < 1 || width > 8192 || height > 8192 ||
                (pair.Key == "baseColor" && (width != expectedSize || height != expectedSize)))
                throw new InvalidOperationException("Unexpected native dimensions: " + pair.Value + " " + width + "x" + height);
            ConfigureTexture(importer, pair.Key);
            Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(pair.Value);
            if (texture == null || texture.width != width || texture.height != height)
                throw new InvalidOperationException("Native texture dimensions were not preserved: " + pair.Value);
            textures.Add(pair.Key, texture);
            textureReports.Add(new { channel = pair.Key, path = pair.Value, sourceWidth = width, sourceHeight = height,
                importedWidth = texture.width, importedHeight = texture.height, format = texture.format.ToString(),
                importer.mipmapEnabled, importer.streamingMipmaps, importer.sRGBTexture, textureType = importer.textureType.ToString() });
        }

        Undo.IncrementCurrentGroup();
        int undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Install and align Tripo winding bridge");
        try
        {
            Material material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null)
            {
                if (File.Exists(Path.Combine(project, materialPath))) throw new InvalidOperationException("Material exists but could not load.");
                material = CreateMaterial(name, textures, (float?)config["normalScale"] ?? (float?)sourceMaterial["normal_scale"] ?? 1f,
                    (bool?)sourceMaterial["double_sided"] ?? false);
                AssetDatabase.CreateAsset(material, materialPath);
            }
            ValidateMaterial(material, textures);
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            bool prefabCreated = prefab == null;
            if (prefabCreated)
            {
                if (File.Exists(Path.Combine(project, prefabPath))) throw new InvalidOperationException("Prefab exists but could not load.");
                CreateStandardPrefab(model, material, name, prefabPath, (string)config["upAxis"] ?? "Y");
                prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            }
            ValidatePrefab(prefab, material, reportedTriangles);
            Bounds nativeBounds = WorldBounds(prefab);
            GuardScene(SceneManager.GetActiveScene());
            if (SceneManager.GetActiveScene().handle != scene.handle) throw new InvalidOperationException("Active scene changed during import.");
            Transform instance = bridge.Find(InstanceName);
            bool instanceCreated = instance == null;
            if (instanceCreated)
            {
                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                Undo.RegisterCreatedObjectUndo(go, "Create Tripo bridge instance");
                go.name = InstanceName;
                go.transform.SetParent(bridge, false);
                instance = go.transform;
            }
            Transform modelYaw = instance.Find("ModelYaw");
            if (modelYaw == null) throw new InvalidOperationException("Standard bridge prefab is missing ModelYaw.");
            Undo.RecordObjects(new UnityEngine.Object[] {instance, modelYaw, instance.gameObject}, "Align Tripo bridge");
            instance.gameObject.SetActive(true);
            instance.localRotation = Quaternion.identity;
            Vector3 parentScale = bridge.lossyScale;
            instance.localScale = new Vector3(scale.x / parentScale.x, scale.y / parentScale.y, scale.z / parentScale.z);
            instance.position = position;
            modelYaw.localPosition = Vector3.zero;
            modelYaw.localRotation = Quaternion.Euler(0, yaw, 0);
            modelYaw.localScale = Vector3.one;
            Record(instance); Record(modelYaw); Record(instance.gameObject);
            ValidateColliders(instance.gameObject);
            if (CountTriangles(instance.gameObject) != actualTriangles) throw new InvalidOperationException("Placed geometry differs from the source.");
            var disabled = new List<object>();
            foreach (Renderer renderer in bridge.GetComponentsInChildren<Renderer>(true))
            {
                if (Within(renderer.transform, instance) || preserved.Any(t => Within(renderer.transform, t))) continue;
                bool wasEnabled = renderer.enabled;
                Undo.RecordObject(renderer, "Disable replaced bridge renderer");
                renderer.enabled = false; Record(renderer);
                disabled.Add(new { path = Hierarchy(renderer.transform), component = renderer.GetType().Name, previouslyEnabled = wasEnabled });
            }
            foreach (Collider collider in bridge.GetComponentsInChildren<Collider>(true))
            {
                if (Within(collider.transform, instance) || preserved.Any(t => Within(collider.transform, t))) continue;
                bool wasEnabled = collider.enabled;
                Undo.RecordObject(collider, "Disable replaced bridge collider");
                collider.enabled = false; Record(collider);
                disabled.Add(new { path = Hierarchy(collider.transform), component = collider.GetType().Name, previouslyEnabled = wasEnabled });
            }
            Physics.SyncTransforms();
            Bounds worldBounds = WorldBounds(instance.gameObject);
            object anchors = DescribeAnchors(config["anchors"] as JArray, instance);
            AssetDatabase.SaveAssets();
            if (TransformFingerprint(bridge) != rootBefore || originalObjects.Any(t => t == null))
                throw new InvalidOperationException("Original bridge root moved or an original scene object was removed.");
            foreach (var pair in preserveBefore)
                if (SubtreeFingerprint(pair.Key) != pair.Value) throw new InvalidOperationException("Preserved bridge subtree changed: " + pair.Key.name);
            if (PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(bridge.gameObject) != sourcePrefabBefore ||
                HashFile(Path.Combine(project, BridgePrefab)) != originalPrefabHash)
                throw new InvalidOperationException("Original Bridge prefab or source connection changed.");
            if (standardPrefabBefore != null && HashFile(Path.Combine(project, prefabPath)) != standardPrefabBefore)
                throw new InvalidOperationException("Existing standard bridge prefab was rewritten during placement.");
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene)) throw new InvalidOperationException("Could not save bridge scene.");
            Undo.CollapseUndoOperations(undoGroup);
            var result = new
            {
                status = "installed", scene = scene.path, sceneSaved = true, configPath, model = (string)sourcePlan["model"],
                sourcePlan = sourcePlanPath, actualTriangles, importReportTriangles = reportedTriangles, geometryReduced = false,
                fbx = fbxPath, material = materialPath, shader = material.shader.name, keywords = material.shaderKeywords,
                wrapperPrefab = prefabPath, prefabCreated, standardPrefabHasScenePlacement = false,
                standardPrefabPreserved = standardPrefabBefore == null || HashFile(Path.Combine(project, prefabPath)) == standardPrefabBefore,
                sourcePrefabPreserved = true, sourcePrefab = BridgePrefab, sourcePrefabHash = originalPrefabHash,
                rootTransformPreserved = true, originalSceneObjectsPreserved = true,
                retainedSubtrees = preserved.Select(Hierarchy).ToArray(), textures = textureReports,
                instance = new { path = Hierarchy(instance), instanceCreated, position = Numbers(instance.position), scale = Numbers(scale), yaw,
                    localPosition = Numbers(instance.localPosition), localScale = Numbers(instance.localScale),
                    transformOrder = "yaw model, scale on world axes, translate pivot", nativeBounds = Describe(nativeBounds),
                    worldBounds = Describe(worldBounds), actualTriangles,
                    meshColliders = instance.GetComponentsInChildren<MeshCollider>(true).Select(c => new
                    { path = Hierarchy(c.transform), c.enabled, c.convex, c.isTrigger, mesh = c.sharedMesh.name,
                        matchesVisualMesh = c.sharedMesh == c.GetComponent<MeshFilter>().sharedMesh }).ToArray() },
                disabledLegacy = disabled, anchors, notes = config["notes"]
            };
            string json = JsonConvert.SerializeObject(result, Formatting.Indented);
            string reportPath = Path.Combine(project, "Temp/TripoBridge-install-report.json");
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath));
            File.WriteAllText(reportPath, json + "\n");
            return json;
        }
        catch { Undo.RevertAllDownToGroup(undoGroup); throw; }
    }

    static void ValidatePlan(JObject plan)
    {
        var steps = plan["steps"] as JArray;
        JToken payload = steps != null && steps.Count == 1 ? steps[0]["payload"] : null;
        if ((bool?)plan["valid"] != true || (int?)plan["candidates"] != 1 || payload == null ||
            (string)plan["model"] != "v3.1-20260211" || (string)payload["model"] != "v3.1-20260211" ||
            (bool?)payload["pbr"] != true || (bool?)payload["texture"] != true || (string)payload["texture_quality"] != "extreme")
            throw new InvalidOperationException("Source model-plan must verify one H3.1 model with texture/PBR=true and extreme textures.");
    }

    static Vector3 Vector(JToken token, string label, bool uniformAllowed = false)
    {
        Vector3 value;
        if (token is JArray && ((JArray)token).Count == 3) value = new Vector3((float)token[0], (float)token[1], (float)token[2]);
        else if (token is JObject && token["x"] != null && token["y"] != null && token["z"] != null)
            value = new Vector3((float)token["x"], (float)token["y"], (float)token["z"]);
        else if (uniformAllowed && token != null && (token.Type == JTokenType.Integer || token.Type == JTokenType.Float))
            value = Vector3.one * (float)token;
        else throw new ArgumentException(label + " must be [x,y,z] or an XYZ object" + (uniformAllowed ? ", or a uniform scalar." : "."));
        foreach (float n in new[] {value.x, value.y, value.z})
            if (float.IsNaN(n) || float.IsInfinity(n)) throw new ArgumentException(label + " must be finite.");
        return value;
    }

    static object DescribeAnchors(JArray anchors, Transform instance)
    {
        if (anchors == null) return null;
        var result = new List<object>();
        foreach (JObject anchor in anchors)
        {
            Vector3 local = Vector(anchor["local"], "anchor.local");
            // Anchors are in the standard prefab's coordinate system before scene scale/yaw.
            Vector3 world = instance.Find("ModelYaw").TransformPoint(local);
            Vector3? target = anchor["targetWorld"] == null ? (Vector3?)null : Vector(anchor["targetWorld"], "anchor.targetWorld");
            result.Add(new { name = (string)anchor["name"], local = Numbers(local), world = Numbers(world),
                targetWorld = target.HasValue ? Numbers(target.Value) : null,
                distanceToTarget = target.HasValue ? (float?)(world - target.Value).magnitude : null, notes = anchor["notes"] });
        }
        return result;
    }

    static bool Within(Transform value, Transform ancestor) { return value == ancestor || value.IsChildOf(ancestor); }
    static string TransformFingerprint(Transform t)
    { return JsonConvert.SerializeObject(new { position = Numbers(t.localPosition), rotation = new[] {t.localRotation.x,t.localRotation.y,t.localRotation.z,t.localRotation.w}, scale = Numbers(t.localScale), t.gameObject.activeSelf }); }
    static string SubtreeFingerprint(Transform root)
    {
        return JsonConvert.SerializeObject(root.GetComponentsInChildren<Transform>(true).Select(t => new
        { id = t.GetInstanceID(), path = Hierarchy(t), state = TransformFingerprint(t),
            renderers = t.GetComponents<Renderer>().Select(r => new { id = r.GetInstanceID(), r.enabled }).ToArray(),
            colliders = t.GetComponents<Collider>().Select(c => new { id = c.GetInstanceID(), c.enabled }).ToArray() }).ToArray());
    }

    static void ConfigureModel(string path)
    {
        var importer = AssetImporter.GetAtPath(path) as ModelImporter;
        if (importer == null) throw new InvalidOperationException("Missing FBX importer: " + path);
        string before = EditorJsonUtility.ToJson(importer);
        importer.importAnimation = false;
        importer.animationType = ModelImporterAnimationType.None;
        importer.importCameras = false;
        importer.importLights = false;
        importer.materialImportMode = ModelImporterMaterialImportMode.None;
        importer.addCollider = false;
        // Keep geometry readable for exact vertex bounds and collider cooking after scene scaling.
        importer.isReadable = true;
        if (before != EditorJsonUtility.ToJson(importer)) importer.SaveAndReimport();
    }

    static void ConfigureTexture(TextureImporter importer, string channel)
    {
        string before = EditorJsonUtility.ToJson(importer);
        importer.textureType = channel == "normal" ? TextureImporterType.NormalMap : TextureImporterType.Default;
        importer.sRGBTexture = channel == "baseColor";
        importer.maxTextureSize = 8192;
        importer.npotScale = TextureImporterNPOTScale.None;
        importer.mipmapEnabled = true;
        importer.streamingMipmaps = true;
        importer.isReadable = false;
        importer.alphaIsTransparency = false;
        importer.textureCompression = TextureImporterCompression.CompressedHQ;
        importer.compressionQuality = 100;
        importer.crunchedCompression = false;
        if (channel == "hdrpMask") importer.alphaSource = TextureImporterAlphaSource.FromInput;
        var platform = importer.GetPlatformTextureSettings("Standalone");
        platform.name = "Standalone";
        platform.overridden = true;
        platform.maxTextureSize = 8192;
        platform.format = TextureImporterFormat.Automatic;
        platform.textureCompression = TextureImporterCompression.CompressedHQ;
        platform.compressionQuality = 100;
        platform.crunchedCompression = false;
        importer.SetPlatformTextureSettings(platform);
        if (before != EditorJsonUtility.ToJson(importer)) importer.SaveAndReimport();
    }

    static Material CreateMaterial(string name, Dictionary<string, Texture2D> textures, float normalScale, bool doubleSided)
    {
        Shader shader = Shader.Find("HDRP/Lit");
        if (shader == null) throw new InvalidOperationException("HDRP/Lit is unavailable.");
        var material = new Material(shader) { name = name + "_HDRP", enableInstancing = true };
        material.SetColor("_BaseColor", Color.white);
        material.SetTexture("_BaseColorMap", textures["baseColor"]);
        material.SetTexture("_NormalMap", textures["normal"]);
        material.SetTexture("_MaskMap", textures["hdrpMask"]);
        Set(material, "_SurfaceType", 0);
        Set(material, "_AlphaCutoffEnable", 0);
        Set(material, "_NormalMapSpace", 0);
        Set(material, "_NormalScale", normalScale);
        Set(material, "_Metallic", 1);
        Set(material, "_Smoothness", 1);
        foreach (string channel in new[] { "_MetallicRemap", "_SmoothnessRemap", "_AORemap" })
        { Set(material, channel + "Min", 0); Set(material, channel + "Max", 1); }
        Set(material, "_DoubleSidedEnable", doubleSided ? 1 : 0);
        HDMaterial.ValidateMaterial(material);
        return material;
    }

    static void ValidateMaterial(Material material, Dictionary<string, Texture2D> textures)
    {
        if (material.shader == null || material.shader.name != "HDRP/Lit" ||
            material.GetTexture("_BaseColorMap") != textures["baseColor"] ||
            material.GetTexture("_NormalMap") != textures["normal"] || material.GetTexture("_MaskMap") != textures["hdrpMask"] ||
            !material.IsKeywordEnabled("_MASKMAP") || !material.IsKeywordEnabled("_NORMALMAP"))
            throw new InvalidOperationException("Existing bridge material does not match the validated HDRP PBR texture set.");
    }

    static void CreateStandardPrefab(GameObject model, Material material, string name, string path, string up)
    {
        Scene preview = EditorSceneManager.NewPreviewScene();
        try
        {
            var root = new GameObject(name);
            SceneManager.MoveGameObjectToScene(root, preview);
            var yaw = new GameObject("ModelYaw");
            SceneManager.MoveGameObjectToScene(yaw, preview);
            yaw.transform.SetParent(root.transform, false);
            var visual = (GameObject)PrefabUtility.InstantiatePrefab(model, preview);
            visual.name = "Visual";
            visual.transform.SetParent(yaw.transform, false);
            visual.transform.localRotation = UpRotation(up) * visual.transform.localRotation;
            Bounds nativeBounds = WorldBounds(visual);
            visual.transform.localPosition += new Vector3(-nativeBounds.center.x, -nativeBounds.min.y, -nativeBounds.center.z);
            foreach (Renderer renderer in visual.GetComponentsInChildren<Renderer>(true))
            {
                renderer.sharedMaterials = Enumerable.Repeat(material, Mathf.Max(1, renderer.sharedMaterials.Length)).ToArray();
                PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
            }
            foreach (MeshFilter filter in visual.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh == null) throw new InvalidOperationException("Source mesh is missing.");
                if (filter.GetComponents<Collider>().Length != 0) throw new InvalidOperationException("Unexpected source collider.");
                var collider = filter.gameObject.AddComponent<MeshCollider>();
                collider.convex = false;
                collider.isTrigger = false;
                collider.sharedMesh = filter.sharedMesh;
            }
            Record(visual.transform);
            ValidateColliders(root);
            bool saved;
            PrefabUtility.SaveAsPrefabAsset(root, path, out saved);
            if (!saved) throw new InvalidOperationException("Failed to save independent bridge prefab.");
        }
        finally { EditorSceneManager.ClosePreviewScene(preview); }
    }

    static void ValidatePrefab(GameObject prefab, Material material, long triangles)
    {
        if (prefab == null || prefab.transform.parent != null || prefab.transform.localPosition != Vector3.zero ||
            prefab.transform.localRotation != Quaternion.identity || prefab.transform.localScale != Vector3.one ||
            prefab.transform.Find("ModelYaw/Visual") == null || CountTriangles(prefab) != triangles)
            throw new InvalidOperationException("Standard prefab is missing or does not match the imported model.");
        foreach (Renderer renderer in prefab.GetComponentsInChildren<Renderer>(true))
            if (!renderer.enabled || renderer.sharedMaterials.Any(m => m != material))
                throw new InvalidOperationException("Standard prefab has unexpected renderer state/materials.");
        ValidateColliders(prefab);
    }

    static long CountTriangles(GameObject root)
    {
        if (root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length != 0)
            throw new InvalidOperationException("Bridge must contain static mesh geometry only.");
        long count = 0;
        foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(true))
        {
            Mesh mesh = filter.sharedMesh;
            if (mesh == null) throw new InvalidOperationException("Missing MeshFilter mesh.");
            for (int i = 0; i < mesh.subMeshCount; i++)
            {
                uint indices = mesh.GetIndexCount(i);
                if (mesh.GetTopology(i) != MeshTopology.Triangles || indices % 3 != 0)
                    throw new InvalidOperationException("Imported mesh is not triangle-list topology.");
                count += indices / 3;
            }
        }
        if (count <= 0) throw new InvalidOperationException("Bridge has no triangles.");
        return count;
    }

    static void ValidateColliders(GameObject root)
    {
        MeshFilter[] filters = root.GetComponentsInChildren<MeshFilter>(true);
        Collider[] all = root.GetComponentsInChildren<Collider>(true);
        if (all.Length != filters.Length) throw new InvalidOperationException("Expected one MeshCollider per visual mesh.");
        foreach (MeshFilter filter in filters)
        {
            MeshCollider collider = filter.GetComponent<MeshCollider>();
            if (collider == null || !collider.enabled || collider.convex || collider.isTrigger || collider.sharedMesh != filter.sharedMesh)
                throw new InvalidOperationException("Bridge collider does not match its actual visual mesh.");
        }
    }

    static Bounds WorldBounds(GameObject root)
    {
        // The transformed mesh vertices give exact bounds even at a nonzero yaw.
        // Renderer.bounds can include empty corners from its rotated local AABB.
        bool initialized = false;
        Bounds result = default(Bounds);
        foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(true))
        {
            Mesh mesh = filter.sharedMesh;
            if (mesh == null || !mesh.isReadable)
                throw new InvalidOperationException("Readable mesh required for exact bridge bounds: " + Hierarchy(filter.transform));
            foreach (Vector3 localVertex in mesh.vertices)
            {
                Vector3 worldVertex = filter.transform.TransformPoint(localVertex);
                if (!initialized) { result = new Bounds(worldVertex, Vector3.zero); initialized = true; }
                else result.Encapsulate(worldVertex);
            }
        }
        if (!initialized) throw new InvalidOperationException("Bridge has no mesh vertices.");
        return result;
    }

    static Quaternion UpRotation(string value)
    {
        switch (value.ToUpperInvariant())
        {
            case "Y": return Quaternion.identity;
            case "Z": return Quaternion.Euler(-90, 0, 0);
            case "-Y": return Quaternion.Euler(180, 0, 0);
            case "-Z": return Quaternion.Euler(90, 0, 0);
            default: throw new ArgumentException("upAxis must be Y, Z, -Y or -Z.");
        }
    }

    static void GuardScene(Scene scene)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode || !scene.IsValid() || scene.path != MainScene)
            throw new InvalidOperationException("Open TianNanGe_Main in Edit Mode before installing the bridge.");
    }
    static void RequireUnrotatedPositive(Transform root)
    {
        if (Quaternion.Angle(root.rotation, Quaternion.identity) > .01f)
            throw new InvalidOperationException("Support root must have zero world rotation; specify model yaw in config: " + Hierarchy(root));
        Positive(root.lossyScale, "Support scale");
    }
    static string AssetFolder(string path, string project)
    {
        string absolute = Path.GetFullPath(Path.Combine(project, path));
        string allowed = Path.GetFullPath(Path.Combine(Application.dataPath, "TianNanGe/Tripo")) + Path.DirectorySeparatorChar;
        if (!absolute.StartsWith(allowed, StringComparison.Ordinal) || !Directory.Exists(absolute))
            throw new ArgumentException("assetFolder must be an existing child of Assets/TianNanGe/Tripo.");
        return absolute.Substring(project.Length + 1).Replace('\\', '/');
    }
    static JObject ReadObject(string path)
    { if (!File.Exists(path)) throw new FileNotFoundException("Missing required source/config JSON.", path); return JObject.Parse(File.ReadAllText(path)); }
    static Transform Find(Scene scene, string path)
    {
        string[] pieces = path.Split('/');
        GameObject root = scene.GetRootGameObjects().SingleOrDefault(g => g.name == pieces[0]);
        return root == null ? null : root.transform.Find(string.Join("/", pieces.Skip(1)));
    }
    static string Segment(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "." || value == ".." || value.Contains("/") || value.Contains("\\") ||
            value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Name must be one nonempty filename/hierarchy segment.");
        return value;
    }
    static float Number(JObject item, string key)
    {
        if (item[key] == null) throw new ArgumentException("Missing placement field " + key);
        float value = (float)item[key];
        if (float.IsNaN(value) || float.IsInfinity(value)) throw new ArgumentException(key + " must be finite.");
        return value;
    }
    static void Positive(Vector3 v, string label)
    { foreach (float n in new[] { v.x, v.y, v.z }) if (n <= .00001f || float.IsNaN(n) || float.IsInfinity(n)) throw new ArgumentException(label + " must be finite and positive."); }
    static string Hierarchy(Transform t)
    { string result = t.name; while (t.parent != null) { t = t.parent; result = t.name + "/" + result; } return result; }
    static void Record(UnityEngine.Object o)
    { if (PrefabUtility.IsPartOfPrefabInstance(o)) PrefabUtility.RecordPrefabInstancePropertyModifications(o); EditorUtility.SetDirty(o); }
    static void Set(Material material, string key, float value) { if (material.HasProperty(key)) material.SetFloat(key, value); }
    static string HashFile(string path)
    { using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant(); }
    static float[] Numbers(Vector3 v) { return new[] { v.x, v.y, v.z }; }
    static object Describe(Bounds b) { return new { center = Numbers(b.center), size = Numbers(b.size), min = Numbers(b.min), max = Numbers(b.max) }; }
}
