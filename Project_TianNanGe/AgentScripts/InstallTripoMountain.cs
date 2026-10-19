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
/// Explicit Pipeline run_script entry: InstallTripoMountain.Install.
/// Reads ProjectRoot/Temp/tripo-mountain-install.json; writing this script does not run it.
/// The standard prefab has native model proportions and no scene placement baked into it.
/// Existing scene instances can be fitted again without rewriting that prefab or Mountains.prefab.
///
/// Configuration (dimensions and positions are WORLD coordinates of actual mesh vertices,
/// rather than the conservative Renderer.bounds box):
/// {
///   "assetFolder":"Assets/TianNanGe/Tripo/SnowMountain09", "name":"SnowMountain_P20_8K",
///   "sourcePlan":"../SourceAssets/Tripo/SnowMountain09/model-plan.json", "upAxis":"Y",
///   "expectedTextureSize":8192, "triangleTolerance":1000,
///   "instances":[{"name":"Tripo_Main", "supportRoot":"TianNanGe_Blockout/Mountains/Mountain_Main",
///     "centerX":4.1,"centerZ":10.5,"width":27.4,"height":18.1,"depth":27.0,"topY":-0.53,"yaw":0}]
/// }
/// A missing supportRoot is created as a direct child of Mountains. Only Rock/SnowCap
/// Renderer/Collider components below that support are disabled; their objects remain.
/// New non-convex MeshColliders use the exact imported visual meshes. No source prefab is applied.
/// triangleTolerance defaults to 0 (strict 25,000); the explicitly approved allowance
/// is at most 1,000. Source generation must still request face_limit=25000, quad=false.
/// </summary>
public static class InstallTripoMountain
{
    const string MainScene = "Assets/TianNanGe/Scenes/TianNanGe_Main.unity";
    const string MountainsPath = "TianNanGe_Blockout/Mountains";
    const string MountainsPrefab = "Assets/TianNanGe/Prefabs/Environment/Mountains.prefab";
    const string DefaultFolder = "Assets/TianNanGe/Tripo/SnowMountain09";
    const string DefaultName = "SnowMountain_P20_8K";
    const long RequestedTriangleLimit = 25000;
    const long MaximumApprovedTolerance = 1000;

    sealed class Placement
    {
        public string name, supportPath;
        public Vector3 size;
        public float x, z, top, yaw;
    }

    public static string Install()
    {
        Scene scene = SceneManager.GetActiveScene();
        GuardScene(scene);
        string project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string configPath = Path.Combine(project, "Temp/tripo-mountain-install.json");
        JObject config = ReadObject(configPath);
        long triangleTolerance = ReadTriangleTolerance(config);
        long approvedTriangleLimit = RequestedTriangleLimit + triangleTolerance;
        string folder = AssetFolder((string)config["assetFolder"] ?? DefaultFolder, project);
        string name = Segment((string)config["name"] ?? DefaultName);
        string fbxPath = folder + "/" + name + ".fbx";
        string importPath = folder + "/" + name + "_ImportReport.json";
        string materialPath = folder + "/" + name + "_HDRP.mat";
        string prefabPath = folder + "/" + name + ".prefab";
        string planPath = Path.GetFullPath(Path.Combine(project,
            (string)config["sourcePlan"] ?? "../SourceAssets/Tripo/SnowMountain09/model-plan.json"));
        JObject sourcePlan = ReadObject(planPath);
        ValidatePlan(sourcePlan);
        JObject imported = ReadObject(Path.Combine(project, importPath));
        long reportedTriangles = (long?)imported["geometry"]?["triangles"] ?? -1;
        if (reportedTriangles <= 0 || reportedTriangles > approvedTriangleLimit)
            throw new InvalidOperationException("ImportReport geometry.triangles must be 1.." + approvedTriangleLimit + ".");
        if ((string)imported["fbx"] != name + ".fbx")
            throw new InvalidOperationException("ImportReport names a different FBX.");
        if ((bool?)imported["images_resized"] != false || (bool?)imported["uvs_repacked"] != false)
            throw new InvalidOperationException("ImportReport must confirm native image dimensions and preserved UVs.");
        var materialReports = imported["materials"] as JArray;
        if (materialReports == null || materialReports.Count != 1)
            throw new InvalidOperationException("This single-texture-set installer requires exactly one imported material.");
        JObject sourceMaterial = (JObject)materialReports[0];
        var placements = ReadPlacements(config);
        Transform mountains = Find(scene, MountainsPath);
        if (mountains == null || !mountains.gameObject.activeInHierarchy)
            throw new InvalidOperationException("Active Mountains scene root is required.");
        RequireUnrotatedPositive(mountains);
        string mountainsPrefabBefore = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(mountains.gameObject);
        string originalPrefabHash = HashFile(Path.Combine(project, MountainsPrefab));

        // Validate names, existing objects, and source documents before asset or scene changes.
        foreach (Placement p in placements)
        {
            Transform support = Find(scene, p.supportPath);
            if (support == null) continue;
            RequireUnrotatedPositive(support);
            if (!support.gameObject.activeInHierarchy)
                throw new InvalidOperationException("Inactive support: " + p.supportPath);
            Transform existing = support.Find(p.name);
            if (existing != null && PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(existing.gameObject) != prefabPath)
                throw new InvalidOperationException("Instance name is occupied by an unrelated object: " + p.supportPath + "/" + p.name);
            foreach (Transform child in support)
                if (child != existing && PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(child.gameObject) == prefabPath)
                    throw new InvalidOperationException("This support already has a Tripo mountain with another instance name: " + child.name);
        }

        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        ConfigureModel(fbxPath);
        GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
        if (model == null) throw new InvalidOperationException("FBX was not imported: " + fbxPath);
        long modelTriangles = CountTriangles(model, approvedTriangleLimit);
        if (modelTriangles != reportedTriangles)
            throw new InvalidOperationException("Unity triangle count " + modelTriangles + " differs from ImportReport " + reportedTriangles + ".");
        var texturePaths = new Dictionary<string, string>
        {
            { "baseColor", folder + "/" + name + "_BaseColor.png" },
            { "normal", folder + "/" + name + "_Normal.png" },
            { "metallic", folder + "/" + name + "_Metallic.png" },
            { "roughness", folder + "/" + name + "_Roughness.png" },
            { "hdrpMask", folder + "/" + name + "_MaskMap.png" }
        };
        var reportKeys = new Dictionary<string, string>
        {
            {"baseColor", "base_color"}, {"normal", "normal"}, {"metallic", "metallic"},
            {"roughness", "roughness"}, {"hdrpMask", "mask"}
        };
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
            textureReports.Add(new { channel = pair.Key, path = pair.Value, sourceWidth = width,
                sourceHeight = height, importedWidth = texture.width, importedHeight = texture.height,
                format = texture.format.ToString(), importer.mipmapEnabled, importer.streamingMipmaps,
                importer.sRGBTexture, textureType = importer.textureType.ToString() });
        }

        Undo.IncrementCurrentGroup();
        int undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Install and place Tripo snow mountain");
        var instanceReports = new List<object>();
        bool prefabCreated = false;
        try
        {
            Material material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null)
            {
                if (File.Exists(Path.Combine(project, materialPath)))
                    throw new InvalidOperationException("Material asset exists but could not load: " + materialPath);
                material = CreateMaterial(name, textures, (float?)config["normalScale"] ?? (float?)sourceMaterial["normal_scale"] ?? 1f,
                    (bool?)sourceMaterial["double_sided"] ?? false);
                AssetDatabase.CreateAsset(material, materialPath);
            }
            ValidateMaterial(material, textures);
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
            {
                if (File.Exists(Path.Combine(project, prefabPath)))
                    throw new InvalidOperationException("Prefab exists but could not load: " + prefabPath);
                CreateStandardPrefab(model, material, name, prefabPath, (string)config["upAxis"] ?? "Y");
                prefabCreated = true;
                prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            }
            ValidatePrefab(prefab, material, reportedTriangles, approvedTriangleLimit);
            GuardScene(SceneManager.GetActiveScene());
            if (SceneManager.GetActiveScene().handle != scene.handle)
                throw new InvalidOperationException("Active scene changed during asset preparation.");
            foreach (Placement p in placements)
            {
                Transform support = Find(scene, p.supportPath);
                bool supportCreated = support == null;
                if (supportCreated)
                {
                    var supportObject = new GameObject(p.supportPath.Substring(MountainsPath.Length + 1));
                    Undo.RegisterCreatedObjectUndo(supportObject, "Create Tripo mountain support root");
                    supportObject.transform.SetParent(mountains, false);
                    support = supportObject.transform;
                }
                RequireUnrotatedPositive(support);
                Transform instance = support.Find(p.name);
                bool instanceCreated = instance == null;
                if (instanceCreated)
                {
                    var instanceObject = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                    Undo.RegisterCreatedObjectUndo(instanceObject, "Create Tripo mountain instance");
                    instanceObject.name = p.name;
                    instanceObject.transform.SetParent(support, false);
                    instance = instanceObject.transform;
                }
                var yawRoot = instance.Find("ModelYaw");
                if (yawRoot == null) throw new InvalidOperationException("Standard prefab is missing ModelYaw.");
                Undo.RecordObjects(new UnityEngine.Object[] { instance, yawRoot, instance.gameObject }, "Fit Tripo mountain world bounds");
                instance.gameObject.SetActive(true);
                instance.localPosition = Vector3.zero;
                instance.localRotation = Quaternion.identity;
                Vector3 parentScale = support.lossyScale;
                instance.localScale = new Vector3(1f / parentScale.x, 1f / parentScale.y, 1f / parentScale.z);
                yawRoot.localPosition = Vector3.zero;
                yawRoot.localRotation = Quaternion.Euler(0, p.yaw, 0);
                yawRoot.localScale = Vector3.one;
                Bounds unscaled = WorldBounds(instance.gameObject);
                Positive(unscaled.size, "Unscaled mountain dimensions");
                instance.localScale = new Vector3(p.size.x / unscaled.size.x / parentScale.x,
                    p.size.y / unscaled.size.y / parentScale.y, p.size.z / unscaled.size.z / parentScale.z);
                Bounds fitted = WorldBounds(instance.gameObject);
                instance.position += new Vector3(p.x - fitted.center.x, p.top - fitted.max.y, p.z - fitted.center.z);
                fitted = WorldBounds(instance.gameObject);
                if ((fitted.size - p.size).sqrMagnitude > 0.0001f ||
                    Mathf.Abs(fitted.max.y - p.top) > 0.002f || Mathf.Abs(fitted.center.x - p.x) > 0.002f ||
                    Mathf.Abs(fitted.center.z - p.z) > 0.002f)
                    throw new InvalidOperationException("World bounds fitting failed for " + p.name);
                Record(instance); Record(yawRoot); Record(instance.gameObject);
                long triangles = CountTriangles(instance.gameObject, approvedTriangleLimit);
                if (triangles != reportedTriangles) throw new InvalidOperationException("Placed mesh count differs from source.");
                ValidateColliders(instance.gameObject);

                var disabled = new List<object>();
                foreach (string oldName in new[] { "Rock", "SnowCap" })
                {
                    Transform old = support.Find(oldName);
                    if (old == null) continue;
                    foreach (Renderer renderer in old.GetComponentsInChildren<Renderer>(true))
                    {
                        bool wasEnabled = renderer.enabled;
                        Undo.RecordObject(renderer, "Disable replaced mountain visual");
                        renderer.enabled = false;
                        Record(renderer);
                        disabled.Add(new { path = Hierarchy(renderer.transform), component = "Renderer", previouslyEnabled = wasEnabled });
                    }
                    foreach (Collider collider in old.GetComponentsInChildren<Collider>(true))
                    {
                        bool wasEnabled = collider.enabled;
                        Undo.RecordObject(collider, "Disable replaced mountain collider");
                        collider.enabled = false;
                        Record(collider);
                        disabled.Add(new { path = Hierarchy(collider.transform), component = "Collider", previouslyEnabled = wasEnabled });
                    }
                }
                instanceReports.Add(new { name = p.name, supportRoot = p.supportPath, supportCreated, instanceCreated,
                    triangles, requestedTriangleLimit = RequestedTriangleLimit, triangleTolerance,
                    maxTriangles = approvedTriangleLimit, requestedWorldSize = Numbers(p.size),
                    centerX = p.x, centerZ = p.z, topY = p.top, yaw = p.yaw, worldBounds = Describe(fitted),
                    localPosition = Numbers(instance.localPosition), localScale = Numbers(instance.localScale),
                    colliders = instance.GetComponentsInChildren<MeshCollider>(true).Length,
                    disabledLegacyComponents = disabled, sourcePrefab = prefabPath });
            }
            Physics.SyncTransforms();
            AssetDatabase.SaveAssets();
            if (PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(mountains.gameObject) != mountainsPrefabBefore ||
                HashFile(Path.Combine(project, MountainsPrefab)) != originalPrefabHash)
                throw new InvalidOperationException("The original Mountains prefab or its connection changed.");
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene)) throw new InvalidOperationException("Could not save scene.");
            Undo.CollapseUndoOperations(undoGroup);
            var result = new
            {
                status = "installed", scene = scene.path, sceneSaved = true, configPath,
                model = (string)sourcePlan["model"], sourceQuad = false, sourceFaceLimit = RequestedTriangleLimit,
                requestedTriangleLimit = RequestedTriangleLimit, triangleTolerance,
                actualTriangles = modelTriangles, importReportTriangles = reportedTriangles, maxTriangles = approvedTriangleLimit,
                fbx = fbxPath, material = materialPath, shader = material.shader.name, keywords = material.shaderKeywords,
                wrapperPrefab = prefabPath, prefabCreated, standardPrefabHasScenePlacement = false,
                originalMountainsPrefabUnchanged = true, textures = textureReports, instances = instanceReports
            };
            string json = JsonConvert.SerializeObject(result, Formatting.Indented);
            string reportPath = Path.Combine(project, "Temp/TripoMountain-install-report.json");
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath));
            File.WriteAllText(reportPath, json + "\n");
            return json;
        }
        catch
        {
            // Revert the scene operation; any already-created standalone source assets remain reviewable.
            Undo.RevertAllDownToGroup(undoGroup);
            throw;
        }
    }

    static List<Placement> ReadPlacements(JObject config)
    {
        var array = config["instances"] as JArray;
        if (array == null || array.Count == 0) throw new ArgumentException("instances must contain at least one placement.");
        var result = new List<Placement>();
        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (JObject item in array)
        {
            string path = (string)item["supportRoot"];
            if (string.IsNullOrWhiteSpace(path) || !path.StartsWith(MountainsPath + "/", StringComparison.Ordinal))
                throw new ArgumentException("supportRoot must be a direct child of " + MountainsPath);
            Segment(path.Substring(MountainsPath.Length + 1));
            string name = Segment((string)item["name"]);
            if (name == "Rock" || name == "SnowCap") throw new ArgumentException("Instance name cannot be Rock or SnowCap.");
            if (!targets.Add(path)) throw new ArgumentException("Each supportRoot may appear only once per install config.");
            var p = new Placement { name = name, supportPath = path, x = Number(item, "centerX"), z = Number(item, "centerZ"),
                top = Number(item, "topY"), yaw = item["yaw"] == null ? 0 : Number(item, "yaw"),
                size = new Vector3(Number(item, "width"), Number(item, "height"), Number(item, "depth")) };
            Positive(p.size, "Requested world dimensions");
            result.Add(p);
        }
        return result;
    }

    static void ValidatePlan(JObject plan)
    {
        var steps = plan["steps"] as JArray;
        JToken payload = steps != null && steps.Count == 1 ? steps[0]["payload"] : null;
        if ((bool?)plan["valid"] != true || (int?)plan["candidates"] != 1 || payload == null ||
            (string)plan["model"] != "P2-20260801" || (string)payload["model"] != "P2-20260801" ||
            payload["quad"] == null || payload["quad"].Type != JTokenType.Boolean || (bool)payload["quad"] ||
            (int?)payload["face_limit"] != RequestedTriangleLimit || (bool?)payload["pbr"] != true ||
            (bool?)payload["texture"] != true)
            throw new InvalidOperationException("Source model-plan must verify one P2-20260801 generation, face_limit=25000, quad=false, texture/PBR=true.");
    }

    static long ReadTriangleTolerance(JObject config)
    {
        JToken token = config["triangleTolerance"];
        if (token == null) return 0;
        if (token.Type != JTokenType.Integer)
            throw new ArgumentException("triangleTolerance must be an explicit integer from 0 to 1000.");
        long tolerance = (long)token;
        if (tolerance < 0 || tolerance > MaximumApprovedTolerance)
            throw new ArgumentException("triangleTolerance exceeds the approved range 0..1000.");
        return tolerance;
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
        // Readable meshes are required for reliable runtime cooking under the nonuniform fitted transforms.
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
            throw new InvalidOperationException("Existing mountain material does not match the validated HDRP PBR texture set.");
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
            if (!saved) throw new InvalidOperationException("Failed to save independent mountain prefab.");
        }
        finally { EditorSceneManager.ClosePreviewScene(preview); }
    }

    static void ValidatePrefab(GameObject prefab, Material material, long triangles, long approvedTriangleLimit)
    {
        if (prefab == null || prefab.transform.parent != null || prefab.transform.localPosition != Vector3.zero ||
            prefab.transform.localRotation != Quaternion.identity || prefab.transform.localScale != Vector3.one ||
            prefab.transform.Find("ModelYaw/Visual") == null || CountTriangles(prefab, approvedTriangleLimit) != triangles)
            throw new InvalidOperationException("Standard prefab is missing or does not match the imported model.");
        foreach (Renderer renderer in prefab.GetComponentsInChildren<Renderer>(true))
            if (!renderer.enabled || renderer.sharedMaterials.Any(m => m != material))
                throw new InvalidOperationException("Standard prefab has unexpected renderer state/materials.");
        ValidateColliders(prefab);
    }

    static long CountTriangles(GameObject root, long approvedTriangleLimit)
    {
        if (root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length != 0)
            throw new InvalidOperationException("Mountain must contain static mesh geometry only.");
        long count = 0;
        foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(true))
        {
            Mesh mesh = filter.sharedMesh;
            if (mesh == null) throw new InvalidOperationException("Missing MeshFilter mesh.");
            for (int i = 0; i < mesh.subMeshCount; i++)
            {
                uint indices = mesh.GetIndexCount(i);
                if (mesh.GetTopology(i) != MeshTopology.Triangles || indices % 3 != 0)
                    throw new InvalidOperationException("Imported mesh is not triangle-list topology (quad=false required).");
                count += indices / 3;
            }
        }
        if (count <= 0 || count > approvedTriangleLimit)
            throw new InvalidOperationException("Mountain has " + count + " triangles; allowed 1.." + approvedTriangleLimit + ".");
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
                throw new InvalidOperationException("Mountain collider does not match its actual visual mesh.");
        }
    }

    static Bounds WorldBounds(GameObject root)
    {
        // Renderer.bounds rotates the mesh's local AABB, which contains empty
        // corners and can grow well beyond the actual rock at nonzero yaw.
        // Fit actual transformed vertices so requested world dimensions remain exact.
        bool initialized = false;
        Bounds result = default(Bounds);
        foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(true))
        {
            Mesh mesh = filter.sharedMesh;
            if (mesh == null || !mesh.isReadable)
                throw new InvalidOperationException("Readable mesh required for exact mountain bounds: " + Hierarchy(filter.transform));
            foreach (Vector3 localVertex in mesh.vertices)
            {
                Vector3 worldVertex = filter.transform.TransformPoint(localVertex);
                if (!initialized) { result = new Bounds(worldVertex, Vector3.zero); initialized = true; }
                else result.Encapsulate(worldVertex);
            }
        }
        if (!initialized) throw new InvalidOperationException("Mountain has no mesh vertices.");
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
            throw new InvalidOperationException("Open TianNanGe_Main in Edit Mode before installing the mountain.");
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
