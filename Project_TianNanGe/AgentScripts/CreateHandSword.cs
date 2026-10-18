using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Rendering.HighDefinition;

// Generates a compact, stylised Chinese straight sword as an independent prefab.
// Run from the Unity Pipeline with CreateHandSword.BuildAsset(). The prefab root is
// the centre of the grip. In local space the blade point is +Y; rotate the root
// 180 degrees around X when hanging the sheathed sword down from a hand.
public static class CreateHandSword
{
    const string Root = "Assets/TianNanGe";
    const string MaterialRoot = Root + "/Materials/Props";
    const string MeshRoot = Root + "/Meshes/Props";
    const string PrefabPath = Root + "/Prefabs/Props/HandSword.prefab";

    static Material steel;
    static Material edge;
    static Material lacquer;
    static Material gold;
    static Material wrap;
    static Material tassel;
    static int meshIndex;
    const float ModelScale = .80f;
    const float GripCentreY = -.16f;

    public static string BuildAsset()
    {
        if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) != null)
            return "HandSword already exists at " + PrefabPath + "; no assets were changed.";

        EnsureFolder(MaterialRoot);
        EnsureFolder(MeshRoot);
        EnsureFolder(Root + "/Prefabs/Props");
        AssetDatabase.Refresh();

        meshIndex = 0;
        steel = CreateMaterial("HandSword_Steel", new Color(.34f, .40f, .44f), .55f, .92f);
        edge = CreateMaterial("HandSword_Edge", new Color(.72f, .80f, .82f), .75f, .94f);
        lacquer = CreateMaterial("HandSword_Scabbard", new Color(.035f, .055f, .075f), .42f, .08f);
        gold = CreateMaterial("HandSword_Gold", new Color(.56f, .30f, .075f), .60f, .85f);
        wrap = CreateMaterial("HandSword_Wrap", new Color(.11f, .19f, .20f), .25f, .00f);
        tassel = CreateMaterial("HandSword_Tassel", new Color(.52f, .08f, .06f), .22f, .00f);

        var previewScene = EditorSceneManager.NewPreviewScene();
        GameObject prefab = null;
        try
        {
            var root = new GameObject("HandSword");
            SceneManager.MoveGameObjectToScene(root, previewScene);
            var scabbard = Group("Scabbard", root.transform);
            BuildScabbard(scabbard);
            var blade = Group("Blade", root.transform);
            BuildBlade(blade);
            blade.gameObject.SetActive(false);
            var hilt = Group("Hilt", root.transform);
            BuildHilt(hilt);
            var tasselGroup = Group("Tassel", root.transform);
            BuildTassel(tasselGroup);

            // Geometry is authored in a common sword coordinate system. Bake the
            // final scale and grip offset into leaf transforms, leaving root/groups
            // at identity for convenient hand attachment and variant toggling.
            foreach (Transform group in root.transform)
            foreach (Transform part in group)
            {
                part.localPosition = (part.localPosition - Vector3.up * GripCentreY) * ModelScale;
                part.localScale *= ModelScale;
            }
            prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        }
        finally
        {
            EditorSceneManager.ClosePreviewScene(previewScene);
        }
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        if (prefab == null) throw new InvalidOperationException("Could not save " + PrefabPath);
        return "Saved " + PrefabPath + "; body length=1.0 m, pivot=grip centre, local +Y=tip; " +
               "Scabbard active, Blade available but disabled. No scene objects were changed.";
    }

    static void BuildScabbard(Transform parent)
    {
        // The visible sheath runs forward from the mouth at y=.07 to a tapered cap at y=.86.
        MeshObject("ScabbardBody", parent,
            TubeMesh(new[] { .07f, .15f, .58f, .79f, .87f },
                     new[] { .036f, .035f, .032f, .028f, .006f }, 8, .40f), lacquer);
        Cylinder("ScabbardMouth", parent, new Vector3(0, .075f, 0), .043f, .034f, gold, flatten: .45f);
        Cylinder("ScabbardMouthInset", parent, new Vector3(0, .094f, 0), .037f, .008f, lacquer, flatten: .45f);
        Cylinder("ScabbardEndCap", parent, new Vector3(0, .815f, 0), .030f, .055f, gold, flatten: .45f);
        // A small leather suspension band gives the otherwise simple sheath a readable break.
        Cylinder("ScabbardBand", parent, new Vector3(0, .255f, 0), .037f, .024f, gold, flatten: .45f);
        Cylinder("ScabbardBandInset", parent, new Vector3(0, .255f, 0), .038f, .010f, wrap, flatten: .45f);
    }

    static void BuildBlade(Transform parent)
    {
        // Double-edged, slightly fuller blade. The tip and cutting edge both point +Y.
        MeshObject("BladeMesh", parent,
            BladeMesh(new[] { .10f, .17f, .61f, .79f, .88f },
                      new[] { .027f, .030f, .027f, .022f, .001f },
                      new[] { .006f, .007f, .006f, .005f, .001f }), edge);
        MeshObject("BladeFuller", parent,
            FullerMesh(.17f, .61f), steel);
        Cylinder("BladeShoulder", parent, new Vector3(0, .102f, 0), .031f, .025f, gold, flatten: .40f);
    }

    static void BuildHilt(Transform parent)
    {
        MeshObject("GripCore", parent,
            TubeMesh(new[] { -.285f, -.225f, -.095f, -.035f },
                     new[] { .021f, .023f, .023f, .021f }, 12, .78f), wrap);
        // Counterwound raised cord gives a recognizable wrapped grip at close range.
        for (int hand = 0; hand < 2; hand++)
        {
            var points = new List<Vector3>();
            for (int i = 0; i <= 96; i++)
            {
                float t = i / 96f;
                float a = t * Mathf.PI * 12f * (hand == 0 ? 1 : -1);
                points.Add(new Vector3(Mathf.Cos(a) * .024f, -.275f + t * .235f,
                    Mathf.Sin(a) * .019f));
            }
            MeshObject("GripBinding_" + hand, parent, CurveMesh(points, .0025f, 5), lacquer);
        }
        Cylinder("GripFrontCollar", parent, new Vector3(0, -.038f, 0), .027f, .018f, gold, flatten: .8f);
        Cylinder("GripRearCollar", parent, new Vector3(0, -.288f, 0), .027f, .018f, gold, flatten: .8f);

        Cylinder("BladeFerrule", parent, new Vector3(0, .055f, 0), .034f, .095f, gold, flatten: .42f);

        // Upturned quillons, a dark jade inset and small metal end ornaments.
        var guardPoints = new List<Vector3>
        {
            new Vector3(-.125f, -.038f, 0), new Vector3(-.09f, -.018f, 0),
            new Vector3(-.04f, .002f, 0), new Vector3(0, .01f, 0),
            new Vector3(.04f, .002f, 0), new Vector3(.09f, -.018f, 0),
            new Vector3(.125f, -.038f, 0)
        };
        MeshObject("Guard", parent, CurveMesh(guardPoints, .014f, 8), gold);
        Sphere("GuardCentre", parent, new Vector3(0, .008f, 0), new Vector3(.076f, .044f, .052f), gold);
        Sphere("GuardJadeInset", parent, new Vector3(0, .009f, -.027f), new Vector3(.027f, .020f, .009f), wrap);
        Sphere("GuardLeftFinial", parent, guardPoints[0], new Vector3(.032f, .027f, .032f), gold);
        Sphere("GuardRightFinial", parent, guardPoints[6], new Vector3(.032f, .027f, .032f), gold);
        Sphere("Pommel", parent, new Vector3(0, -.328f, 0), new Vector3(.064f, .067f, .047f), gold);
        Sphere("PommelJadeInset", parent, new Vector3(0, -.328f, -.024f), new Vector3(.023f, .031f, .008f), wrap);
    }

    static void BuildTassel(Transform parent)
    {
        // The ornament falls in +Y when this sword is held point-down. Its cord
        // bends away from the pommel so it does not pass through the user's hand.
        var cord = new List<Vector3>
        {
            new Vector3(0, -.353f, 0), new Vector3(.040f, -.370f, 0),
            new Vector3(.075f, -.345f, 0), new Vector3(.085f, -.285f, .006f),
            new Vector3(.092f, -.220f, .010f)
        };
        MeshObject("TasselCord", parent, CurveMesh(cord, .0035f, 6), tassel);
        Sphere("TasselKnot", parent, cord[4], new Vector3(.022f, .025f, .022f), gold);
        for (int i = 0; i < 5; i++)
        {
            float x = .092f + (i - 2) * .005f;
            MeshObject("TasselStrand_" + i, parent,
                CurveMesh(new List<Vector3>
                {
                    new Vector3(x, -.205f, .010f),
                    new Vector3(x + .004f, -.155f, .013f),
                    new Vector3(x + (i - 2) * .002f, -.11f - Mathf.Abs(i - 2) * .006f, .017f)
                }, .0026f, 5), tassel);
        }
    }

    static Transform Group(string name, Transform parent)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        return go.transform;
    }

    static Material CreateMaterial(string name, Color baseColor, float smoothness, float metallic)
    {
        string path = MaterialRoot + "/" + name + ".mat";
        var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (existing != null) return existing;
        var shader = Shader.Find("HDRP/Lit") ?? Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        if (shader == null) throw new InvalidOperationException("No lit shader was found for " + name);
        var material = new Material(shader) { name = name };
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", baseColor);
        if (material.HasProperty("_Color")) material.SetColor("_Color", baseColor);
        if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", smoothness);
        if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", metallic);
        if (shader.name == "HDRP/Lit") HDMaterial.ValidateMaterial(material);
        AssetDatabase.CreateAsset(material, path);
        return material;
    }

    static void Cylinder(string name, Transform parent, Vector3 position, float radius, float height, Material material,
        float flatten = 1f)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = position;
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale = new Vector3(radius * 2f, height * .5f, radius * 2f * flatten);
        RemoveCollider(go);
        go.GetComponent<Renderer>().sharedMaterial = material;
    }

    static void Sphere(string name, Transform parent, Vector3 position, Vector3 scale, Material material)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = position;
        go.transform.localScale = scale;
        RemoveCollider(go);
        go.GetComponent<Renderer>().sharedMaterial = material;
    }

    static void MeshObject(string name, Transform parent, Mesh mesh, Material material)
    {
        string path = MeshRoot + "/" + meshIndex.ToString("D3") + "_HandSword_" + name + ".asset";
        mesh.name = "HandSword_" + name;
        AssetDatabase.CreateAsset(mesh, path);
        var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
        go.transform.SetParent(parent, false);
        go.GetComponent<MeshFilter>().sharedMesh = mesh;
        go.GetComponent<MeshRenderer>().sharedMaterial = material;
        meshIndex++;
    }

    static Mesh TubeMesh(float[] ys, float[] radii, int sides, float flatten = 1f)
    {
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        int rings = ys.Length;
        for (int i = 0; i < rings; i++)
        {
            for (int j = 0; j < sides; j++)
            {
                float a = Mathf.PI * 2f * j / sides;
                vertices.Add(new Vector3(Mathf.Cos(a) * radii[i], ys[i], Mathf.Sin(a) * radii[i] * flatten));
            }
        }
        for (int i = 0; i < rings - 1; i++)
            for (int j = 0; j < sides; j++)
            {
                int a = i * sides + j;
                int b = i * sides + (j + 1) % sides;
                int c = a + sides;
                int d = b + sides;
                triangles.AddRange(new[] { a, c, b, b, c, d });
            }
        AddCap(triangles, vertices, 0, sides, false);
        AddCap(triangles, vertices, (rings - 1) * sides, sides, true);
        return MakeMesh(vertices, triangles);
    }

    static Mesh BladeMesh(float[] ys, float[] widths, float[] thickness)
    {
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        for (int i = 0; i < ys.Length; i++)
        {
            vertices.Add(new Vector3(widths[i], ys[i], 0));
            vertices.Add(new Vector3(0, ys[i], thickness[i]));
            vertices.Add(new Vector3(-widths[i], ys[i], 0));
            vertices.Add(new Vector3(0, ys[i], -thickness[i]));
        }
        for (int i = 0; i < ys.Length - 1; i++)
        for (int j = 0; j < 4; j++)
        {
            int a = i * 4 + j, b = i * 4 + (j + 1) % 4;
            Quad(triangles, a, a + 4, b + 4, b);
        }
        AddCap(triangles, vertices, 0, 4, false);
        AddCap(triangles, vertices, (ys.Length - 1) * 4, 4, true);
        return MakeMesh(vertices, triangles);
    }

    static Mesh CurveMesh(List<Vector3> points, float radius, int sides)
    {
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        for (int i = 0; i < points.Count; i++)
        {
            Vector3 tangent = (points[Mathf.Min(i + 1, points.Count - 1)] - points[Mathf.Max(0, i - 1)]).normalized;
            Vector3 right = Vector3.Cross(tangent, Mathf.Abs(tangent.z) < .9f ? Vector3.forward : Vector3.up).normalized;
            Vector3 back = Vector3.Cross(right, tangent).normalized;
            for (int j = 0; j < sides; j++)
            {
                float a = j * Mathf.PI * 2f / sides;
                vertices.Add(points[i] + radius * (right * Mathf.Cos(a) + back * Mathf.Sin(a)));
            }
        }
        for (int i = 0; i < points.Count - 1; i++)
        for (int j = 0; j < sides; j++)
        {
            int a = i * sides + j, b = i * sides + (j + 1) % sides;
            Quad(triangles, a, a + sides, b + sides, b);
        }
        AddCap(triangles, vertices, 0, sides, false);
        AddCap(triangles, vertices, (points.Count - 1) * sides, sides, true);
        return MakeMesh(vertices, triangles);
    }

    static Mesh FullerMesh(float y0, float y1)
    {
        var vertices = new List<Vector3>
        {
            new Vector3(-.003f, y0, .0074f), new Vector3(.003f, y0, .0074f),
            new Vector3(-.002f, y1, .0064f), new Vector3(.002f, y1, .0064f),
            new Vector3(-.003f, y0, -.0074f), new Vector3(.003f, y0, -.0074f),
            new Vector3(-.002f, y1, -.0064f), new Vector3(.002f, y1, -.0064f)
        };
        return MakeMesh(vertices, new List<int> { 0, 1, 2, 1, 3, 2, 4, 6, 5, 5, 6, 7 });
    }

    static void Quad(List<int> triangles, int a, int b, int c, int d)
    {
        triangles.AddRange(new[] { a, b, c, a, c, d });
    }

    static void AddCap(List<int> triangles, List<Vector3> vertices, int offset, int sides, bool top)
    {
        int center = vertices.Count;
        Vector3 p = Vector3.zero;
        for (int j = 0; j < sides; j++) p += vertices[offset + j];
        p /= sides;
        vertices.Add(p);
        for (int j = 0; j < sides; j++)
        {
            int a = offset + j;
            int b = offset + (j + 1) % sides;
            if (top) triangles.AddRange(new[] { center, b, a });
            else triangles.AddRange(new[] { center, a, b });
        }
    }

    static Mesh MakeMesh(List<Vector3> vertices, List<int> triangles)
    {
        var mesh = new Mesh();
        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    static void RemoveCollider(GameObject go)
    {
        var collider = go.GetComponent<Collider>();
        if (collider != null) UnityEngine.Object.DestroyImmediate(collider);
    }

    static void EnsureFolder(string path)
    {
        string[] pieces = path.Split('/');
        string current = pieces[0];
        for (int i = 1; i < pieces.Length; i++)
        {
            string next = current + "/" + pieces[i];
            if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, pieces[i]);
            current = next;
        }
    }
}
