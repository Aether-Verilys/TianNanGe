using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Read-only audit for all Tripo entries in SourceAssets/Tripo/building-plan.json.
/// When a reference composition plan exists, installation dimensions are scaled
/// by planned root scale / baseline root scale without changing the original plan.
/// It never changes scene objects, prefab contents, asset importers, or scene state.
/// The only write is the JSON report under Temp/.
/// Invoke with Pipeline run_script:
///   file=AgentScripts/VerifyTripoBuildings.cs
///   entry=VerifyTripoBuildings.Run
/// </summary>
public static class VerifyTripoBuildings
{
    const string MainScene = "Assets/TianNanGe/Scenes/TianNanGe_Main.unity";
    const string PlanRelativePath = "SourceAssets/Tripo/building-plan.json";
    const string CompositionRelativePath = "SourceAssets/Composition/reference-layout.json";
    const string CompositionBaselineRelativePath = "SourceAssets/Composition/before-reference-layout.json";
    const string ReportRelativePath = "Temp/VerifyTripoBuildings-report.json";
    const float BoundsTolerance = 0.05f;
    const float RootPositionTolerance = 0.002f;
    const float RootScaleTolerance = 0.0001f;

    public static string Run()
    {
        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string repositoryRoot = Path.GetFullPath(Path.Combine(projectRoot, ".."));
        string planPath = Path.Combine(repositoryRoot, PlanRelativePath.Replace('/', Path.DirectorySeparatorChar));
        string reportPath = Path.Combine(projectRoot, ReportRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var report = new Dictionary<string, object>();
        var entries = new List<object>();

        Scene scene = SceneManager.GetSceneByPath(MainScene);
        if (!scene.IsValid() || !scene.isLoaded)
            scene = SceneManager.GetActiveScene();
        report["scene"] = scene.path;
        report["sceneIsLoaded"] = scene.IsValid() && scene.isLoaded;
        report["sceneIsDirty"] = scene.IsValid() && scene.isDirty;
        report["sceneSaved"] = scene.IsValid() && !scene.isDirty;
        report["plan"] = PlanRelativePath;
        report["readOnly"] = true;
        report["boundsTolerance"] = BoundsTolerance;

        try
        {
            if (!File.Exists(planPath))
                throw new FileNotFoundException("Missing building plan.", planPath);
            JObject plan = JObject.Parse(File.ReadAllText(planPath));
            JArray buildings = plan["buildings"] as JArray;
            if (buildings == null) throw new InvalidDataException("building-plan.json has no buildings array.");
            JArray compositionBuildings = null;
            JArray compositionBaseline = null;
            string compositionPath = Path.Combine(repositoryRoot, CompositionRelativePath);
            if (File.Exists(compositionPath))
            {
                compositionBuildings = JObject.Parse(File.ReadAllText(compositionPath))["buildings"] as JArray;
                if (compositionBuildings == null) throw new InvalidDataException("reference-layout.json has no buildings array.");
                string baselinePath = Path.Combine(repositoryRoot, CompositionBaselineRelativePath);
                if (!File.Exists(baselinePath)) throw new FileNotFoundException("Reference composition baseline is required to calculate expected dimensions.", baselinePath);
                compositionBaseline = JArray.Parse(File.ReadAllText(baselinePath));
            }
            report["compositionPlan"] = compositionBuildings == null ? null : CompositionRelativePath;
            report["compositionBaseline"] = compositionBaseline == null ? null : CompositionBaselineRelativePath;
            report["rootPositionTolerance"] = RootPositionTolerance;
            report["rootScaleTolerance"] = RootScaleTolerance;
            foreach (JObject building in buildings.OfType<JObject>())
            {
                try { entries.Add(CheckBuilding(scene, projectRoot, building, compositionBuildings, compositionBaseline)); }
                catch (Exception ex) { entries.Add(new { id = (string)building["id"], status = "failed", issues = new[] { ex.Message } }); }
            }
            report["buildingCount"] = entries.Count;
            report["buildings"] = entries;
            report["status"] = "checked";
        }
        catch (Exception ex)
        {
            report["status"] = "error";
            report["error"] = ex.ToString();
        }

        report["sceneIsDirtyAfterCheck"] = scene.IsValid() && scene.isDirty;
        report["sceneDirtyStateUnchanged"] = (bool)report["sceneIsDirty"] == (scene.IsValid() && scene.isDirty);
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath));
        File.WriteAllText(reportPath, JsonConvert.SerializeObject(report, Formatting.Indented));
        return JsonConvert.SerializeObject(new
        {
            status = report["status"],
            report = ReportRelativePath,
            scene = scene.path,
            sceneIsDirty = scene.IsValid() && scene.isDirty,
            buildingCount = entries.Count,
            installed = entries.Count(x => JObject.FromObject(x)["status"]?.Value<string>() == "installed"),
            pending = entries.Count(x => JObject.FromObject(x)["status"]?.Value<string>() == "pending"),
            failed = entries.Count(x => JObject.FromObject(x)["status"]?.Value<string>() == "failed")
        }, Formatting.Indented);
    }

    static object CheckBuilding(Scene scene, string projectRoot, JObject plan, JArray compositionBuildings, JArray compositionBaseline)
    {
        string id = (string)plan["id"] ?? "";
        string name = (string)plan["name"] ?? "";
        string targetPath = (string)plan["target"] ?? "";
        int expectedTextureSize = (int?)plan["texture_size"] ?? 8192;
        Vector3 installationSize = new Vector3(
            (float?)plan["width"] ?? 0f,
            (float?)plan["height"] ?? 0f,
            (float?)plan["depth"] ?? 0f);
        Vector3 expectedSize = installationSize;
        Transform target = FindSceneTransform(scene, targetPath);
        if (target == null)
            return new { id, name, target = targetPath, status = "pending", reason = "target_not_found" };

        Transform wrapper = target.Find(name);
        if (wrapper == null || !wrapper.gameObject.activeInHierarchy)
            return new { id, name, target = targetPath, status = "pending", reason = wrapper == null ? "model_wrapper_not_installed" : "model_wrapper_inactive" };

        var issues = new List<string>();
        object compositionCheck = null;
        if (compositionBuildings != null)
        {
            JObject compositionEntry = NamedEntry(compositionBuildings, target.name, CompositionRelativePath);
            JObject baselineEntry = NamedEntry(compositionBaseline, target.name, CompositionBaselineRelativePath);
            Vector3 plannedPosition = ReadVector(compositionEntry["position"], "composition position");
            Vector3 plannedScale = ReadVector(compositionEntry["scale"], "composition scale");
            Vector3 baselineScale = ReadVector(baselineEntry["scale"], "baseline scale");
            if (!Positive(plannedScale) || !Positive(baselineScale))
                throw new InvalidDataException("Composition and baseline scales must be positive: " + target.name);
            Vector3 scaleRatio = new Vector3(plannedScale.x / baselineScale.x, plannedScale.y / baselineScale.y, plannedScale.z / baselineScale.z);
            expectedSize = Vector3.Scale(installationSize, scaleRatio);
            bool positionMatch = WithinTolerance(target.position, plannedPosition, RootPositionTolerance);
            bool scaleMatch = WithinTolerance(target.localScale, plannedScale, RootScaleTolerance);
            if (!positionMatch) issues.Add("root_position_does_not_match_composition_plan");
            if (!scaleMatch) issues.Add("root_scale_does_not_match_composition_plan");
            compositionCheck = new
            {
                name = target.name,
                plannedWorldPosition = Numbers(plannedPosition),
                actualWorldPosition = Numbers(target.position),
                plannedLocalScale = Numbers(plannedScale),
                actualLocalScale = Numbers(target.localScale),
                baselineLocalScale = Numbers(baselineScale),
                scaleRatio = Numbers(scaleRatio),
                positionMatch,
                scaleMatch,
                sizeBasis = "installation planned world size * composition planned local scale / baseline local scale (per axis)"
            };
        }
        Transform oldVisual = target.Find("Visual");
        Transform oldProxy = target.Find("Proxy_ReplaceWithTripo");
        bool oldVisualDisabled = oldVisual == null || !oldVisual.gameObject.activeInHierarchy;
        bool oldProxyDisabled = oldProxy == null || !oldProxy.gameObject.activeInHierarchy;
        if (oldVisual == null && oldProxy == null) issues.Add("original_visual_or_proxy_missing");
        if (!oldVisualDisabled || !oldProxyDisabled)
            issues.Add("original_visual_or_proxy_still_enabled");

        Transform installedVisual = wrapper.Find("Visual");
        if (installedVisual == null) issues.Add("installed_visual_missing");
        Renderer[] renderers = installedVisual == null ? new Renderer[0] : installedVisual.GetComponentsInChildren<Renderer>(true);
        bool modelEnabled = wrapper.gameObject.activeInHierarchy && installedVisual != null &&
            installedVisual.gameObject.activeInHierarchy && renderers.Length > 0 &&
            renderers.All(r => r.enabled && r.gameObject.activeInHierarchy);
        if (!modelEnabled) issues.Add("installed_model_not_enabled");

        Bounds modelBounds = default(Bounds);
        bool hasBounds = renderers.Length > 0;
        if (hasBounds)
        {
            modelBounds = renderers[0].bounds;
            foreach (Renderer renderer in renderers.Skip(1)) modelBounds.Encapsulate(renderer.bounds);
        }
        Vector3 actualSize = hasBounds ? modelBounds.size : Vector3.zero;
        bool boundsMatch = hasBounds && Positive(expectedSize) &&
            Mathf.Abs(actualSize.x - expectedSize.x) <= BoundsTolerance &&
            Mathf.Abs(actualSize.y - expectedSize.y) <= BoundsTolerance &&
            Mathf.Abs(actualSize.z - expectedSize.z) <= BoundsTolerance;
        if (!boundsMatch) issues.Add("world_bounds_do_not_match_plan");

        var materialChecks = CheckMaterials(renderers, expectedTextureSize, issues);
        string sourcePrefab = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(target.gameObject) ?? "";
        JObject installationRecord = ReadInstallationRecord(projectRoot, id);
        string recordedSourcePrefab = (string)installationRecord?["sourcePrefabPreserved"];
        bool prefabAssetExists = !string.IsNullOrEmpty(sourcePrefab) && File.Exists(Path.Combine(projectRoot, sourcePrefab));
        bool prefabLink = prefabAssetExists && PrefabUtility.GetCorrespondingObjectFromSource(target.gameObject) != null &&
            !string.IsNullOrEmpty(recordedSourcePrefab) && sourcePrefab == recordedSourcePrefab;
        if (!prefabLink) issues.Add("original_prefab_link_missing");
        string expectedWrapperPrefab = "Assets/TianNanGe/Tripo/" + id + "/" + name + ".prefab";
        string wrapperPrefab = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(wrapper.gameObject) ?? "";
        if (wrapperPrefab != expectedWrapperPrefab) issues.Add("wrapper_prefab_link_incorrect");

        var currentCollision = CollisionSummary(target, target);
        var prefabCollision = ReadPrefabCollision(sourcePrefab, projectRoot);
        bool collisionComparable = prefabCollision["available"].Value<bool>() && currentCollision["available"].Value<bool>();
        bool collisionMatch = collisionComparable &&
            (int)prefabCollision["count"] == (int)currentCollision["count"] &&
            string.Equals((string)prefabCollision["signature"], (string)currentCollision["signature"], StringComparison.Ordinal);
        if (!collisionComparable) issues.Add("original_prefab_collision_unavailable");
        else if (!collisionMatch) issues.Add("collision_structure_changed_from_original_prefab");

        return new
        {
            id,
            name,
            target = targetPath,
            status = issues.Count == 0 ? "installed" : "failed",
            issues,
            modelEnabled,
            oldVisualPresent = oldVisual != null,
            oldVisualDisabled,
            oldProxyPresent = oldProxy != null,
            oldProxyDisabled,
            wrapperPath = RelativePath(target, wrapper),
            modelBounds = hasBounds ? Describe(modelBounds) : null,
            installationPlannedWorldSize = Numbers(installationSize),
            plannedWorldSize = Numbers(expectedSize),
            actualWorldSize = Numbers(actualSize),
            boundsMatch,
            composition = compositionCheck,
            materials = materialChecks,
            sourcePbrTextures = new[] { "BaseColor", "Normal", "Metallic", "Roughness", "MaskMap" }.Select(channel => new
            {
                channel,
                texture = DescribeTexture(AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/TianNanGe/Tripo/" + id + "/" + name + "_" + channel + ".png"))
            }).ToArray(),
            wrapperPrefab,
            originalPrefab = new { path = sourcePrefab, installationRecordPath = recordedSourcePrefab, assetExists = prefabAssetExists, linkPreserved = prefabLink },
            collision = new { current = currentCollision, originalPrefab = prefabCollision, comparable = collisionComparable, matchesOriginalPrefab = collisionMatch,
                comparisonBasis = "Current scene versus original prefab asset; this is not a reconstructed historical before count." }
        };
    }

    static object[] CheckMaterials(Renderer[] renderers, int expectedTextureSize, List<string> issues)
    {
        var materialChecks = new List<object>();
        foreach (Renderer renderer in renderers)
        {
            if (renderer.sharedMaterials.Length == 0 || renderer.sharedMaterials.Any(m => m == null))
                issues.Add("missing_material_slot:" + renderer.name);
            foreach (Material material in renderer.sharedMaterials.Where(m => m != null).Distinct())
            {
                Texture baseColor = material.HasProperty("_BaseColorMap") ? material.GetTexture("_BaseColorMap") : null;
                Texture normal = material.HasProperty("_NormalMap") ? material.GetTexture("_NormalMap") : null;
                Texture mask = material.HasProperty("_MaskMap") ? material.GetTexture("_MaskMap") : null;
                bool shaderPass = material.shader != null && material.shader.name == "HDRP/Lit";
                bool bindingPass = baseColor != null && normal != null && mask != null;
                bool importPass = CheckTextureImport(baseColor, false, true) && CheckTextureImport(normal, true, false) && CheckTextureImport(mask, false, false);
                bool keywordsPass = material.IsKeywordEnabled("_MASKMAP") && material.IsKeywordEnabled("_NORMALMAP");
                bool baseColorSizePass = baseColor != null && baseColor.width == expectedTextureSize && baseColor.height == expectedTextureSize;
                bool allPbrMapsAtExpectedSize = bindingPass &&
                    baseColor.width == expectedTextureSize && baseColor.height == expectedTextureSize &&
                    normal.width == expectedTextureSize && normal.height == expectedTextureSize &&
                    mask.width == expectedTextureSize && mask.height == expectedTextureSize;
                if (!shaderPass) issues.Add("material_shader_not_HDRP_Lit:" + material.name);
                if (!bindingPass) issues.Add("material_PBR_binding_incomplete:" + material.name);
                if (!importPass) issues.Add("material_texture_import_settings_incorrect:" + material.name);
                if (!keywordsPass) issues.Add("material_PBR_keywords_missing:" + material.name);
                if (!baseColorSizePass) issues.Add("base_color_not_expected_8K:" + material.name);
                materialChecks.Add(new
                {
                    renderer = renderer.name,
                    material = material.name,
                    shader = material.shader == null ? "" : material.shader.name,
                    shaderPass,
                    bindingPass,
                    importPass,
                    keywordsPass,
                    baseColor = DescribeTexture(baseColor),
                    normal = DescribeTexture(normal),
                    maskMap = DescribeTexture(mask),
                    baseColorSizePass,
                    allPbrMapsAtExpectedSize,
                    note = allPbrMapsAtExpectedSize ? "all channels match plan texture_size" : "Tripo extreme commonly returns 8K BaseColor with 4K PBR channels; report actual dimensions above"
                });
            }
        }
        if (materialChecks.Count == 0) issues.Add("no_material_found");
        return materialChecks.ToArray();
    }

    static JObject CollisionSummary(Transform root, Transform relativeRoot)
    {
        Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
        Transform collisionRoot = root.Find("Collision");
        var descriptors = colliders.Select(c => new
        {
            path = IndexedRelativePath(relativeRoot, c.transform),
            type = c.GetType().FullName,
            active = c.gameObject.activeSelf,
            enabled = c.enabled,
            serialized = EditorJsonUtility.ToJson(c)
        }).OrderBy(x => x.path, StringComparer.Ordinal).ThenBy(x => x.type, StringComparer.Ordinal).ToArray();
        var transforms = (collisionRoot == null ? new Transform[0] : collisionRoot.GetComponentsInChildren<Transform>(true))
            .Select(t => new
            {
                path = IndexedRelativePath(relativeRoot, t),
                activeSelf = t.gameObject.activeSelf,
                layer = t.gameObject.layer,
                localPosition = Numbers(t.localPosition),
                localRotation = new[] { t.localRotation.x, t.localRotation.y, t.localRotation.z, t.localRotation.w },
                localScale = Numbers(t.localScale)
            }).OrderBy(x => x.path, StringComparer.Ordinal).ToArray();
        return new JObject
        {
            ["available"] = true,
            ["count"] = colliders.Length,
            ["collisionRoot"] = collisionRoot != null ? RelativePath(relativeRoot, collisionRoot) : "",
            ["paths"] = JArray.FromObject(descriptors),
            ["transforms"] = JArray.FromObject(transforms),
            ["signature"] = JsonConvert.SerializeObject(new { descriptors, transforms }, Formatting.None)
        };
    }

    static JObject ReadPrefabCollision(string sourcePrefab, string projectRoot)
    {
        if (string.IsNullOrEmpty(sourcePrefab) || !File.Exists(Path.Combine(projectRoot, sourcePrefab)))
            return new JObject { ["available"] = false, ["reason"] = "source_prefab_missing" };
        try
        {
            GameObject prefabRoot = AssetDatabase.LoadAssetAtPath<GameObject>(sourcePrefab);
            if (prefabRoot == null) return new JObject { ["available"] = false, ["reason"] = "source_prefab_unloadable" };
            return CollisionSummary(prefabRoot.transform, prefabRoot.transform);
        }
        catch (Exception ex)
        {
            return new JObject { ["available"] = false, ["reason"] = ex.Message };
        }
    }

    static JObject ReadInstallationRecord(string projectRoot, string id)
    {
        string path = Path.Combine(repositoryRoot(projectRoot), "SourceAssets/Tripo/" + id + "/install-result.json");
        if (!File.Exists(path)) return null;
        try
        {
            JObject record = JObject.Parse(File.ReadAllText(path));
            JToken nested = record.SelectToken("data.result.result");
            return nested != null && nested.Type == JTokenType.String ? JObject.Parse((string)nested) : record;
        }
        catch { return null; }
    }

    static string repositoryRoot(string projectRoot)
    {
        return Path.GetFullPath(Path.Combine(projectRoot, ".."));
    }

    static object DescribeTexture(Texture texture)
    {
        if (texture == null) return null;
        return new { name = texture.name, width = texture.width, height = texture.height, format = texture is Texture2D ? ((Texture2D)texture).format.ToString() : "" };
    }

    static bool CheckTextureImport(Texture texture, bool normal, bool sRgb)
    {
        if (texture == null) return false;
        string path = AssetDatabase.GetAssetPath(texture);
        TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer == null) return false;
        return importer.textureType == (normal ? TextureImporterType.NormalMap : TextureImporterType.Default) && importer.sRGBTexture == sRgb && importer.mipmapEnabled;
    }

    static object Describe(Bounds bounds) => new { center = Numbers(bounds.center), size = Numbers(bounds.size), min = Numbers(bounds.min), max = Numbers(bounds.max) };
    static float[] Numbers(Vector3 value) => new[] { value.x, value.y, value.z };
    static bool Positive(Vector3 value) => value.x > 0f && value.y > 0f && value.z > 0f;

    static JObject NamedEntry(JArray entries, string name, string source)
    {
        JObject[] matches = entries == null ? new JObject[0] : entries.OfType<JObject>().Where(e => (string)e["name"] == name).ToArray();
        if (matches.Length != 1) throw new InvalidDataException(source + " must contain exactly one entry for " + name + "; found " + matches.Length);
        return matches[0];
    }

    static Vector3 ReadVector(JToken token, string label)
    {
        JArray array = token as JArray;
        if (array == null || array.Count != 3) throw new InvalidDataException(label + " must be an array of three finite numbers.");
        float[] values = array.Select(x => (float)x).ToArray();
        if (values.Any(x => float.IsNaN(x) || float.IsInfinity(x))) throw new InvalidDataException(label + " must contain only finite numbers.");
        return new Vector3(values[0], values[1], values[2]);
    }

    static bool WithinTolerance(Vector3 actual, Vector3 expected, float tolerance)
    {
        return Mathf.Abs(actual.x - expected.x) <= tolerance && Mathf.Abs(actual.y - expected.y) <= tolerance && Mathf.Abs(actual.z - expected.z) <= tolerance;
    }

    static string RelativePath(Transform root, Transform child)
    {
        if (child == root) return "";
        var parts = new List<string>();
        Transform cursor = child;
        while (cursor != null && cursor != root) { parts.Add(cursor.name); cursor = cursor.parent; }
        if (cursor != root) return child.name;
        parts.Reverse();
        return string.Join("/", parts);
    }

    // Include a same-name sibling ordinal so repeated Stair/Wall names remain distinct.
    static string IndexedRelativePath(Transform root, Transform child)
    {
        var parts = new List<string>();
        for (Transform cursor = child; cursor != null && cursor != root; cursor = cursor.parent)
        {
            int ordinal = 0;
            if (cursor.parent != null)
                for (int i = 0; i < cursor.GetSiblingIndex(); i++)
                    if (cursor.parent.GetChild(i).name == cursor.name) ordinal++;
            parts.Add(cursor.name + "[" + ordinal + "]");
        }
        parts.Reverse();
        return string.Join("/", parts);
    }

    static Transform FindSceneTransform(Scene scene, string path)
    {
        if (!scene.IsValid()) return null;
        string[] pieces = path.Split('/');
        GameObject root = scene.GetRootGameObjects().SingleOrDefault(g => g.name == pieces[0]);
        return root == null ? null : (pieces.Length == 1 ? root.transform : root.transform.Find(string.Join("/", pieces.Skip(1))));
    }
}
