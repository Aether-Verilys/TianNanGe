using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

// Read-only scene audit. Explicit entry: VerifyTripoMountainScene.Run.
// Reads SourceAssets/Composition/mountain-layout.json (same instances schema as
// InstallTripoMountain, plus each instance's building name under Architecture).
// Writes only ProjectRoot/Temp/VerifyTripoMountainScene-report.json. It does not
// change objects, assets, materials, colliders, transforms, or save the scene.
public static class VerifyTripoMountainScene
{
    const string ScenePath = "Assets/TianNanGe/Scenes/TianNanGe_Main.unity";
    const string ArchitecturePath = "TianNanGe_Blockout/Architecture";
    const string DefaultFolder = "Assets/TianNanGe/Tripo/SnowMountain09";
    const string DefaultName = "SnowMountain_P20_8K";
    const long ExpectedOriginalTriangles = 25380;
    static readonly string[] Pavilions = { "MainPavilion", "LeftPavilion", "FarPavilion", "FarRight" };

    sealed class Footprint
    {
        public Bounds bounds;
        public string path, kind;
        public object[] candidates;
    }

    public static string Run()
    {
        Scene scene = SceneManager.GetActiveScene();
        if (scene.path != ScenePath || EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Open TianNanGe_Main in Edit Mode to audit mountains.");
        bool dirtyBefore = scene.isDirty;
        string project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string repository = Path.GetFullPath(Path.Combine(project, ".."));
        string layoutPath = Path.Combine(repository, "SourceAssets/Composition/mountain-layout.json");
        JObject layout = ReadObject(layoutPath);
        JArray placements = layout["instances"] as JArray;
        if (placements == null || placements.Count == 0)
            throw new InvalidOperationException("mountain-layout.json must contain instances.");
        string folder = (string)layout["assetFolder"] ?? DefaultFolder;
        string name = (string)layout["name"] ?? DefaultName;
        string prefabPath = folder + "/" + name + ".prefab";
        JObject imported = ReadObject(Path.Combine(project, folder, name + "_ImportReport.json"));
        long importedTriangles = (long?)imported["geometry"]?["triangles"] ?? -1;
        var globalIssues = new List<string>();
        if (importedTriangles != ExpectedOriginalTriangles)
            globalIssues.Add("ImportReport has " + importedTriangles + " triangles; approved original model must have 25380.");
        if (imported["geometry"]?["simplification"] != null)
            globalIssues.Add("ImportReport contains geometry.simplification; inspect whether the unsimplified source was restored.");
        if ((bool?)imported["images_resized"] != false || (bool?)imported["uvs_repacked"] != false)
            globalIssues.Add("ImportReport does not confirm native images and preserved UVs.");
        long tolerance = (long?)layout["triangleTolerance"] ?? 0;
        long permitted = 25000 + tolerance;
        if (tolerance < 0 || tolerance > 1000)
            globalIssues.Add("triangleTolerance must be within the explicitly approved 0..1000 range.");
        if (importedTriangles > permitted)
            globalIssues.Add("ImportReport triangle count exceeds this layout's requested 25000 + explicit tolerance " + tolerance + ".");
        string installationPath = Path.Combine(project, "Temp/TripoMountain-install-report.json");
        JObject installation = File.Exists(installationPath) ? ReadObject(installationPath) : null;
        if (installation != null && (long?)installation["actualTriangles"] != importedTriangles)
            globalIssues.Add("Latest install report actualTriangles differs from ImportReport.");
        float gapTolerance = ReadPositive(layout, "supportGapTolerance", .35f);
        float minimumNormalY = ReadPositive(layout, "minimumSupportNormalY", .65f);
        if (minimumNormalY > 1) throw new ArgumentException("minimumSupportNormalY must be <= 1.");
        float rayMargin = ReadPositive(layout, "supportRayMargin", 2f);
        var results = new List<object>();
        int valid = 0, supported = 0, samplesPassed = 0, samplesTotal = 0;
        // Synchronizes physics' cached transforms without editing or saving any scene data.
        Physics.SyncTransforms();
        foreach (JToken token in placements)
        {
            JObject entry = token as JObject;
            var issues = new List<string>();
            var supportIssues = new List<string>();
            string instanceName = (string)entry?["name"] ?? "<missing name>";
            string supportPath = (string)entry?["supportRoot"] ?? "";
            string buildingName = (string)entry?["building"] ?? "";
            try
            {
                if (entry == null) throw new InvalidOperationException("Instance entry must be an object.");
                Transform support = Find(scene, supportPath);
                Transform instance = support == null ? null : support.Find(instanceName);
                if (support == null || instance == null)
                    throw new InvalidOperationException("Missing instance " + supportPath + "/" + instanceName);
                if (!instance.gameObject.activeInHierarchy) issues.Add("Mountain instance is inactive.");
                string sourcePrefab = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(instance.gameObject);
                if (sourcePrefab != prefabPath) issues.Add("Unexpected mountain prefab: " + sourcePrefab);
                if (instance.Find("ModelYaw/Visual") == null) issues.Add("Expected ModelYaw/Visual hierarchy is absent.");
                var geometry = CheckGeometry(instance, importedTriangles, permitted, issues);
                var materials = CheckMaterials(instance, folder, name, imported, issues);
                MeshCollider[] meshColliders = instance.GetComponentsInChildren<MeshCollider>(true);
                var collision = CheckColliders(instance, meshColliders, issues);
                var legacy = CheckLegacy(support, issues);
                Bounds mountainBounds = MountainGeometryBounds(instance);
                var placement = CheckPlacement(entry, mountainBounds, instance, issues);
                object supportCheck;
                bool supportPass = false;
                try
                {
                    if (string.IsNullOrWhiteSpace(buildingName)) throw new InvalidOperationException("No building field in layout instance.");
                    string buildingPath = buildingName.StartsWith(ArchitecturePath + "/", StringComparison.Ordinal)
                        ? buildingName : ArchitecturePath + "/" + buildingName;
                    Transform building = Find(scene, buildingPath);
                    if (building == null) throw new InvalidOperationException("Building is missing: " + buildingPath);
                    Footprint foot = FindFootprint(building);
                    var probes = new List<object>();
                    int passing = 0;
                    Vector2[] offsets = { Vector2.zero, new Vector2(-1,-1), new Vector2(-1,1), new Vector2(1,-1),
                        new Vector2(1,1), new Vector2(-1,0), new Vector2(1,0), new Vector2(0,-1), new Vector2(0,1) };
                    string[] labels = { "center", "corner_minX_minZ", "corner_minX_maxZ", "corner_maxX_minZ", "corner_maxX_maxZ",
                        "edge_minX", "edge_maxX", "edge_minZ", "edge_maxZ" };
                    MeshCollider[] activeColliders = meshColliders.Where(c => c.enabled && c.gameObject.activeInHierarchy && !c.isTrigger).ToArray();
                    float upper = Mathf.Max(foot.bounds.min.y, mountainBounds.max.y);
                    float lower = Mathf.Min(foot.bounds.min.y, mountainBounds.min.y);
                    foreach (MeshCollider collider in activeColliders) { upper = Mathf.Max(upper, collider.bounds.max.y); lower = Mathf.Min(lower, collider.bounds.min.y); }
                    float originY = upper + rayMargin, distance = originY - lower + rayMargin;
                    for (int i = 0; i < offsets.Length; i++)
                    {
                        Vector3 origin = new Vector3(foot.bounds.center.x + offsets[i].x * foot.bounds.extents.x,
                            originY, foot.bounds.center.z + offsets[i].y * foot.bounds.extents.z);
                        bool hitAny = false;
                        RaycastHit best = new RaycastHit();
                        string colliderPath = null;
                        foreach (MeshCollider collider in activeColliders)
                        {
                            RaycastHit hit;
                            if (collider.Raycast(new Ray(origin, Vector3.down), out hit, distance) &&
                                (!hitAny || hit.point.y > best.point.y))
                            { best = hit; hitAny = true; colliderPath = Hierarchy(collider.transform); }
                        }
                        float? gap = hitAny ? (float?)(foot.bounds.min.y - best.point.y) : null;
                        bool normalPass = hitAny && best.normal.y >= minimumNormalY;
                        bool gapPass = gap.HasValue && Mathf.Abs(gap.Value) <= gapTolerance;
                        bool pass = hitAny && normalPass && gapPass;
                        if (pass) passing++;
                        probes.Add(new { sample = labels[i], origin = Numbers(origin), rayDirection = new float[] {0,-1,0},
                            rayDistance = distance, hit = hitAny, collider = colliderPath, hitPoint = hitAny ? Numbers(best.point) : null,
                            height = hitAny ? (float?)best.point.y : null, normal = hitAny ? Numbers(best.normal) : null,
                            gap, normalPass, gapPass, supported = pass,
                            diagnosis = !hitAny ? "miss" : !normalPass ? "slope_too_steep" : !gapPass ?
                                (gap > 0 ? "building_above_rock" : "rock_intersects_building") : "supported" });
                    }
                    samplesPassed += passing;
                    samplesTotal += offsets.Length;
                    supportPass = passing == offsets.Length;
                    if (!supportPass) supportIssues.Add("Building supported at " + passing + "/9 exact footprint samples; inspect gaps/normals before accepting placement.");
                    supportCheck = new { building = buildingPath, footprintSource = foot.path, footprintKind = foot.kind,
                        footprint = Describe(foot.bounds), foot.candidates, samples = probes, supportedSamples = passing,
                        totalSamples = offsets.Length, fullySupported = supportPass, activeMeshColliders = activeColliders.Length };
                }
                catch (Exception error)
                {
                    supportIssues.Add(error.Message);
                    supportCheck = new { fullySupported = false, building = buildingName, error = error.Message };
                }
                bool instanceValid = issues.Count == 0;
                if (instanceValid) valid++;
                if (supportPass) supported++;
                results.Add(new { name = instanceName, supportRoot = supportPath, building = buildingName,
                    valid = instanceValid, fullySupported = supportPass, issues, supportIssues, sourcePrefab,
                    worldBounds = Describe(mountainBounds), geometry, materials, collision, legacy, placement, supportCheck });
            }
            catch (Exception error)
            {
                issues.Add(error.Message);
                results.Add(new { name = instanceName, supportRoot = supportPath, building = buildingName,
                    valid = false, fullySupported = false, issues, supportIssues });
            }
        }
        bool sceneUnchanged = dirtyBefore == scene.isDirty;
        if (!sceneUnchanged) globalIssues.Add("Scene dirty flag changed during audit.");
        var report = new { status = globalIssues.Count == 0 && valid == placements.Count && supported == placements.Count ? "passed" : "needs_attention",
            scene = scene.path, sceneUnchanged, dirtyBefore, dirtyAfter = scene.isDirty,
            layout = "SourceAssets/Composition/mountain-layout.json", expectedOriginalTriangles = ExpectedOriginalTriangles,
            importReportTriangles = importedTriangles, requestedTriangleLimit = 25000, triangleTolerance = tolerance, maxTriangles = permitted,
            installationReportAvailable = installation != null, installationReportActualTriangles = (long?)installation?["actualTriangles"],
            sourceSimplification = imported["geometry"]?["simplification"], globalIssues,
            supportCriteria = new { gapTolerance, minimumNormalY, rayMargin,
                gapDefinition = "building footprint minY minus highest ray hit Y; positive means floating, negative means intersection",
                sampleLocations = "center, 4 exact AABB corners, and 4 exact AABB edge midpoints; no inset",
                scope = "only enabled nontrigger MeshColliders belonging to this instance; no other support can mask a miss" },
            totalInstances = placements.Count, validInstances = valid, fullySupportedInstances = supported,
            supportedSamples = samplesPassed, totalSamples = samplesTotal, instances = results };
        string json = JsonConvert.SerializeObject(report, Formatting.Indented);
        string output = Path.Combine(project, "Temp/VerifyTripoMountainScene-report.json");
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        File.WriteAllText(output, json + "\n");
        return json;
    }

    static object CheckGeometry(Transform instance, long reported, long maximum, List<string> issues)
    {
        long total = 0;
        var records = new List<object>();
        foreach (MeshFilter filter in instance.GetComponentsInChildren<MeshFilter>(true))
        {
            Mesh mesh = filter.sharedMesh;
            if (mesh == null) { issues.Add("Missing visual mesh: " + Hierarchy(filter.transform)); continue; }
            long triangles = 0;
            bool topologyValid = true;
            for (int i = 0; i < mesh.subMeshCount; i++)
            {
                uint indices = mesh.GetIndexCount(i);
                bool valid = mesh.GetTopology(i) == MeshTopology.Triangles && indices % 3 == 0;
                topologyValid &= valid;
                if (valid) triangles += indices / 3;
            }
            if (!topologyValid) issues.Add("Nontriangle topology: " + mesh.name);
            total += triangles;
            records.Add(new { path = Hierarchy(filter.transform), mesh = mesh.name, triangles, topologyValid, mesh.isReadable });
        }
        if (total != ExpectedOriginalTriangles || total != reported)
            issues.Add("Actual mesh triangles " + total + " must match original 25380 and ImportReport " + reported + ".");
        if (total <= 0 || total > maximum) issues.Add("Actual triangle count exceeds permitted budget or is empty.");
        if (instance.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length > 0) issues.Add("Unexpected skinned mountain mesh.");
        return new { triangles = total, matchesOriginal = total == ExpectedOriginalTriangles, matchesImportReport = total == reported, meshes = records };
    }

    static object CheckMaterials(Transform instance, string folder, string name, JObject imported, List<string> issues)
    {
        Renderer[] renderers = instance.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0) issues.Add("Mountain has no renderers.");
        foreach (Renderer renderer in renderers)
            if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) issues.Add("Disabled mountain renderer: " + Hierarchy(renderer.transform));
        Material[] materials = renderers.SelectMany(r => r.sharedMaterials).Distinct().ToArray();
        var records = new List<object>();
        foreach (Material material in materials)
        {
            if (material == null) { issues.Add("Missing mountain material."); continue; }
            bool hdrp = material.shader != null && material.shader.name == "HDRP/Lit";
            bool normalKeyword = material.IsKeywordEnabled("_NORMALMAP"), maskKeyword = material.IsKeywordEnabled("_MASKMAP");
            if (!hdrp || !normalKeyword || !maskKeyword) issues.Add("Material lacks HDRP/Lit PBR shader/keywords: " + material.name);
            var textureReports = new List<object>();
            string[] properties = { "_BaseColorMap", "_NormalMap", "_MaskMap" };
            string[] suffixes = { "BaseColor", "Normal", "MaskMap" };
            string[] mapKeys = { "base_color", "normal", "mask" };
            for (int i = 0; i < properties.Length; i++)
            {
                Texture texture = material.HasProperty(properties[i]) ? material.GetTexture(properties[i]) : null;
                string path = texture == null ? null : AssetDatabase.GetAssetPath(texture);
                string expectedPath = folder + "/" + name + "_" + suffixes[i] + ".png";
                var importer = path == null ? null : AssetImporter.GetAtPath(path) as TextureImporter;
                JToken size = imported["materials"]?[0]?["maps"]?[mapKeys[i]];
                bool matchingSize = texture != null && (int?)size?["width"] == texture.width && (int?)size?["height"] == texture.height;
                bool importValid = importer != null && importer.sRGBTexture == (i == 0) && importer.mipmapEnabled &&
                    importer.textureType == (i == 1 ? TextureImporterType.NormalMap : TextureImporterType.Default);
                bool valid = path == expectedPath && matchingSize && importValid;
                if (!valid) issues.Add("Material texture/import mismatch: " + material.name + " " + properties[i]);
                textureReports.Add(new { property = properties[i], path, expectedPath, valid,
                    width = texture == null ? (int?)null : texture.width, height = texture == null ? (int?)null : texture.height,
                    nativeDimensionsPreserved = matchingSize, importValid });
            }
            records.Add(new { name = material.name, path = AssetDatabase.GetAssetPath(material), shader = material.shader == null ? null : material.shader.name,
                normalKeyword, maskKeyword, textures = textureReports });
        }
        return records;
    }

    static object CheckColliders(Transform instance, MeshCollider[] colliders, List<string> issues)
    {
        MeshFilter[] filters = instance.GetComponentsInChildren<MeshFilter>(true);
        if (colliders.Length != filters.Length || instance.GetComponentsInChildren<Collider>(true).Length != colliders.Length)
            issues.Add("Expected exactly one MeshCollider per visual mesh and no extra collider types.");
        var records = new List<object>();
        foreach (MeshFilter filter in filters)
        {
            MeshCollider collider = filter.GetComponent<MeshCollider>();
            bool valid = collider != null && collider.enabled && collider.gameObject.activeInHierarchy &&
                !collider.convex && !collider.isTrigger && collider.sharedMesh == filter.sharedMesh;
            if (!valid) issues.Add("Collider does not match enabled visual geometry: " + Hierarchy(filter.transform));
            records.Add(new { path = Hierarchy(filter.transform), valid, present = collider != null,
                enabled = collider != null && collider.enabled, active = collider != null && collider.gameObject.activeInHierarchy,
                convex = collider != null && collider.convex, trigger = collider != null && collider.isTrigger,
                sameMesh = collider != null && collider.sharedMesh == filter.sharedMesh });
        }
        return records;
    }

    static object CheckLegacy(Transform support, List<string> issues)
    {
        var records = new List<object>();
        foreach (string name in new[] { "Rock", "SnowCap" })
        {
            Transform legacy = support.Find(name);
            if (legacy == null) { records.Add(new { name, exists = false, disabled = true, note = "No procedural component on a newly created support." }); continue; }
            Renderer[] renderers = legacy.GetComponentsInChildren<Renderer>(true);
            Collider[] colliders = legacy.GetComponentsInChildren<Collider>(true);
            bool disabled = renderers.All(r => !r.enabled) && colliders.All(c => !c.enabled);
            if (!disabled) issues.Add("Legacy " + name + " renderer/collider remains enabled at " + Hierarchy(support));
            records.Add(new { name, exists = true, disabled, renderers = renderers.Select(r => new { path = Hierarchy(r.transform), r.enabled }).ToArray(),
                colliders = colliders.Select(c => new { path = Hierarchy(c.transform), c.enabled }).ToArray() });
        }
        return records;
    }

    static object CheckPlacement(JObject entry, Bounds actual, Transform instance, List<string> issues)
    {
        Vector3 size = new Vector3((float)entry["width"], (float)entry["height"], (float)entry["depth"]);
        float x = (float)entry["centerX"], z = (float)entry["centerZ"], top = (float)entry["topY"];
        float yaw = (float?)entry["yaw"] ?? 0;
        Transform yawRoot = instance.Find("ModelYaw");
        bool matches = (actual.size - size).sqrMagnitude < .0001f && Mathf.Abs(actual.center.x - x) < .002f &&
            Mathf.Abs(actual.center.z - z) < .002f && Mathf.Abs(actual.max.y - top) < .002f && yawRoot != null &&
            Quaternion.Angle(yawRoot.localRotation, Quaternion.Euler(0,yaw,0)) < .01f;
        if (!matches) issues.Add("Instance world bounds/yaw do not match mountain-layout.json.");
        return new { matches, requestedSize = Numbers(size), centerX = x, centerZ = z, topY = top, yaw };
    }

    static Footprint FindFootprint(Transform building)
    {
        if (Pavilions.Contains(building.name))
        {
            Transform stone = building.Find("Proxy_ReplaceWithTripo/StoneBase");
            if (stone == null) throw new InvalidOperationException("Hidden Proxy StoneBase is missing: " + building.name);
            return new Footprint { bounds = RenderBounds(stone), path = Hierarchy(stone), kind = "hidden_proxy_stone_base", candidates = new object[0] };
        }
        Transform[] retained = building.GetComponentsInChildren<Transform>(true).Where(t => t.name == "RetainedWalkableSurfaces").ToArray();
        var floors = retained.SelectMany(t => t.GetComponentsInChildren<Transform>(true)).Where(t => t.name == "Floor")
            .Distinct().Where(t => t.GetComponentsInChildren<Renderer>(true).Length > 0)
            .Select(t => new { transform = t, bounds = RenderBounds(t) }).OrderBy(item => item.bounds.min.y)
            .ThenByDescending(item => item.bounds.size.x * item.bounds.size.z).ToArray();
        if (floors.Length == 0) throw new InvalidOperationException("No retained Floor visual under building " + building.name);
        return new Footprint { bounds = floors[0].bounds, path = Hierarchy(floors[0].transform), kind = "lowest_retained_floor",
            candidates = floors.Select(item => (object)new { path = Hierarchy(item.transform), bounds = Describe(item.bounds),
                active = item.transform.gameObject.activeInHierarchy }).ToArray() };
    }

    static Bounds RenderBounds(Transform root)
    {
        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0) throw new InvalidOperationException("No renderer bounds for " + Hierarchy(root));
        Bounds bounds = renderers[0].bounds;
        foreach (Renderer renderer in renderers.Skip(1)) bounds.Encapsulate(renderer.bounds);
        return bounds;
    }
    static Bounds MountainGeometryBounds(Transform root)
    {
        // Match InstallTripoMountain's exact vertex-based world dimensions.
        // Keep RenderBounds for the hidden StoneBase and retained Floor footprints.
        bool initialized = false;
        Bounds bounds = default(Bounds);
        foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(true))
        {
            Mesh mesh = filter.sharedMesh;
            if (mesh == null || !mesh.isReadable)
                throw new InvalidOperationException("Readable mesh required for exact mountain bounds: " + Hierarchy(filter.transform));
            foreach (Vector3 localVertex in mesh.vertices)
            {
                Vector3 worldVertex = filter.transform.TransformPoint(localVertex);
                if (!initialized) { bounds = new Bounds(worldVertex, Vector3.zero); initialized = true; }
                else bounds.Encapsulate(worldVertex);
            }
        }
        if (!initialized) throw new InvalidOperationException("Mountain has no mesh vertices: " + Hierarchy(root));
        return bounds;
    }
    static Transform Find(Scene scene, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string[] pieces = path.Split('/');
        GameObject root = scene.GetRootGameObjects().SingleOrDefault(g => g.name == pieces[0]);
        return root == null ? null : pieces.Length == 1 ? root.transform : root.transform.Find(string.Join("/", pieces.Skip(1)));
    }
    static JObject ReadObject(string path)
    { if (!File.Exists(path)) throw new FileNotFoundException("Missing audit input", path); return JObject.Parse(File.ReadAllText(path)); }
    static float ReadPositive(JObject source, string key, float fallback)
    { float value = (float?)source[key] ?? fallback; if (value <= 0 || float.IsNaN(value) || float.IsInfinity(value)) throw new ArgumentException(key + " must be finite and positive."); return value; }
    static string Hierarchy(Transform t)
    { string value = t.name; while (t.parent != null) { t = t.parent; value = t.name + "/" + value; } return value; }
    static float[] Numbers(Vector3 value) { return new[] { value.x, value.y, value.z }; }
    static object Describe(Bounds value) { return new { center = Numbers(value.center), size = Numbers(value.size), min = Numbers(value.min), max = Numbers(value.max) }; }
}
