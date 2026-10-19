using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// Explicit operation: FitTripoMountainFoundations.Apply. Merely loading this file does not execute it.
// Reads SourceAssets/Composition/mountain-layout.json. Adds visible stone/snow solids only under each
// supportRoot/FittedFoundation; leaves building transforms, Tripo geometry and shared prefabs intact.
// Optional layout.foundations.buildBridgeStairs=true enables a conservative 28-step bridge connection.
// A missing rock hit aborts that foundation/stair item and is reported instead of inventing support.
public static class FitTripoMountainFoundations
{
    const string MainScene = "Assets/TianNanGe/Scenes/TianNanGe_Main.unity";
    const string Mountains = "TianNanGe_Blockout/Mountains";
    const string Architecture = "TianNanGe_Blockout/Architecture";
    const string Marker = "GeneratedBy_FitTripoMountainFoundations";
    const int Grid = 9;
    const long OriginalTriangles = 25380;
    const float FootprintScale = .98f, RockPenetration = .15f, BuildingOverlap = .025f, SnowThickness = .035f;
    const float NeighborhoodRadius = .003f;
    static readonly string[] Pavilions = { "MainPavilion", "LeftPavilion", "FarPavilion", "FarRight" };

    sealed class Job
    {
        public string name, buildingName, footprintSource;
        public Transform support, mountain, building;
        public Bounds footprint, rockBounds;
        public float top, bottom, width, depth;
        public MeshCollider[] rocks;
        public List<Probe> probes = new List<Probe>();
        public List<string> issues = new List<string>();
        public List<string> warnings = new List<string>();
        public string mountainFingerprint, buildingFingerprint;
        public bool safe;
        public Transform foundation;
    }
    sealed class Probe
    {
        public string label, collider;
        public float x, z;
        public bool hit;
        public float? height;
        public float[] normal;
        public float neighborhoodRadius;
        public float[] selectedOffset;
        public int neighborhoodHits;
        public object[] neighborhoodSamples;
    }

    public static string Apply()
    {
        Scene scene = SceneManager.GetActiveScene();
        if (scene.path != MainScene || EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Open TianNanGe_Main in Edit Mode before fitting foundations.");
        string project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string repo = Path.GetFullPath(Path.Combine(project, ".."));
        JObject layout = JObject.Parse(File.ReadAllText(Path.Combine(repo, "SourceAssets/Composition/mountain-layout.json")));
        var entries = layout["instances"] as JArray;
        if (entries == null || entries.Count != 8) throw new InvalidOperationException("Expected the eight single-building mountain instances.");
        Material stone = AssetDatabase.LoadAssetAtPath<Material>("Assets/TianNanGe/Materials/Stone.mat");
        Material snow = AssetDatabase.LoadAssetAtPath<Material>("Assets/TianNanGe/Materials/Snow.mat");
        if (stone == null || snow == null) throw new InvalidOperationException("Existing Stone.mat and Snow.mat are required.");
        Physics.SyncTransforms();
        var jobs = new List<Job>();
        var preparationFailures = new List<object>();
        foreach (JObject entry in entries)
        {
            try { jobs.Add(Prepare(scene, entry)); }
            catch (Exception error) { preparationFailures.Add(new { name = (string)entry["name"], building = (string)entry["building"], error = error.Message }); }
        }
        Undo.IncrementCurrentGroup();
        int undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Fit visible stone foundations to Tripo mountains");
        try
        {
            foreach (Job job in jobs.Where(j => j.safe))
            {
                Transform root = ReplaceGeneratedRoot(job.support, "FittedFoundation");
                FitUnitWorldTransform(root, new Vector3(job.footprint.center.x, job.bottom, job.footprint.center.z));
                float total = job.top - job.bottom;
                float stoneHeight = total - SnowThickness;
                // Three actual box solids give restrained masonry ledges. Every collision
                // volume is also rendered; the broad top is no larger than .98 of the foot.
                float lowerHeight = Mathf.Min(.18f, stoneHeight * .15f);
                float upperHeight = Mathf.Min(.14f, stoneHeight * .15f);
                float middleHeight = stoneHeight - lowerHeight - upperHeight;
                Box(root, "StoneFoot", new Vector3(0, lowerHeight * .5f, 0),
                    new Vector3(job.width, lowerHeight, job.depth), stone);
                Box(root, "StoneCore", new Vector3(0, lowerHeight + middleHeight * .5f, 0),
                    new Vector3(job.width * .995f, middleHeight, job.depth * .995f), stone);
                Box(root, "StoneCornice", new Vector3(0, stoneHeight - upperHeight * .5f, 0),
                    new Vector3(job.width, upperHeight, job.depth), stone);
                Box(root, "SnowTop", new Vector3(0, total - SnowThickness * .5f, 0),
                    new Vector3(job.width, SnowThickness, job.depth), snow);
                job.foundation = root;
            }
            Physics.SyncTransforms();
            object stairs = BuildOptionalStairs(scene, layout, jobs, stone, snow);
            Physics.SyncTransforms();
            var reports = new List<object>();
            foreach (Job job in jobs)
            {
                if (Fingerprint(job.mountain) != job.mountainFingerprint || Fingerprint(job.building) != job.buildingFingerprint)
                    throw new InvalidOperationException("Original building/mountain changed while creating foundation: " + job.name);
                long triangles = TriangleCount(job.mountain);
                var contacts = job.probes.Select(p => new
                {
                    p.label, p.x, p.z, p.hit, rockHeight = p.height, p.normal, p.collider,
                    p.neighborhoodRadius, p.selectedOffset, p.neighborhoodHits, p.neighborhoodSamples,
                    foundationBottom = job.bottom, foundationTop = job.top,
                    bottomPenetration = p.hit ? (float?)(p.height.Value - job.bottom) : null,
                    rockAboveFoundationTop = p.hit && p.height.Value > job.top,
                    embedded = p.hit && p.height.Value - job.bottom >= RockPenetration - .002f,
                    materialGap = p.hit ? (float?)(job.bottom - p.height.Value) : null
                }).ToArray();
                bool embeddedAll = contacts.Length == Grid * Grid && contacts.All(p => p.embedded);
                object topChecks = job.foundation == null ? null : CheckFoundationTop(job);
                reports.Add(new
                {
                    name = job.name, building = job.buildingName, supportRoot = Hierarchy(job.support),
                    status = job.safe ? "installed" : "skipped_missing_or_invalid_support", job.issues, job.warnings,
                    footprintSource = job.footprintSource, footprint = Describe(job.footprint), worldWidth = job.width, worldDepth = job.depth,
                    buildingModelBottom = job.top - BuildingOverlap, foundationTop = job.top, foundationBottom = job.bottom,
                    height = job.top - job.bottom, allSampledFeetEmbedded = embeddedAll, contacts, topChecks,
                    originalMountainTriangles = triangles, mountainGeometryUnchanged = true, buildingTransformUnchanged = true,
                    foundationBoxes = job.foundation == null ? 0 : job.foundation.GetComponentsInChildren<BoxCollider>(true).Length
                });
            }
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene)) throw new InvalidOperationException("Could not save the foundation scene.");
            Undo.CollapseUndoOperations(undoGroup);
            string output = Path.Combine(project, "Temp/FitTripoMountainFoundations-report.json");
            var result = new { status = preparationFailures.Count == 0 && jobs.All(j => j.safe && j.warnings.Count == 0) ? "installed" : "needs_review",
                scene = scene.path, sceneSaved = true, originalMountainTrianglesPerInstance = OriginalTriangles,
                footprintScale = FootprintScale, minimumRockPenetration = RockPenetration, buildingOverlap = BuildingOverlap,
                sampleGrid = Grid + "x" + Grid, neighborhoodRadius = NeighborhoodRadius,
                raysPerFoundationSample = 5, neighborhoodSelection = "highest actual hit within center and +/-3mm X/Z offsets; all five results retained",
                raycastScope = "the corresponding mountain instance only",
                installed = jobs.Count(j => j.safe), skipped = jobs.Count(j => !j.safe) + preparationFailures.Count,
                preparationFailures, instances = reports, stairs };
            string json = JsonConvert.SerializeObject(result, Formatting.Indented);
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            File.WriteAllText(output, json + "\n");
            return json;
        }
        catch { Undo.RevertAllDownToGroup(undoGroup); throw; }
    }

    static Job Prepare(Scene scene, JObject entry)
    {
        string name = (string)entry["name"], buildingName = (string)entry["building"], supportPath = (string)entry["supportRoot"];
        Transform support = Find(scene, supportPath);
        Transform mountain = support == null ? null : support.Find(name);
        Transform building = Find(scene, buildingName != null && buildingName.StartsWith(Architecture + "/", StringComparison.Ordinal)
            ? buildingName : Architecture + "/" + buildingName);
        if (support == null || mountain == null || building == null) throw new InvalidOperationException("Missing support/mountain/building.");
        if (!supportPath.StartsWith(Mountains + "/", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected support path.");
        RequireAxisAligned(support);
        if (TriangleCount(mountain) != OriginalTriangles) throw new InvalidOperationException("Expected original mountain with exactly 25380 triangles.");
        if (!mountain.gameObject.activeInHierarchy) throw new InvalidOperationException("Mountain instance is inactive.");
        Transform previous = support.Find("FittedFoundation");
        RequireGeneratedOrAbsent(previous);
        string source;
        Bounds footprint = BuildingFootprint(building, out source);
        Renderer[] modelRenderers = building.GetComponentsInChildren<Renderer>(true).Where(r => r.enabled && r.gameObject.activeInHierarchy &&
            !HasAncestorName(r.transform, building, "RetainedWalkableSurfaces") && IsTripoModel(r.transform, building)).ToArray();
        if (modelRenderers.Length == 0) throw new InvalidOperationException("No active Tripo building-model renderers were found.");
        float modelBottom = modelRenderers.Min(r => r.bounds.min.y);
        Bounds rockBounds = RenderBounds(mountain);
        MeshCollider[] rocks = mountain.GetComponentsInChildren<MeshCollider>(true).Where(c => c.enabled && c.gameObject.activeInHierarchy && !c.isTrigger).ToArray();
        if (rocks.Length == 0) throw new InvalidOperationException("No active Tripo mountain MeshCollider.");
        var job = new Job { name = name, buildingName = building.name, support = support, mountain = mountain, building = building,
            footprintSource = source, footprint = footprint, rockBounds = rockBounds, top = modelBottom + BuildingOverlap,
            width = footprint.size.x * FootprintScale, depth = footprint.size.z * FootprintScale, rocks = rocks,
            mountainFingerprint = Fingerprint(mountain), buildingFingerprint = Fingerprint(building) };
        float castTop = Mathf.Max(job.top, rockBounds.max.y) + 2;
        float castBottom = Mathf.Min(job.top, rockBounds.min.y) - 2;
        for (int z = 0; z < Grid; z++)
            for (int x = 0; x < Grid; x++)
            {
                float px = footprint.center.x + Mathf.Lerp(-job.width * .5f, job.width * .5f, x / (Grid - 1f));
                float pz = footprint.center.z + Mathf.Lerp(-job.depth * .5f, job.depth * .5f, z / (Grid - 1f));
                job.probes.Add(CastNeighborhood(rocks, px, pz, castTop, castTop - castBottom, "grid_" + x + "_" + z));
            }
        int misses = job.probes.Count(p => !p.hit);
        if (misses > 0) job.issues.Add(misses + "/81 samples miss this rock; foundation skipped to avoid unsupported overhangs.");
        Probe[] hits = job.probes.Where(p => p.hit).ToArray();
        if (hits.Length > 0) job.bottom = hits.Min(p => p.height.Value) - RockPenetration;
        else job.bottom = job.top;
        if (job.top - job.bottom <= SnowThickness + .03f) job.issues.Add("Insufficient positive height for a visible stone/snow foundation.");
        int protrusions = hits.Count(p => p.height.Value > job.top + .025f);
        if (protrusions > 0) job.warnings.Add("Rock protrudes above proposed foundation top at " + protrusions + "/81 samples; terrain/building intersection needs visual review.");
        if (job.top - job.bottom > 8) job.warnings.Add("Foundation exceeds 8 meters in height; inspect the resulting masonry silhouette.");
        if (job.top > footprint.min.y + .1f) job.warnings.Add("Model bottom is above the footprint bottom; check retained floor overlap.");
        job.safe = job.issues.Count == 0;
        return job;
    }

    static object CheckFoundationTop(Job job)
    {
        BoxCollider[] boxes = job.foundation.GetComponentsInChildren<BoxCollider>(true);
        var checks = new List<object>();
        foreach (Probe p in job.probes)
        {
            // Slightly inset the top audit from exact collider edges to avoid PhysX edge ambiguity.
            Vector3 origin = new Vector3(Mathf.Lerp(job.footprint.center.x, p.x, .999f), job.top + 1,
                Mathf.Lerp(job.footprint.center.z, p.z, .999f));
            float highest = float.NegativeInfinity;
            foreach (BoxCollider collider in boxes)
            { RaycastHit hit; if (collider.Raycast(new Ray(origin, Vector3.down), out hit, job.top - job.bottom + 2)) highest = Mathf.Max(highest, hit.point.y); }
            checks.Add(new { p.label, hit = !float.IsNegativeInfinity(highest), height = float.IsNegativeInfinity(highest) ? (float?)null : highest,
                matchesVisibleTop = !float.IsNegativeInfinity(highest) && Mathf.Abs(highest - job.top) < .003f });
        }
        return checks;
    }

    static object BuildOptionalStairs(Scene scene, JObject layout, List<Job> jobs, Material stone, Material snow)
    {
        if ((bool?)layout["foundations"]?["buildBridgeStairs"] != true)
            return new { status = "not_requested", enableWith = "foundations.buildBridgeStairs=true" };
        Job far = jobs.SingleOrDefault(j => j.buildingName == "FarPavilion" && j.safe && j.foundation != null);
        Transform bridge = Find(scene, "TianNanGe_Blockout/Bridge");
        Transform abutment = bridge == null ? null : bridge.Find("BridgeConnections/FarAbutment");
        if (far == null || abutment == null) return new { status = "skipped", reason = "Far foundation or FarAbutment missing." };
        RequireAxisAligned(bridge);
        Transform old = bridge.Find("FarPavilionStoneStairs");
        RequireGeneratedOrAbsent(old);
        Bounds abutmentBounds = RenderBounds(abutment);
        float startY = abutmentBounds.max.y;
        Transform landing = bridge.Find("BridgeConnections/FarLandingCollision");
        if (landing != null)
        {
            Collider landingCollider = landing.GetComponent<Collider>();
            if (landingCollider != null && landingCollider.enabled) startY = landingCollider.bounds.max.y;
        }
        float startZ = abutmentBounds.max.z, endZ = far.footprint.center.z - far.depth * .5f;
        float startX = abutmentBounds.center.x, endX = far.footprint.center.x;
        float endY = far.top, width = 2.7f;
        const int count = 28;
        float run = endZ - startZ, rise = endY - startY;
        if (run <= 0 || rise <= 0 || run / count < .16f || rise / count > .24f)
            return new { status = "skipped", reason = "Connection needs more detailed stair layout.", startZ, endZ, startY, endY, run, rise };
        // Retain real active procedural bridge land as well as Tripo rocks. The
        // replaced four legacy mountain colliders remain excluded by enabled=false.
        MeshCollider[] allRocks = Find(scene, Mountains).GetComponentsInChildren<MeshCollider>(true)
            .Where(c => c.enabled && c.gameObject.activeInHierarchy && !c.isTrigger &&
                c.sharedMesh != null && c.GetComponent<MeshFilter>() != null &&
                c.GetComponent<MeshFilter>().sharedMesh == c.sharedMesh).ToArray();
        float castY = Mathf.Max(endY, allRocks.Length == 0 ? endY : allRocks.Max(c => c.bounds.max.y)) + 2;
        float castBottom = Mathf.Min(startY, allRocks.Length == 0 ? startY : allRocks.Min(c => c.bounds.min.y)) - 2;
        var samples = new List<object>();
        var bottoms = new List<float>();
        bool unsafeEdges = false;
        for (int step = 0; step < count; step++)
        {
            float t = (step + .5f) / count, x = Mathf.Lerp(startX, endX, t), z = Mathf.Lerp(startZ, endZ, t);
            float top = Mathf.Lerp(startY, endY, (step + 1f) / count);
            foreach (float side in new[] { -.5f, 0f, .5f })
            {
                Probe p = Cast(allRocks, x + width * side, z, castY, castY - castBottom, "step_" + step + "_side_" + side);
                bool protrudes = p.hit && p.height.Value > top + .04f;
                if (!p.hit || protrudes) unsafeEdges = true;
                if (p.hit) bottoms.Add(p.height.Value - RockPenetration);
                samples.Add(new { step = step + 1, side, stepTop = top, p.hit, rockHeight = p.height, p.normal, p.collider,
                    gap = p.hit ? (float?)(top - p.height.Value) : null, protrudesAboveStep = protrudes });
            }
        }
        if (unsafeEdges || bottoms.Count == 0)
            return new { status = "skipped_needs_manual_edge_fit", reason = "One or more center/left/right rock samples miss or intersect a stair tread; no staircase changed.",
                startZ, endZ, startY, endY, width, steps = count, raycastColliders = allRocks.Select(c => Hierarchy(c.transform)).ToArray(), samples };
        float baseY = Mathf.Min(bottoms.Min(), startY - RockPenetration);
        Transform root = ReplaceGeneratedRoot(bridge, "FarPavilionStoneStairs");
        FitUnitWorldTransform(root, new Vector3(0, baseY, 0));
        for (int i = 0; i < count; i++)
        {
            float t = (i + .5f) / count, x = Mathf.Lerp(startX, endX, t), z = Mathf.Lerp(startZ, endZ, t);
            float top = Mathf.Lerp(startY, endY, (i + 1f) / count), stepHeight = top - baseY;
            // Full-depth masonry columns are visible and grounded; no invisible ramp collider.
            Box(root, "StepStone_" + (i + 1).ToString("00"), new Vector3(x, (stepHeight - SnowThickness) * .5f, z),
                new Vector3(width, stepHeight - SnowThickness, run / count + .003f), stone);
            Box(root, "StepSnow_" + (i + 1).ToString("00"), new Vector3(x, stepHeight - SnowThickness * .5f, z),
                new Vector3(width, SnowThickness, run / count + .003f), snow);
        }
        return new { status = "installed", startZ, endZ, startY, endY, startX, endX, width, steps = count,
            riser = rise / count, tread = run / count, masonryBottom = baseY, visibleColliderBoxes = count * 2,
            raycastColliders = allRocks.Select(c => Hierarchy(c.transform)).ToArray(), samples };
    }

    static Transform ReplaceGeneratedRoot(Transform parent, string name)
    {
        Transform old = parent.Find(name);
        RequireGeneratedOrAbsent(old);
        if (old != null) Undo.DestroyObjectImmediate(old.gameObject);
        var go = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(go, "Create fitted visible masonry");
        go.transform.SetParent(parent, false);
        var marker = new GameObject(Marker);
        Undo.RegisterCreatedObjectUndo(marker, "Mark generated masonry");
        marker.transform.SetParent(go.transform, false);
        return go.transform;
    }
    static void RequireGeneratedOrAbsent(Transform root)
    { if (root != null && root.Find(Marker) == null) throw new InvalidOperationException("Refusing to replace unrelated/manual object " + Hierarchy(root)); }
    static void FitUnitWorldTransform(Transform root, Vector3 worldPosition)
    {
        RequireAxisAligned(root.parent);
        Vector3 scale = root.parent.lossyScale;
        root.localRotation = Quaternion.identity;
        root.localScale = new Vector3(1 / scale.x, 1 / scale.y, 1 / scale.z);
        root.position = worldPosition;
    }
    static void Box(Transform parent, string name, Vector3 position, Vector3 size, Material material)
    {
        if (size.x <= 0 || size.y <= 0 || size.z <= 0) throw new InvalidOperationException("Masonry solid dimensions must be positive.");
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Undo.RegisterCreatedObjectUndo(go, "Create visible collision masonry");
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = position;
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale = size;
        go.GetComponent<MeshRenderer>().sharedMaterial = material;
        go.GetComponent<BoxCollider>().isTrigger = false;
    }
    static Probe Cast(MeshCollider[] colliders, float x, float z, float originY, float distance, string label)
    {
        var p = new Probe { label = label, x = x, z = z };
        foreach (MeshCollider collider in colliders)
        {
            RaycastHit hit;
            if (collider.Raycast(new Ray(new Vector3(x, originY, z), Vector3.down), out hit, distance) &&
                (!p.hit || hit.point.y > p.height.Value))
            { p.hit = true; p.height = hit.point.y; p.normal = Numbers(hit.normal); p.collider = Hierarchy(collider.transform); }
        }
        return p;
    }
    static Probe CastNeighborhood(MeshCollider[] colliders, float x, float z, float originY, float distance, string label)
    {
        Vector2[] offsets = { Vector2.zero, new Vector2(-NeighborhoodRadius, 0), new Vector2(NeighborhoodRadius, 0),
            new Vector2(0, -NeighborhoodRadius), new Vector2(0, NeighborhoodRadius) };
        var probes = offsets.Select(offset => Cast(colliders, x + offset.x, z + offset.y, originY, distance, label)).ToArray();
        Probe[] hits = probes.Where(probe => probe.hit).ToArray();
        Probe best = hits.OrderByDescending(probe => probe.height.Value).FirstOrDefault();
        var result = new Probe { label = label, x = x, z = z, hit = best != null,
            height = best == null ? null : best.height, normal = best == null ? null : best.normal,
            collider = best == null ? null : best.collider, neighborhoodRadius = NeighborhoodRadius,
            selectedOffset = best == null ? null : new[] { best.x - x, best.z - z }, neighborhoodHits = hits.Length,
            neighborhoodSamples = probes.Select((probe, index) => (object)new { offset = new[] { offsets[index].x, offsets[index].y },
                probe.hit, height = probe.height, probe.normal, probe.collider }).ToArray() };
        // A gap wider than this six-millimeter footprint still yields five misses,
        // or a low surface in all five rays; neither is discarded as an outlier.
        return result;
    }
    static Bounds BuildingFootprint(Transform building, out string path)
    {
        if (Pavilions.Contains(building.name))
        {
            Transform stone = building.Find("Proxy_ReplaceWithTripo/StoneBase");
            if (stone == null) throw new InvalidOperationException("Missing hidden StoneBase for " + building.name);
            path = Hierarchy(stone);
            return RenderBounds(stone);
        }
        var floors = building.GetComponentsInChildren<Transform>(true).Where(t => t.name == "RetainedWalkableSurfaces")
            .SelectMany(t => t.GetComponentsInChildren<Transform>(true)).Where(t => t.name == "Floor")
            .Where(t => t.GetComponentsInChildren<Renderer>(true).Length > 0)
            .Select(t => new { t, bounds = RenderBounds(t) }).OrderBy(t => t.bounds.min.y)
            .ThenByDescending(t => t.bounds.size.x * t.bounds.size.z).ToArray();
        if (floors.Length == 0) throw new InvalidOperationException("No retained lowest floor for " + building.name);
        path = Hierarchy(floors[0].t);
        return floors[0].bounds;
    }
    static bool IsTripoModel(Transform transform, Transform stop)
    {
        for (Transform t = transform; t != null && t != stop; t = t.parent)
        {
            string path = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(t.gameObject);
            if (!string.IsNullOrEmpty(path) && path.StartsWith("Assets/TianNanGe/Tripo/", StringComparison.Ordinal)) return true;
            if (t.name.StartsWith("Tripo_", StringComparison.Ordinal) || t.name.EndsWith("_H31_8K", StringComparison.Ordinal)) return true;
        }
        return false;
    }
    static bool HasAncestorName(Transform t, Transform stop, string name)
    { for (; t != null && t != stop; t = t.parent) if (t.name == name) return true; return false; }
    static string Fingerprint(Transform root)
    {
        return string.Join("\n", root.GetComponentsInChildren<Transform>(true).Select(t => Hierarchy(t) + "|" +
            EditorJsonUtility.ToJson(t) + "|" + t.gameObject.activeSelf)) + "\n" +
            string.Join("\n", root.GetComponentsInChildren<MeshFilter>(true).Select(f => Hierarchy(f.transform) + "|" +
                (f.sharedMesh == null ? "missing" : f.sharedMesh.GetInstanceID().ToString() + ":" + f.sharedMesh.vertexCount)));
    }
    static long TriangleCount(Transform root)
    {
        long total = 0;
        foreach (MeshFilter f in root.GetComponentsInChildren<MeshFilter>(true))
        {
            if (f.sharedMesh == null) throw new InvalidOperationException("Missing original mountain mesh.");
            for (int i = 0; i < f.sharedMesh.subMeshCount; i++) total += f.sharedMesh.GetIndexCount(i) / 3;
        }
        return total;
    }
    static Bounds RenderBounds(Transform root)
    {
        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0) throw new InvalidOperationException("No render bounds: " + Hierarchy(root));
        Bounds bounds = renderers[0].bounds;
        foreach (Renderer renderer in renderers.Skip(1)) bounds.Encapsulate(renderer.bounds);
        return bounds;
    }
    static void RequireAxisAligned(Transform root)
    {
        if (root == null || Quaternion.Angle(root.rotation, Quaternion.identity) > .01f ||
            root.lossyScale.x <= 0 || root.lossyScale.y <= 0 || root.lossyScale.z <= 0)
            throw new InvalidOperationException("Positive, unrotated parent required for world-aligned masonry.");
    }
    static Transform Find(Scene scene, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string[] pieces = path.Split('/');
        GameObject root = scene.GetRootGameObjects().SingleOrDefault(g => g.name == pieces[0]);
        return root == null ? null : pieces.Length == 1 ? root.transform : root.transform.Find(string.Join("/", pieces.Skip(1)));
    }
    static string Hierarchy(Transform t)
    { string text = t.name; while (t.parent != null) { t = t.parent; text = t.name + "/" + text; } return text; }
    static float[] Numbers(Vector3 v) { return new[] { v.x, v.y, v.z }; }
    static object Describe(Bounds b) { return new { center = Numbers(b.center), size = Numbers(b.size), min = Numbers(b.min), max = Numbers(b.max) }; }
}
