using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class ExtendBridge
{
    const string ScenePath = "Assets/TianNanGe/Scenes/TianNanGe_Main.unity";
    const string BridgePath = "Assets/TianNanGe/Prefabs/Environment/Bridge.prefab";
    const float DeckStart = -16.5f;
    const float DeckEnd = 21.5f;
    const float DeckCenter = 2.5f;
    const float DeckLength = 38f;

    public static string Apply()
    {
        var scene = EditorSceneManager.GetActiveScene();
        if (scene.path != ScenePath) throw new Exception("Open TianNanGe_Main before extending the bridge.");
        var bridge = GameObject.Find("TianNanGe_Blockout/Bridge");
        if (bridge == null) throw new Exception("Bridge root is missing.");
        if (EditorApplication.isPlaying) throw new Exception("Exit Play Mode before editing the bridge.");
        if (bridge.transform.Find("ExtendedRailings") != null) throw new Exception("Bridge extension already installed; edit the prefab to preserve hand edits.");

        var snow = AssetDatabase.LoadAssetAtPath<Material>("Assets/TianNanGe/Materials/Snow.mat");
        var timber = AssetDatabase.LoadAssetAtPath<Material>("Assets/TianNanGe/Materials/BlueTimber.mat");
        var stone = AssetDatabase.LoadAssetAtPath<Material>("Assets/TianNanGe/Materials/Stone.mat");
        if (snow == null || timber == null || stone == null) throw new Exception("Bridge materials are missing.");

        var deck = bridge.transform.Find("BridgeDeck");
        var snowDeck = bridge.transform.Find("SnowDeck");
        if (deck == null || snowDeck == null) throw new Exception("Bridge deck objects are missing.");
        deck.localPosition = new Vector3(.35f, -.17f, DeckCenter);
        deck.localScale = new Vector3(2.7f, .32f, DeckLength);
        snowDeck.localPosition = new Vector3(.35f, .02f, DeckCenter);
        snowDeck.localScale = new Vector3(2.76f, .09f, DeckLength);

        // Disable the short first-pass rail pieces. The new continuous rail set is easier to read and
        // leaves no gap on the left approach.
        foreach (var t in bridge.GetComponentsInChildren<Transform>(true))
        {
            if (t == bridge.transform) continue;
            if (t.name.StartsWith("Rail", StringComparison.Ordinal) ||
                t.name.StartsWith("Post", StringComparison.Ordinal) ||
                t.name.StartsWith("SnowCap", StringComparison.Ordinal))
                t.gameObject.SetActive(false);
        }

        var rails = FindOrCreate(bridge.transform, "ExtendedRailings");
        ClearChildren(rails);
        float leftX = -1.08f, rightX = 1.78f;
        int postCount = 0, segmentCount = 0;
        foreach (float x in new[] { leftX, rightX })
        {
            var zValues = new List<float>();
            for (int i = 0; i <= 22; i++) zValues.Add(Mathf.Lerp(DeckStart, DeckEnd, i / 22f));
            for (int i = 0; i < zValues.Count; i++)
            {
                float z = zValues[i];
                MakeCube("Post_" + postCount++, rails, new Vector3(x, .73f, z), new Vector3(.14f, 1.5f, .16f), timber);
                MakeBall("SnowCap_" + i, rails, new Vector3(x, 1.52f, z), new Vector3(.26f, .16f, .26f), snow);
                if (i == zValues.Count - 1) continue;
                float next = zValues[i + 1];
                foreach (float y in new[] { .42f, 1.42f })
                {
                    float length = next - z;
                    MakeCube("Rail_" + segmentCount, rails, new Vector3(x, y, (z + next) * .5f), new Vector3(.0936f, .1275f, length), timber);
                    MakeCube("RailSnow_" + segmentCount++, rails, new Vector3(x, y + .09f, (z + next) * .5f), new Vector3(.1224f, .17f, length), snow);
                }
            }
        }

        var connections = FindOrCreate(bridge.transform, "BridgeConnections");
        ClearChildren(connections);
        MakeCube("NearAbutment", connections, new Vector3(.35f, -.17f, -18f), new Vector3(6f, .32f, 4.5f), stone);
        MakeCube("NearConnectorSnow", connections, new Vector3(.35f, .02f, -18.1f), new Vector3(3.78f, .09f, 3.8f), snow);
        MakeCube("FarAbutment", connections, new Vector3(.35f, -.17f, 23f), new Vector3(6f, .32f, 4.5f), stone);
        MakeCube("FarConnectorSnow", connections, new Vector3(.35f, .02f, 23.1f), new Vector3(3.78f, .09f, 3.8f), snow);
        foreach (float z in new[] { -18.1f, 23.1f })
        {
            var landing = new GameObject(z < 0 ? "NearLandingCollision" : "FarLandingCollision");
            landing.transform.SetParent(connections, false);
            var surface = landing.AddComponent<BoxCollider>();
            surface.center = new Vector3(.35f, .015f, z < 0 ? -18f : 23f);
            surface.size = new Vector3(6f, .1f, 4.5f);
            MakeCube("AbutmentWallL", connections, new Vector3(-1.85f, -.95f, z), new Vector3(.45f, 1.8f, 3.8f), stone);
            MakeCube("AbutmentWallR", connections, new Vector3(2.55f, -.95f, z), new Vector3(.45f, 1.8f, 3.8f), stone);
            MakeCube("AbutmentSnowL", connections, new Vector3(-1.85f, -.02f, z), new Vector3(.52f, .16f, 3.95f), snow);
            MakeCube("AbutmentSnowR", connections, new Vector3(2.55f, -.02f, z), new Vector3(.52f, .16f, 3.95f), snow);
        }

        var deckCollider = bridge.GetComponent<BoxCollider>() ?? bridge.AddComponent<BoxCollider>();
        deckCollider.center = new Vector3(.35f, .02f, DeckCenter);
        deckCollider.size = new Vector3(2.76f, .10f, DeckLength);
        deckCollider.isTrigger = false;
        SetRailCollider(bridge, "LeftRailCollision", leftX);
        SetRailCollider(bridge, "RightRailCollision", rightX);

        PrefabUtility.ApplyPrefabInstance(bridge, InteractionMode.AutomatedAction);
        EditorSceneManager.MarkSceneDirty(scene);
        AssetDatabase.SaveAssets();
        EditorSceneManager.SaveScene(scene);
        return $"Bridge extended to {DeckLength:0.#}m ({DeckStart:0.#}..{DeckEnd:0.#}), continuous two-side railings and two connector platforms saved to {BridgePath}.";
    }

    static void SetRailCollider(GameObject bridge, string name, float x)
    {
        var t = bridge.transform.Find(name);
        if (t == null) { t = new GameObject(name).transform; t.SetParent(bridge.transform, false); }
        var c = t.GetComponent<BoxCollider>() ?? t.gameObject.AddComponent<BoxCollider>();
        c.center = new Vector3(x, .8f, DeckCenter);
        c.size = new Vector3(.12f, 1.6f, DeckLength);
        c.isTrigger = false;
    }

    static Transform FindOrCreate(Transform parent, string name)
    {
        var t = parent.Find(name);
        if (t != null) return t;
        var go = new GameObject(name);
        t = go.transform; t.SetParent(parent, false); return t;
    }

    static void ClearChildren(Transform parent)
    {
        for (int i = parent.childCount - 1; i >= 0; i--) UnityEngine.Object.DestroyImmediate(parent.GetChild(i).gameObject);
    }

    static GameObject MakeCube(string name, Transform parent, Vector3 pos, Vector3 scale, Material mat)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube); go.name = name; go.transform.SetParent(parent, false);
        go.transform.localPosition = pos; go.transform.localScale = scale;
        go.GetComponent<Renderer>().sharedMaterial = mat;
        UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>()); return go;
    }

    static GameObject MakeBall(string name, Transform parent, Vector3 pos, Vector3 scale, Material mat)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere); go.name = name; go.transform.SetParent(parent, false);
        go.transform.localPosition = pos; go.transform.localScale = scale;
        go.GetComponent<Renderer>().sharedMaterial = mat;
        UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>()); return go;
    }
}
