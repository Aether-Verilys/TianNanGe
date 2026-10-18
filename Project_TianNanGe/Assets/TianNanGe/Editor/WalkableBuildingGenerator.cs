using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Generates standalone, reusable Chinese-style walkable building prefabs.
/// Visual meshes do not carry colliders; simplified BoxColliders live below a
/// separate Collision child so door openings remain usable by the player.
/// </summary>
public static class WalkableBuildingGenerator
{
    private const string PrefabFolder = "Assets/TianNanGe/Prefabs/Architecture/Walkable";
    private const string MaterialsFolder = "Assets/TianNanGe/Materials";

    private static Material timber;
    private static Material roof;
    private static Material snow;
    private static Material stone;

    [MenuItem("TianNanGe/Generate Walkable Buildings")]
    public static void BuildAll()
    {
        EnsureFolder(PrefabFolder);
        LoadMaterials();

        BuildBuilding("TeaHouse_Walkable", 7.6f, 6.2f, 1, 2.8f, 2.0f, 0.35f, true);
        BuildBuilding("GuestHouse_Walkable", 9.0f, 7.0f, 2, 2.8f, 2.2f, 0.45f, true);
        BuildBuilding("WatchTower_Walkable", 6.2f, 6.2f, 3, 2.55f, 1.65f, 0.30f, false);
        BuildBuilding("MainHall_Walkable", 12.0f, 8.2f, 2, 3.15f, 2.8f, 0.55f, true);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("Generated walkable building prefabs in " + PrefabFolder);
    }

    private static void LoadMaterials()
    {
        timber = AssetDatabase.LoadAssetAtPath<Material>(MaterialsFolder + "/BlueTimber.mat");
        roof = AssetDatabase.LoadAssetAtPath<Material>(MaterialsFolder + "/Roof.mat");
        snow = AssetDatabase.LoadAssetAtPath<Material>(MaterialsFolder + "/Snow.mat");
        stone = AssetDatabase.LoadAssetAtPath<Material>(MaterialsFolder + "/Stone.mat");
        if (timber == null || roof == null || snow == null || stone == null)
            throw new InvalidOperationException("Walkable building generator needs BlueTimber, Roof, Snow and Stone materials.");
    }

    private static void BuildBuilding(string name, float width, float depth, int floors,
        float wallHeight, float doorWidth, float roofLift, bool addPorch)
    {
        var root = new GameObject(name);
        var visual = Child(root.transform, "Visual");
        var collision = Child(root.transform, "Collision");

        // A broad stone plinth makes the structure read as a snow-covered mountain building.
        Part(visual, "StonePlinth", new Vector3(0f, -0.18f, 0f), new Vector3(width + .5f, .36f, depth + .5f), stone);
        BoxColliderPart(collision, "Plinth", new Vector3(0f, -0.18f, 0f), new Vector3(width + .5f, .36f, depth + .5f));

        const float floorThickness = .18f;
        for (int level = 0; level < floors; level++)
        {
            float baseY = level * (wallHeight + floorThickness);
            BuildLevel(root.transform, visual, collision, width, depth, wallHeight, floorThickness,
                baseY, level == 0 ? doorWidth : Mathf.Min(doorWidth * .8f, width * .34f), level == 0);
        }

        // A short covered entry porch is kept open at the front, making the entrance obvious.
        if (addPorch)
            BuildPorch(visual, collision, width, depth, wallHeight);

        BuildRoof(visual, collision, width, depth, floors * (wallHeight + floorThickness) - floorThickness,
            roofLift, floors > 1 ? .72f : .58f);

        root.transform.position = Vector3.zero;
        string path = PrefabFolder + "/" + name + ".prefab";
        PrefabUtility.SaveAsPrefabAsset(root, path);
        UnityEngine.Object.DestroyImmediate(root);
    }

    private static void BuildLevel(Transform root, Transform visual, Transform collision,
        float width, float depth, float wallHeight, float floorThickness, float baseY,
        float doorWidth, bool groundLevel)
    {
        float wallY = baseY + wallHeight * .5f;
        float wallDepth = .20f;
        // Visual floor and its independent collider.
        Part(visual, "Floor", new Vector3(0f, baseY, 0f), new Vector3(width, floorThickness, depth), timber);
        BoxColliderPart(collision, "Floor", new Vector3(0f, baseY, 0f), new Vector3(width, floorThickness, depth));

        // Four timber posts accent the blue-gray Chinese frame.
        float px = width * .5f - .22f;
        float pz = depth * .5f - .22f;
        foreach (float x in new[] { -px, px })
            foreach (float z in new[] { -pz, pz })
                Part(visual, "Post", new Vector3(x, wallY, z), new Vector3(.30f, wallHeight + .18f, .30f), timber);

        // Back and side walls are solid. The front is split around the doorway so the
        // Collision hierarchy leaves a true, controller-sized opening.
        Part(visual, "BackWall", new Vector3(0f, wallY, depth * .5f), new Vector3(width, wallHeight, wallDepth), timber);
        BoxColliderPart(collision, "BackWall", new Vector3(0f, wallY, depth * .5f), new Vector3(width, wallHeight, wallDepth));
        Part(visual, "LeftWall", new Vector3(-width * .5f, wallY, 0f), new Vector3(wallDepth, wallHeight, depth), timber);
        Part(visual, "RightWall", new Vector3(width * .5f, wallY, 0f), new Vector3(wallDepth, wallHeight, depth), timber);
        BoxColliderPart(collision, "LeftWall", new Vector3(-width * .5f, wallY, 0f), new Vector3(wallDepth, wallHeight, depth));
        BoxColliderPart(collision, "RightWall", new Vector3(width * .5f, wallY, 0f), new Vector3(wallDepth, wallHeight, depth));

        float opening = Mathf.Clamp(doorWidth, .9f, width - .8f);
        float side = (width - opening) * .5f;
        if (side > .12f)
        {
            float segmentX = (opening + width) * .25f;
            Part(visual, "FrontWall_L", new Vector3(-segmentX, wallY, -depth * .5f), new Vector3(side, wallHeight, wallDepth), timber);
            Part(visual, "FrontWall_R", new Vector3(segmentX, wallY, -depth * .5f), new Vector3(side, wallHeight, wallDepth), timber);
            BoxColliderPart(collision, "FrontWall_L", new Vector3(-segmentX, wallY, -depth * .5f), new Vector3(side, wallHeight, wallDepth));
            BoxColliderPart(collision, "FrontWall_R", new Vector3(segmentX, wallY, -depth * .5f), new Vector3(side, wallHeight, wallDepth));
        }
        Part(visual, "DoorHeader", new Vector3(0f, baseY + wallHeight - .24f, -depth * .5f), new Vector3(opening, .48f, wallDepth), timber);
        BoxColliderPart(collision, "DoorHeader", new Vector3(0f, baseY + wallHeight - .24f, -depth * .5f), new Vector3(opening, .48f, wallDepth));

        // Snow resting along the inner sill and a small dark threshold make the opening readable.
        Part(visual, "Threshold", new Vector3(0f, baseY + .12f, -depth * .5f - .08f), new Vector3(opening, .12f, .28f), snow);

        // Upper levels have a walkable balcony deck on the front. Ground level remains an open interior.
        if (!groundLevel)
        {
            Part(visual, "BalconyDeck", new Vector3(0f, baseY + .04f, -depth * .57f), new Vector3(width + .3f, .12f, .70f), timber);
            BoxColliderPart(collision, "BalconyDeck", new Vector3(0f, baseY + .04f, -depth * .57f), new Vector3(width + .3f, .12f, .70f));
            Beam(visual, "BalconyRail", new Vector3(-width * .45f, baseY + .8f, -depth * .72f), new Vector3(width * .45f, baseY + .8f, -depth * .72f), .12f, .12f, timber);
            foreach (float x in new[] { -width * .45f, 0f, width * .45f })
                Part(visual, "BalconyPost", new Vector3(x, baseY + .42f, -depth * .72f), new Vector3(.10f, .85f, .10f), timber);
        }
    }

    private static void BuildPorch(Transform visual, Transform collision, float width, float depth, float wallHeight)
    {
        float z = -depth * .5f - .72f;
        Part(visual, "EntryPorch", new Vector3(0f, .05f, z), new Vector3(Mathf.Min(width * .62f, 5.5f), .16f, 1.4f), stone);
        BoxColliderPart(collision, "EntryPorch", new Vector3(0f, .05f, z), new Vector3(Mathf.Min(width * .62f, 5.5f), .16f, 1.4f));
        float porchW = Mathf.Min(width * .62f, 5.5f) * .5f - .15f;
        foreach (float x in new[] { -porchW, porchW })
        {
            Part(visual, "PorchPost", new Vector3(x, wallHeight * .46f, z), new Vector3(.16f, wallHeight * .82f, .16f), timber);
            Part(visual, "PorchSnowCap", new Vector3(x, wallHeight * .9f, z), new Vector3(.28f, .12f, .28f), snow);
        }
        Part(visual, "PorchLintel", new Vector3(0f, wallHeight * .86f, z), new Vector3(porchW * 2f + .2f, .18f, .18f), timber);
    }

    private static void BuildRoof(Transform visual, Transform collision, float width, float depth, float topY, float roofLift, float slope)
    {
        float roofWidth = width + 1.0f;
        float halfDepth = depth * .5f + .65f;
        float panelLength = halfDepth * 1.13f;
        float angle = Mathf.Atan2(slope, halfDepth) * Mathf.Rad2Deg;
        float z = depth * .25f + .12f;
        float y = topY + .60f;
        for (int side = -1; side <= 1; side += 2)
        {
            Vector3 pos = new Vector3(0f, y + .12f, side * z);
            var panel = Part(visual, side < 0 ? "RoofFront" : "RoofBack", pos, new Vector3(roofWidth, .30f, panelLength), roof);
            panel.transform.localRotation = Quaternion.Euler(side < 0 ? -angle : angle, 0f, 0f);
            var cap = Part(visual, side < 0 ? "SnowRoofFront" : "SnowRoofBack", pos + Vector3.up * .19f, new Vector3(roofWidth * 1.015f, .11f, panelLength * 1.015f), snow);
            cap.transform.localRotation = panel.transform.localRotation;
        }
        Part(visual, "RoofRidge", new Vector3(0f, y + slope * .12f + .22f, 0f), new Vector3(roofWidth * .98f, .24f, .28f), roof);
        Part(visual, "RidgeSnow", new Vector3(0f, y + slope * .12f + .38f, 0f), new Vector3(roofWidth, .10f, .34f), snow);
        // A simple overhang collider prevents the player from walking through the roof edge.
        BoxColliderPart(collision, "RoofCollider", new Vector3(0f, y, 0f), new Vector3(roofWidth, .26f, depth + 1.1f));
    }

    private static GameObject Part(Transform parent, string name, Vector3 localPosition, Vector3 localScale, Material material)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPosition;
        go.transform.localScale = localScale;
        var renderer = go.GetComponent<Renderer>();
        renderer.sharedMaterial = material;
        var collider = go.GetComponent<Collider>();
        if (collider != null) UnityEngine.Object.DestroyImmediate(collider);
        return go;
    }

    private static GameObject BoxColliderPart(Transform parent, string name, Vector3 localPosition, Vector3 localScale)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPosition;
        var box = go.AddComponent<BoxCollider>();
        box.size = localScale;
        return go;
    }

    private static void Beam(Transform parent, string name, Vector3 from, Vector3 to, float width, float depth, Material material)
    {
        var go = Part(parent, name, (from + to) * .5f, new Vector3(width, (to - from).magnitude, depth), material);
        go.transform.localRotation = Quaternion.FromToRotation(Vector3.up, to - from);
    }

    private static Transform Child(Transform parent, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        return go.transform;
    }

    private static void EnsureFolder(string folder)
    {
        string[] parts = folder.Split('/');
        string current = parts[0];
        for (int i = 1; i < parts.Length; i++)
        {
            string next = current + "/" + parts[i];
            if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]);
            current = next;
        }
    }
}
