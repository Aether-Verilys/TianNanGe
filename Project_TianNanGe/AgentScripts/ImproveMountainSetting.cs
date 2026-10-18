using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;

// Run with Unity Pipeline run_script. This editor-only builder intentionally
// refuses to replace an existing Mountains object or prefab so hand edits stay
// intact. It creates a separate terrain group and leaves the existing pavilion
// prefab instances and camera untouched.
public static class ImproveMountainSetting
{
    const string RootPath = "Assets/TianNanGe";
    const string ScenePath = RootPath + "/Scenes/TianNanGe_Main.unity";
    const string PrefabPath = RootPath + "/Prefabs/Environment/Mountains.prefab";
    const string MeshPath = RootPath + "/Meshes";
    const string MaterialPath = RootPath + "/Materials";

    static Material rockMaterial;
    static Material snowMaterial;

    public static string Improve()
    {
        Scene scene = SceneManager.GetActiveScene();
        if (!scene.IsValid() || string.IsNullOrEmpty(scene.path))
            throw new InvalidOperationException("Open the saved TianNanGe_Main scene before running ImproveMountainSetting.Improve.");
        if (scene.path != ScenePath)
            throw new InvalidOperationException("ImproveMountainSetting only edits " + ScenePath + ".");
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Exit Play Mode before creating mountain assets.");

        Transform blockout = scene.GetRootGameObjects().Length == 0
            ? null
            : GameObject.Find("TianNanGe_Blockout")?.transform;
        if (blockout == null)
            throw new InvalidOperationException("TianNanGe_Blockout root is missing.");
        if (blockout.Find("Mountains") != null)
            throw new InvalidOperationException("Mountains already exists in the scene; refusing to overwrite hand edits.");
        if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) != null)
            throw new InvalidOperationException("Mountains prefab already exists; refusing to overwrite it.");
        Transform architecture = blockout.Find("Architecture");
        foreach (string pavilionName in new[] { "MainPavilion", "LeftPavilion", "FarPavilion", "FarRight" })
            if (architecture == null || architecture.Find(pavilionName + "/Proxy_ReplaceWithTripo/StoneBase") == null)
                throw new InvalidOperationException("Missing pavilion stone base: " + pavilionName);

        Directory.CreateDirectory(MeshPath);
        Directory.CreateDirectory(MaterialPath);
        AssetDatabase.Refresh();
        rockMaterial = LoadOrCreateMaterial("MountainRock", new Color(0.16f, 0.22f, 0.25f), 0.12f);
        snowMaterial = AssetDatabase.LoadAssetAtPath<Material>(RootPath + "/Materials/Snow.mat");
        if (snowMaterial == null)
            snowMaterial = LoadOrCreateMaterial("MountainSnow", new Color(0.78f, 0.86f, 0.87f), 0.2f);

        Transform mountains = new GameObject("Mountains").transform;
        mountains.SetParent(blockout, false);

        // The four peaks rise into the existing pavilion bases. Their broad
        // upper terraces keep the bases grounded while the lower rings create
        // faceted cliffs instead of spherical piles.
        CreatePavilionMountain(mountains, architecture.Find("MainPavilion"), "Mountain_Main", 18f, 0.15f);
        CreatePavilionMountain(mountains, architecture.Find("LeftPavilion"), "Mountain_Left", 20f, 0.44f);
        CreatePavilionMountain(mountains, architecture.Find("FarPavilion"), "Mountain_Far", 24f, 1.08f);
        CreatePavilionMountain(mountains, architecture.Find("FarRight"), "Mountain_FarRight", 22f, 1.71f);

        // Landings connect the future extended bridge to solid ground. They
        // remain independent children so bridge length/shape can be adjusted
        // later without touching this prefab.
        CreateMountain(mountains, "NearBridgeLand", new Vector3(0.35f, -21f, -20f), 15.2f, 20.94f, 20, 2.24f);
        CreateMountain(mountains, "FarBridgeLand", new Vector3(0.35f, -24f, 24.5f), 15.2f, 23.94f, 20, 2.83f);

        // Keep the old flat courtyard in the scene for reference, but remove
        // it from rendering/collision so it cannot make the mountains float.
        Transform oldCourtyard = blockout.Find("Architecture/SnowCourtyard");
        if (oldCourtyard != null)
        {
            oldCourtyard.gameObject.SetActive(false);
            PrefabUtility.RecordPrefabInstancePropertyModifications(oldCourtyard.gameObject);
        }

        AssetDatabase.SaveAssets();
        PrefabUtility.SaveAsPrefabAssetAndConnect(mountains.gameObject, PrefabPath, InteractionMode.AutomatedAction);
        EditorSceneManager.MarkSceneDirty(scene);
        if (!EditorSceneManager.SaveScene(scene))
            throw new InvalidOperationException("Could not save " + ScenePath + ".");
        return "Created " + PrefabPath + "; six faceted mountain/bridge landing groups; SnowCourtyard disabled.";
    }

    static Material LoadOrCreateMaterial(string name, Color color, float smoothness)
    {
        string path = MaterialPath + "/" + name + ".mat";
        Material existing = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (existing != null)
            return existing;
        Shader shader = Shader.Find("HDRP/Lit");
        if (shader == null)
            throw new InvalidOperationException("HDRP/Lit shader is missing.");
        Material material = new Material(shader) { name = name };
        material.SetColor("_BaseColor", color);
        material.SetFloat("_Smoothness", smoothness);
        HDMaterial.ValidateMaterial(material);
        AssetDatabase.CreateAsset(material, path);
        return material;
    }

    static void CreatePavilionMountain(Transform mountains, Transform pavilion, string name, float cliffHeight, float phase)
    {
        Renderer stoneBase = pavilion.Find("Proxy_ReplaceWithTripo/StoneBase").GetComponent<Renderer>();
        if (stoneBase == null)
            throw new InvalidOperationException("StoneBase renderer missing on " + pavilion.name);
        Bounds b = stoneBase.bounds;
        // Make even the narrowest edge of the irregular snow plateau reach
        // beyond all four building-base corners. The snow overlaps the stone
        // by 6 cm so no seam can read as a floating building.
        float supportRadius = new Vector2(b.extents.x, b.extents.z).magnitude + 0.35f;
        float baseRadius = supportRadius / (0.36f * 0.85f);
        float rockTop = b.min.y - 0.06f;
        Vector3 basePosition = mountains.InverseTransformPoint(new Vector3(b.center.x, rockTop - cliffHeight, b.center.z));
        CreateMountain(mountains, name, basePosition, baseRadius, cliffHeight, 20, phase);
    }

    static void CreateMountain(Transform parent, string name, Vector3 position,
        float baseRadius, float height, int sides, float phase)
    {
        Transform root = new GameObject(name).transform;
        root.SetParent(parent, false);
        root.localPosition = position;

        Mesh rockMesh = CreateLayeredMesh(name + "_Rock", baseRadius, height, sides, phase,
            new[] { 0f, 0.13f, 0.39f, 0.68f, 0.88f, 1f },
            new[] { 1.00f, 1.04f, 0.91f, 0.70f, 0.50f, 0.40f });
        GameObject rock = new GameObject("Rock", typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider));
        rock.transform.SetParent(root, false);
        rock.GetComponent<MeshFilter>().sharedMesh = rockMesh;
        rock.GetComponent<MeshRenderer>().sharedMaterial = rockMaterial;
        rock.GetComponent<MeshCollider>().sharedMesh = rockMesh;

        // A second, raised upper layer gives the snow a thick, readable edge.
        // Its irregular low-poly profile follows the rock but stops short of
        // the cliff base, preserving a visible dark rock band.
        Mesh snowMesh = CreateLayeredMesh(name + "_SnowCap", baseRadius, height + 0.12f, sides, phase + 0.17f,
            new[] { 0.86f, 0.94f, 0.992f, 1f },
            new[] { 0.515f, 0.455f, 0.405f, 0.36f });
        GameObject snow = new GameObject("SnowCap", typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider));
        snow.transform.SetParent(root, false);
        snow.GetComponent<MeshFilter>().sharedMesh = snowMesh;
        snow.GetComponent<MeshRenderer>().sharedMaterial = snowMaterial;
        snow.GetComponent<MeshCollider>().sharedMesh = snowMesh;
    }

    static Mesh CreateLayeredMesh(string name, float radius, float height, int sides,
        float phase, float[] heights, float[] radiusFractions)
    {
        List<Vector3> vertices = new List<Vector3>(heights.Length * sides + 2);
        List<int> triangles = new List<int>();
        for (int ring = 0; ring < heights.Length; ring++)
        {
            float y = height * heights[ring];
            float ringRadius = radius * radiusFractions[ring];
            for (int side = 0; side < sides; side++)
            {
                float angle = side * Mathf.PI * 2f / sides;
                // Two low-frequency terms produce ledges and an asymmetric
                // silhouette while keeping each ring continuous.
                float variation = 1f + 0.10f * Mathf.Sin(angle * 3f + phase + ring * 0.19f)
                    + 0.045f * Mathf.Sin(angle * 7f - phase * 1.7f);
                vertices.Add(new Vector3(Mathf.Cos(angle) * ringRadius * variation,
                    y, Mathf.Sin(angle) * ringRadius * variation));
            }
        }

        for (int ring = 0; ring < heights.Length - 1; ring++)
        {
            int current = ring * sides;
            int next = (ring + 1) * sides;
            for (int side = 0; side < sides; side++)
            {
                int a = current + side;
                int b = current + (side + 1) % sides;
                int c = next + side;
                int d = next + (side + 1) % sides;
                triangles.Add(a); triangles.Add(c); triangles.Add(b);
                triangles.Add(b); triangles.Add(c); triangles.Add(d);
            }
        }

        // Close the underside and top terrace. The top is deliberately a
        // polygonal plateau so a pavilion's stone base can meet the mountain.
        int bottom = -1;
        if (heights[0] <= 0.001f)
        {
            bottom = vertices.Count;
            vertices.Add(Vector3.zero);
        }
        int top = vertices.Count;
        vertices.Add(new Vector3(0f, height, 0f));
        for (int side = 0; side < sides; side++)
        {
            int next = (side + 1) % sides;
            if (bottom >= 0)
            {
                triangles.Add(bottom); triangles.Add(side); triangles.Add(next);
            }
            int topRing = (heights.Length - 1) * sides;
            triangles.Add(top); triangles.Add(topRing + next); triangles.Add(topRing + side);
        }

        // Split triangle vertices so each rock plane has a crisp normal.
        // Flat facets keep the broad cliff breaks visible at blockout scale.
        List<Vector3> flatVertices = new List<Vector3>(triangles.Count);
        List<int> flatTriangles = new List<int>(triangles.Count);
        foreach (int index in triangles)
        {
            flatTriangles.Add(flatVertices.Count);
            flatVertices.Add(vertices[index]);
        }
        Mesh mesh = new Mesh { name = name };
        mesh.SetVertices(flatVertices);
        mesh.SetTriangles(flatTriangles, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        string path = AssetDatabase.GenerateUniqueAssetPath(MeshPath + "/" + name + ".asset");
        AssetDatabase.CreateAsset(mesh, path);
        return mesh;
    }
}
