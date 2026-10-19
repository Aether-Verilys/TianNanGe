using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;

/// <summary>
/// Installs exactly one Tripo building into the active TianNanGe_Main scene.
/// This file lives outside Assets and is invoked explicitly with Pipeline eval_file:
///   file=AgentScripts/InstallTripoBuilding.cs
/// Writing or reading this file does not execute it.
///
/// Read config from EditorPrefs["TianNanGe.Tripo.InstallConfig"] (a JSON file path),
/// or, when the preference is empty, from ProjectRoot/Temp/tripo-install.json.
/// Example (width/height/depth are WORLD dimensions; omit to use the old visual bounds):
/// {
///   "assetFolder": "Assets/TianNanGe/Tripo/MainHall02",
///   "name": "MainHall_H31_8K",
///   "target": {
///     "hierarchyPath": "TianNanGe_Blockout/Architecture/MainHall_Walkable",
///     "hideChild": "Visual", "width": 10.4, "height": 6.488, "depth": 8.648,
///     "yaw": 0
///   },
///   "upAxis": "auto", "expectedTextureSize": 8192
/// }
/// Target settings may also be top-level fields. Optional explicit asset paths
/// (relative to assetFolder): fbx, baseColor, normal, metallic, roughness, hdrpMask.
/// Otherwise each file is found by its name; ambiguous matches are rejected.
/// expectedTextureSize validates BaseColor. Other PBR maps retain their native
/// sizes (Tripo extreme commonly returns 8K BaseColor with 4K PBR maps).
/// upAxis: auto, Y, Z, -Y, or -Z. Auto uses a grounded-bounds heuristic and
/// defaults to Y without forcing the old MainPavilion -90-degree correction.
/// The model is fit on each axis to the desired bounds after yaw is applied.
/// Only the old Visual/Proxy is disabled. Its objects, the parent prefab, and
/// the independent Collision tree are retained. No ApplyPrefabInstance is used.
/// </summary>
public static class InstallTripoBuilding
{
    const string MainScene = "Assets/TianNanGe/Scenes/TianNanGe_Main.unity";
    const string ConfigPreference = "TianNanGe.Tripo.InstallConfig";
    static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".tga", ".tif", ".tiff", ".exr", ".bmp" };

    public static string Install()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Install a Tripo building in Edit Mode.");
        var scene = SceneManager.GetActiveScene();
        if (scene.path != MainScene)
            throw new InvalidOperationException("Open " + MainScene + " before installing a building.");

        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string configPath = EditorPrefs.GetString(ConfigPreference, "");
        if (string.IsNullOrWhiteSpace(configPath)) configPath = Path.Combine(projectRoot, "Temp/tripo-install.json");
        else if (!Path.IsPathRooted(configPath)) configPath = Path.Combine(projectRoot, configPath);
        configPath = Path.GetFullPath(configPath);
        if (!File.Exists(configPath)) throw new FileNotFoundException("Missing Tripo install config.", configPath);
        JObject config = JObject.Parse(File.ReadAllText(configPath));
        JObject targetConfig = config["target"] as JObject;

        string assetFolder = NormalizeAssetFolder(RequiredString(config, "assetFolder"), projectRoot);
        string name = RequiredString(config, "name");
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains("/") || name.Contains("\\"))
            throw new ArgumentException("name must be one filename and hierarchy segment.");
        string hierarchyPath = TargetString(config, targetConfig, "hierarchyPath", "targetHierarchyPath");
        if (string.IsNullOrEmpty(hierarchyPath) && config["target"] != null && config["target"].Type == JTokenType.String)
            hierarchyPath = (string)config["target"];
        if (string.IsNullOrEmpty(hierarchyPath) || !hierarchyPath.StartsWith("TianNanGe_Blockout/Architecture/", StringComparison.Ordinal))
            throw new ArgumentException("hierarchyPath must identify one building under TianNanGe_Blockout/Architecture.");
        Transform target = FindSceneTransform(scene, hierarchyPath);
        if (target == null) throw new InvalidOperationException("Building not found in active scene: " + hierarchyPath);
        if (!target.gameObject.activeInHierarchy) throw new InvalidOperationException("Target building is inactive: " + hierarchyPath);
        if (target.Find(name) != null) throw new InvalidOperationException("This building is already installed: " + hierarchyPath + "/" + name);

        string hideName = TargetString(config, targetConfig, "hideChild", null);
        if (string.IsNullOrEmpty(hideName)) hideName = target.Find("Visual") != null ? "Visual" : "Proxy_ReplaceWithTripo";
        if (hideName != "Visual" && hideName != "Proxy_ReplaceWithTripo")
            throw new ArgumentException("hideChild must be Visual or Proxy_ReplaceWithTripo.");
        Transform oldVisual = target.Find(hideName);
        if (oldVisual == null || !oldVisual.gameObject.activeSelf)
            throw new InvalidOperationException("Expected active original visual: " + hierarchyPath + "/" + hideName);
        if (oldVisual.GetComponentsInChildren<Collider>(true).Length != 0)
            throw new InvalidOperationException("The selected visual contains colliders; refusing to disable a collision hierarchy.");
        Bounds oldBounds = WorldBounds(oldVisual.gameObject);
        var oldPartBounds = oldVisual.GetComponentsInChildren<Renderer>(true).Select(r => new
        {
            path = HierarchyPath(r.transform), active = r.gameObject.activeInHierarchy,
            enabled = r.enabled, worldBounds = Describe(r.bounds)
        }).ToArray();
        Vector3 targetSize = new Vector3(
            TargetFloat(config, targetConfig, "width", "targetWidth", oldBounds.size.x),
            TargetFloat(config, targetConfig, "height", "targetHeight", oldBounds.size.y),
            TargetFloat(config, targetConfig, "depth", "targetDepth", oldBounds.size.z));
        RequirePositive(targetSize, "Target dimensions");
        float yaw = TargetFloat(config, targetConfig, "yaw", null, 0f);
        if (float.IsNaN(yaw) || float.IsInfinity(yaw)) throw new ArgumentException("yaw must be finite.");
        // Axis-wise fitting has unambiguous world dimensions for the scene's unrotated roots.
        // Refuse a rotated target rather than silently producing a wrong world-space fit.
        if (Quaternion.Angle(target.rotation, Quaternion.identity) > .01f)
            throw new InvalidOperationException("Target root must have zero world rotation; apply model yaw through the config.");
        RequirePositive(target.lossyScale, "Target root scale");

        var files = Directory.GetFiles(ToAbsolute(assetFolder, projectRoot), "*", SearchOption.AllDirectories)
            .Select(p => ToAssetPath(p, projectRoot)).ToArray();
        string fbx = FindFile(config, "fbx", assetFolder, files, p => Path.GetExtension(p).Equals(".fbx", StringComparison.OrdinalIgnoreCase));
        var texturePaths = new Dictionary<string, string>
        {
            { "baseColor", FindTexture(config, "baseColor", assetFolder, files, "basecolor", "albedo", "diffuse") },
            { "normal", FindTexture(config, "normal", assetFolder, files, "normal") },
            { "metallic", FindTexture(config, "metallic", assetFolder, files, "metallic", "metalness") },
            { "roughness", FindTexture(config, "roughness", assetFolder, files, "roughness") },
            { "hdrpMask", FindTexture(config, "hdrpMask", assetFolder, files, "hdrpmask", "maskmap", "mask") }
        };
        if (texturePaths.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != texturePaths.Count)
            throw new InvalidOperationException("Each PBR channel must resolve to its own source file.");
        string materialPath = assetFolder + "/" + name + "_HDRP.mat";
        string prefabPath = assetFolder + "/" + name + ".prefab";
        if (File.Exists(ToAbsolute(materialPath, projectRoot)) || File.Exists(ToAbsolute(prefabPath, projectRoot)))
            throw new InvalidOperationException("Output material/prefab already exists; choose a new name rather than overwriting an existing asset.");
        int expectedSize = (int?)config["expectedTextureSize"] ?? (int?)config["texture_size"] ?? 8192;
        if (expectedSize < 1 || expectedSize > 8192) throw new ArgumentException("expectedTextureSize must be 1..8192.");

        var originalColliders = target.GetComponentsInChildren<Collider>(true);
        string collisionBefore = CollisionFingerprint(target);
        string sourcePrefabBefore = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(target.gameObject);
        Vector3 originalPosition = target.position, originalScale = target.localScale;
        Quaternion originalRotation = target.rotation;

        // Asset imports do not apply to the target's shared prefab. Validate every source
        // texture before creating scene objects or hiding the previous visual.
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        var sourceSizes = new Dictionary<string, Vector2Int>();
        foreach (var pair in texturePaths)
        {
            var importer = AssetImporter.GetAtPath(pair.Value) as TextureImporter;
            if (importer == null) throw new InvalidOperationException("Texture did not import: " + pair.Value);
            int w, h;
            importer.GetSourceTextureWidthAndHeight(out w, out h);
            if (w < 1 || h < 1 || w > 8192 || h > 8192)
                throw new InvalidOperationException(pair.Key + " has unsupported source dimensions " + w + "x" + h + ".");
            if (pair.Key == "baseColor" && (w != expectedSize || h != expectedSize))
                throw new InvalidOperationException(pair.Key + " source is " + w + "x" + h + "; expected " + expectedSize + "x" + expectedSize + ". No scene replacement was made.");
            sourceSizes.Add(pair.Key, new Vector2Int(w, h));
        }
        var modelImporter = AssetImporter.GetAtPath(fbx) as ModelImporter;
        if (modelImporter == null) throw new InvalidOperationException("FBX did not import: " + fbx);
        modelImporter.importAnimation = false;
        modelImporter.animationType = ModelImporterAnimationType.None;
        modelImporter.importCameras = false;
        modelImporter.importLights = false;
        modelImporter.materialImportMode = ModelImporterMaterialImportMode.None;
        modelImporter.addCollider = false;
        modelImporter.isReadable = false;
        modelImporter.SaveAndReimport();

        var textureReports = new List<object>();
        var textures = new Dictionary<string, Texture2D>();
        foreach (var pair in texturePaths)
        {
            ConfigureTexture(pair.Value, pair.Key);
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(pair.Value);
            Vector2Int sourceSize = sourceSizes[pair.Key];
            if (texture == null || texture.width != sourceSize.x || texture.height != sourceSize.y)
                throw new InvalidOperationException("Imported texture does not preserve its native size: " + pair.Value);
            textures.Add(pair.Key, texture);
            var importer = (TextureImporter)AssetImporter.GetAtPath(pair.Value);
            int w, h;
            importer.GetSourceTextureWidthAndHeight(out w, out h);
            textureReports.Add(new
            {
                channel = pair.Key, path = pair.Value, sourceWidth = w, sourceHeight = h,
                importedWidth = texture.width, importedHeight = texture.height,
                format = texture.format.ToString(), importer.maxTextureSize,
                importer.mipmapEnabled, importer.streamingMipmaps, importer.sRGBTexture,
                textureType = importer.textureType.ToString(), compression = importer.textureCompression.ToString()
            });
        }
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(fbx);
        if (model == null) throw new InvalidOperationException("FBX has no GameObject: " + fbx);
        Shader shader = Shader.Find("HDRP/Lit");
        if (shader == null) throw new InvalidOperationException("HDRP/Lit is unavailable.");

        Undo.IncrementCurrentGroup();
        int undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Install Tripo building " + name);
        GameObject wrapper = null;
        Material material = null;
        try
        {
            material = new Material(shader) { name = name + "_HDRP", enableInstancing = true };
            material.SetColor("_BaseColor", Color.white);
            material.SetTexture("_BaseColorMap", textures["baseColor"]);
            material.SetTexture("_NormalMap", textures["normal"]);
            material.SetTexture("_MaskMap", textures["hdrpMask"]);
            SetFloat(material, "_SurfaceType", 0f);
            SetFloat(material, "_AlphaCutoffEnable", 0f);
            SetFloat(material, "_NormalMapSpace", 0f);
            SetFloat(material, "_NormalScale", (float?)config["normalScale"] ?? 1f);
            SetFloat(material, "_Metallic", 1f);
            SetFloat(material, "_Smoothness", 1f);
            foreach (string channel in new[] { "_MetallicRemap", "_SmoothnessRemap", "_AORemap" })
            {
                SetFloat(material, channel + "Min", 0f);
                SetFloat(material, channel + "Max", 1f);
            }
            SetFloat(material, "_DoubleSidedEnable", (bool?)config["doubleSided"] == true ? 1f : 0f);
            HDMaterial.ValidateMaterial(material);
            if (!material.IsKeywordEnabled("_MASKMAP") || !material.IsKeywordEnabled("_NORMALMAP"))
                throw new InvalidOperationException("HDRP material did not enable its mask/normal keywords.");

            wrapper = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(wrapper, "Create Tripo building wrapper");
            Undo.SetTransformParent(wrapper.transform, target, "Place Tripo building");
            wrapper.transform.localPosition = Vector3.zero;
            wrapper.transform.localRotation = Quaternion.identity;
            wrapper.transform.localScale = Vector3.one;
            var visual = (GameObject)PrefabUtility.InstantiatePrefab(model, scene);
            Undo.RegisterCreatedObjectUndo(visual, "Instantiate Tripo FBX");
            visual.name = "Visual";
            visual.transform.SetParent(wrapper.transform, false);
            var renderers = visual.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) throw new InvalidOperationException("FBX has no renderers.");
            if (visual.GetComponentsInChildren<Collider>(true).Length != 0)
                throw new InvalidOperationException("Imported visual unexpectedly contains colliders.");
            foreach (var renderer in renderers)
            {
                int slotCount = Mathf.Max(1, renderer.sharedMaterials.Length);
                renderer.sharedMaterials = Enumerable.Repeat(material, slotCount).ToArray();
                PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
            }

            Bounds rawBounds = BoundsInSpace(visual, wrapper.transform);
            string requestedUp = (string)config["upAxis"] ?? "auto";
            string axisNote;
            Quaternion correction = UpCorrection(rawBounds, requestedUp, out axisNote);
            Quaternion modelRotation = Quaternion.Euler(0f, yaw, 0f) * correction;
            visual.transform.localPosition = modelRotation * visual.transform.localPosition;
            visual.transform.localRotation = modelRotation * visual.transform.localRotation;
            PrefabUtility.RecordPrefabInstancePropertyModifications(visual.transform);
            Bounds unscaledBounds = WorldBounds(visual);
            RequirePositive(unscaledBounds.size, "Imported model dimensions");
            wrapper.transform.localScale = new Vector3(
                targetSize.x / unscaledBounds.size.x,
                targetSize.y / unscaledBounds.size.y,
                targetSize.z / unscaledBounds.size.z);
            Bounds fittedBounds = WorldBounds(visual);
            wrapper.transform.position += new Vector3(
                oldBounds.center.x - fittedBounds.center.x,
                oldBounds.min.y - fittedBounds.min.y,
                oldBounds.center.z - fittedBounds.center.z);
            fittedBounds = WorldBounds(visual);
            if ((fittedBounds.size - targetSize).sqrMagnitude > .0001f ||
                Mathf.Abs(fittedBounds.min.y - oldBounds.min.y) > .002f)
                throw new InvalidOperationException("Model bounds did not match requested size/ground alignment.");

            // The old Visual must remain intact as the fallback. Preserve visible walking
            // surfaces with copies so disabling it does not make its existing stairs vanish.
            // This does not imply that a generated facade has a usable opening/interior.
            var retainedParts = new List<object>();
            bool retainWalkable = (bool?)config["retainWalkableSurfaces"] ?? true;
            if (hideName == "Visual" && retainWalkable)
            {
                string[] keepNames = { "Floor", "Stair", "EntryPorch", "BalconyDeck" };
                var selected = oldVisual.GetComponentsInChildren<Transform>(true)
                    .Where(t => keepNames.Contains(t.name)).ToArray();
                if (selected.Length > 0)
                {
                    var retainedRoot = new GameObject("RetainedWalkableSurfaces");
                    Undo.RegisterCreatedObjectUndo(retainedRoot, "Retain walkable floor and stair visuals");
                    retainedRoot.transform.SetParent(wrapper.transform, false);
                    foreach (var source in selected)
                    {
                        var retained = UnityEngine.Object.Instantiate(source.gameObject, source.parent, false);
                        Undo.RegisterCreatedObjectUndo(retained, "Retain " + source.name + " visual");
                        retained.name = source.name;
                        retained.transform.SetParent(retainedRoot.transform, true);
                        retainedParts.Add(new { source = HierarchyPath(source), worldBounds = Describe(WorldBounds(retained)) });
                    }
                }
            }

            AssetDatabase.CreateAsset(material, materialPath);
            EditorUtility.SetDirty(material);
            // Save only this new wrapper; do not apply additions to the existing building prefab.
            PrefabUtility.SaveAsPrefabAssetAndConnect(wrapper, prefabPath, InteractionMode.AutomatedAction);
            if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) == null)
                throw new InvalidOperationException("Failed to save new building prefab: " + prefabPath);
            Undo.RecordObject(oldVisual.gameObject, "Retain original building visual as inactive fallback");
            oldVisual.gameObject.SetActive(false);
            PrefabUtility.RecordPrefabInstancePropertyModifications(oldVisual.gameObject);
            EditorUtility.SetDirty(oldVisual.gameObject);

            if (CollisionFingerprint(target) != collisionBefore ||
                target.GetComponentsInChildren<Collider>(true).Length != originalColliders.Length)
                throw new InvalidOperationException("Collision structure changed during the visual replacement.");
            if (target.position != originalPosition || target.rotation != originalRotation || target.localScale != originalScale ||
                PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(target.gameObject) != sourcePrefabBefore)
                throw new InvalidOperationException("Original building root or prefab link changed.");
            AssetDatabase.SaveAssets();
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene)) throw new InvalidOperationException("Could not save the scene.");
            Undo.CollapseUndoOperations(undoGroup);
            return JsonConvert.SerializeObject(new
            {
                status = "installed", configPath, scene = scene.path, target = hierarchyPath,
                hiddenVisual = hideName, sourcePrefabPreserved = sourcePrefabBefore,
                wrapperPrefab = prefabPath, fbx, material = materialPath, shader = material.shader.name,
                keywords = material.shaderKeywords, textures = textureReports,
                modelUpAxis = axisNote, rawModelBounds = Describe(rawBounds), yaw,
                requestedWorldSize = Numbers(targetSize), oldWorldBounds = Describe(oldBounds),
                oldVisualPartBounds = oldPartBounds, retainedWalkableSurfaces = retainedParts,
                installedWorldBounds = Describe(fittedBounds), localPosition = Numbers(wrapper.transform.localPosition),
                localScale = Numbers(wrapper.transform.localScale), rendererCount = renderers.Length,
                collidersBefore = originalColliders.Length,
                collidersAfter = target.GetComponentsInChildren<Collider>(true).Length,
                collisionUnchanged = true, sceneSaved = true
            }, Formatting.Indented);
        }
        catch
        {
            Undo.RevertAllDownToGroup(undoGroup);
            if (wrapper != null) UnityEngine.Object.DestroyImmediate(wrapper);
            if (material != null && !EditorUtility.IsPersistent(material)) UnityEngine.Object.DestroyImmediate(material);
            // Imported sources and any newly written standalone assets remain inspectable.
            // No existing/shared prefab asset is overwritten, even after a failure.
            throw;
        }
    }

    static void ConfigureTexture(string path, string channel)
    {
        var importer = (TextureImporter)AssetImporter.GetAtPath(path);
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
        importer.SaveAndReimport();
    }

    static Quaternion UpCorrection(Bounds bounds, string requested, out string note)
    {
        string axis = requested.Trim().ToUpperInvariant();
        if (axis == "AUTO")
        {
            Vector3 s = bounds.size;
            RequirePositive(s, "Raw model bounds");
            float yScore = Mathf.Abs(bounds.min.y) / s.y + Mathf.Abs(bounds.center.x) / s.x + Mathf.Abs(bounds.center.z) / s.z;
            float zScore = Mathf.Abs(bounds.min.z) / s.z + Mathf.Abs(bounds.center.x) / s.x + Mathf.Abs(bounds.center.y) / s.y;
            axis = zScore < .15f && zScore + .15f < yScore ? "Z" : "Y";
            note = "auto=" + axis + "; grounded Y score=" + yScore.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) +
                "; grounded Z score=" + zScore.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) +
                (Mathf.Min(yScore, zScore) >= .15f ? "; ambiguous bounds: retained Y-up, review orientation" : "");
        }
        else note = "explicit=" + axis;
        switch (axis)
        {
            case "Y": return Quaternion.identity;
            case "Z": return Quaternion.Euler(-90f, 0f, 0f);
            case "-Y": return Quaternion.Euler(180f, 0f, 0f);
            case "-Z": return Quaternion.Euler(90f, 0f, 0f);
            default: throw new ArgumentException("upAxis must be auto, Y, Z, -Y, or -Z.");
        }
    }

    static Bounds WorldBounds(GameObject root)
    {
        var renderers = root.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0) throw new InvalidOperationException("No visual bounds: " + root.name);
        Bounds result = renderers[0].bounds;
        foreach (var r in renderers.Skip(1)) result.Encapsulate(r.bounds);
        return result;
    }

    static Bounds BoundsInSpace(GameObject root, Transform space)
    {
        bool hasBounds = false;
        Bounds result = default(Bounds);
        foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
        {
            Bounds local = renderer.localBounds;
            Matrix4x4 matrix = space.worldToLocalMatrix * renderer.localToWorldMatrix;
            foreach (float x in new[] { local.min.x, local.max.x })
                foreach (float y in new[] { local.min.y, local.max.y })
                    foreach (float z in new[] { local.min.z, local.max.z })
                    {
                        Vector3 p = matrix.MultiplyPoint3x4(new Vector3(x, y, z));
                        if (!hasBounds) { result = new Bounds(p, Vector3.zero); hasBounds = true; }
                        else result.Encapsulate(p);
                    }
        }
        if (!hasBounds) throw new InvalidOperationException("Imported model has no local bounds.");
        return result;
    }

    static string CollisionFingerprint(Transform root)
    {
        return string.Join("\n", root.GetComponentsInChildren<Collider>(true).Select(c =>
            c.GetInstanceID() + "|" + c.gameObject.activeSelf + "|" + c.enabled + "|" +
            EditorJsonUtility.ToJson(c) + "|" + EditorJsonUtility.ToJson(c.transform)));
    }

    static string HierarchyPath(Transform transform)
    {
        string result = transform.name;
        while (transform.parent != null) { transform = transform.parent; result = transform.name + "/" + result; }
        return result;
    }

    static Transform FindSceneTransform(Scene scene, string path)
    {
        string[] pieces = path.Split('/');
        var root = scene.GetRootGameObjects().SingleOrDefault(g => g.name == pieces[0]);
        return root == null ? null : (pieces.Length == 1 ? root.transform : root.transform.Find(string.Join("/", pieces.Skip(1))));
    }

    static string FindTexture(JObject config, string field, string folder, string[] files, params string[] names)
    {
        return FindFile(config, field, folder, files, p =>
            ImageExtensions.Contains(Path.GetExtension(p).ToLowerInvariant()) &&
            names.Any(n => NormalizeName(Path.GetFileNameWithoutExtension(p)).Contains(n)));
    }

    static string FindFile(JObject config, string field, string folder, string[] files, Func<string, bool> predicate)
    {
        string explicitPath = (string)config[field];
        if (!string.IsNullOrEmpty(explicitPath))
        {
            string asset = explicitPath.StartsWith("Assets/", StringComparison.Ordinal) ? explicitPath : folder + "/" + explicitPath;
            asset = asset.Replace('\\', '/');
            if (asset.Contains("/../") || !asset.StartsWith(folder + "/", StringComparison.Ordinal) || !files.Contains(asset))
                throw new ArgumentException(field + " must name an existing file inside assetFolder.");
            return asset;
        }
        string[] matches = files.Where(predicate).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException("Expected one " + field + " file, found " + matches.Length + ". Set explicit '" + field + "' in JSON. Candidates: " + string.Join(", ", matches));
        return matches[0];
    }

    static string NormalizeAssetFolder(string value, string projectRoot)
    {
        string absolute = Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(projectRoot, value));
        string assetsRoot = Path.GetFullPath(Application.dataPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!absolute.StartsWith(assetsRoot, StringComparison.Ordinal) || !Directory.Exists(absolute))
            throw new ArgumentException("assetFolder must be an existing directory inside this project's Assets.");
        return ToAssetPath(absolute, projectRoot).TrimEnd('/');
    }

    static string ToAbsolute(string assetPath, string projectRoot) { return Path.GetFullPath(Path.Combine(projectRoot, assetPath)); }
    static string ToAssetPath(string absolute, string projectRoot) { return Path.GetFullPath(absolute).Substring(projectRoot.TrimEnd(Path.DirectorySeparatorChar).Length + 1).Replace('\\', '/'); }
    static string NormalizeName(string value) { return new string(value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray()); }
    static string RequiredString(JObject config, string key)
    {
        string value = (string)config[key];
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Missing config field: " + key);
        return value;
    }
    static JToken TargetValue(JObject config, JObject target, string key, string alias)
    {
        return (target == null ? null : target[key]) ?? config[key] ?? (alias == null ? null : config[alias]);
    }
    static string TargetString(JObject config, JObject target, string key, string alias) { return (string)TargetValue(config, target, key, alias); }
    static float TargetFloat(JObject config, JObject target, string key, string alias, float fallback)
    {
        JToken token = TargetValue(config, target, key, alias);
        return token == null ? fallback : (float)token;
    }
    static void RequirePositive(Vector3 value, string label)
    {
        foreach (float n in new[] { value.x, value.y, value.z })
            if (n <= .00001f || float.IsNaN(n) || float.IsInfinity(n)) throw new InvalidOperationException(label + " must be finite and positive: " + value);
    }
    static void SetFloat(Material material, string key, float value) { if (material.HasProperty(key)) material.SetFloat(key, value); }
    static float[] Numbers(Vector3 v) { return new[] { v.x, v.y, v.z }; }
    static object Describe(Bounds b) { return new { center = Numbers(b.center), size = Numbers(b.size), min = Numbers(b.min), max = Numbers(b.max) }; }
}
