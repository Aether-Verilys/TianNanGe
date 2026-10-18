using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class AttachHandSword
{
    const string ScenePath = "Assets/TianNanGe/Scenes/TianNanGe_Main.unity";
    const string SwordPath = "Assets/TianNanGe/Prefabs/Props/HandSword.prefab";
    const string CharacterPath = "Assets/TianNanGe/Prefabs/Characters/LittleGreen.prefab";

    public static string Apply()
    {
        if (EditorApplication.isPlaying) throw new Exception("Exit Play Mode before attaching the sword.");
        var scene = EditorSceneManager.GetActiveScene();
        if (scene.path != ScenePath) throw new Exception("Open TianNanGe_Main before attaching the sword.");
        var character = GameObject.Find("TianNanGe_Blockout/Character");
        var swordPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(SwordPath);
        if (character == null || swordPrefab == null) throw new Exception("Character or HandSword prefab is missing.");
        var visual = character.transform.Find("Visual_LittleGreen");
        if (visual == null) throw new Exception("Visual_LittleGreen is missing.");
        var hand = FindChild(visual, "mixamorig:RightHand") ?? FindChild(visual, "RightHand");
        if (hand == null) throw new Exception("Humanoid right hand transform is missing.");

        var socket = hand.Find("SwordSocket");
        if (socket == null)
        {
            socket = new GameObject("SwordSocket").transform;
            socket.SetParent(hand, false);
        }
        socket.localPosition = new Vector3(.025f, .035f, -.02f);
        socket.localRotation = Quaternion.identity;
        socket.localScale = Vector3.one;
        var old = socket.Find("HandSword");
        if (old != null) UnityEngine.Object.DestroyImmediate(old.gameObject);
        var sword = (GameObject)PrefabUtility.InstantiatePrefab(swordPrefab, socket);
        sword.name = "HandSword";
        sword.transform.localPosition = Vector3.zero;
        // +Y is the blade direction in the prefab. Rotate it down from the hand;
        // the hilt remains above the palm while the sheathed blade hangs beside the skirt.
        sword.transform.localRotation = Quaternion.Euler(13.7702f, 133.0003f, 340.8432f);
        // Little Green is authored at a 2x visual scale in this scene; a half-scale
        // prop keeps the one-metre sword at a believable hand-to-body proportion.
        sword.transform.localScale = Vector3.one * .5f;

        PrefabUtility.ApplyPrefabInstance(character, InteractionMode.AutomatedAction);
        EditorSceneManager.MarkSceneDirty(scene);
        AssetDatabase.SaveAssets();
        EditorSceneManager.SaveScene(scene);
        return "Attached HandSword.prefab to " + hand.name + "/SwordSocket; sword hangs downward in the right hand and the character prefab was updated.";
    }

    static Transform FindChild(Transform root, string name)
    {
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
            if (t.name == name) return t;
        return null;
    }
}
