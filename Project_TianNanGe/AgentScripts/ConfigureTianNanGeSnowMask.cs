using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// One-time scene setup. Subsequent tuning belongs on the existing controller.
public static class ConfigureTianNanGeSnowMask
{
    const string MaskPath = "Assets/TianNanGe/Effects/Snow/SurfaceSnow/SurfaceSnowMask.prefab";
    const string MainScene = "Assets/TianNanGe/Scenes/TianNanGe_Main.unity";

    public static string Configure()
    {
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if (EditorApplication.isPlaying || scene.path != MainScene || scene.isDirty)
            throw new InvalidOperationException("Open the saved main scene outside Play Mode first.");
        var masks = UnityEngine.Object.FindObjectsByType<SurfaceSnowMaskController>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .Where(m => m.gameObject.scene == scene).ToArray();
        if (masks.Length != 1)
            throw new InvalidOperationException("Expected the single existing SurfaceSnowMask controller.");
        var backup = Path.Combine(Path.GetTempPath(), "TianNanGe-SnowMask-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(backup);
        File.Copy(MaskPath, Path.Combine(backup, "SurfaceSnowMask.prefab"));
        File.Copy(MainScene, Path.Combine(backup, "TianNanGe_Main.unity"));
        var root = PrefabUtility.LoadPrefabContents(MaskPath);
        try
        {
            var controller = root.GetComponent<SurfaceSnowMaskController>();
            controller.coverageAmount = 0.8f;
            controller.coverageRadius = 100f;
            controller.edgeSize = 4f;
            PrefabUtility.SaveAsPrefabAsset(root, MaskPath);
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
        // Keep the user's standalone mask, transform, and other scene objects.
        var mask = masks[0];
        Undo.RecordObject(mask, "Configure surface snow coverage");
        mask.coverageAmount = 0.8f;
        mask.coverageRadius = 100f;
        mask.edgeSize = 4f;
        PrefabUtility.RecordPrefabInstancePropertyModifications(mask);
        EditorUtility.SetDirty(mask);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        mask.SendMessage("Apply", SendMessageOptions.RequireReceiver);
        Selection.activeGameObject = mask.gameObject;
        SceneView.RepaintAll();
        return "Configured the existing SurfaceSnowMask; coverage=0.8, radius=100m, edge=4m. Backup: " + backup;
    }

    public static string Inspect()
    {
        var shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/TianNanGe/Effects/Snow/SurfaceSnow/SurfaceSnow.shadergraph");
        var masks = UnityEngine.Object.FindObjectsByType<SurfaceSnowMaskController>(FindObjectsSortMode.None);
        var renderers = UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        var snow = renderers.Where(r => r.sharedMaterials.Any(m => m != null && m.shader == shader)).ToArray();
        var intensities = Shader.GetGlobalFloatArray("DynamicRadialMasks_HeightField_4_Advanced_Normalized_ID1_Global_Intensity");
        return Newtonsoft.Json.JsonConvert.SerializeObject(new
        {
            scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().path,
            frame = Time.frameCount,
            playing = EditorApplication.isPlaying,
            shader = shader == null ? null : shader.name,
            supported = shader != null && shader.isSupported,
            shaderMessages = shader == null ? null : ShaderUtil.GetShaderMessages(shader).Select(m => new { m.message, severity = m.severity.ToString() }).ToArray(),
            activeControllers = masks.Length,
            coverage = Shader.GetGlobalFloat("_TianNanGeSnowCoverage"),
            intensities,
            snowRenderers = snow.Length,
            materialCounts = snow.SelectMany(r => r.sharedMaterials).Where(m => m != null && m.shader == shader).GroupBy(m => m.name).Select(g => new { material = g.Key, slots = g.Count() }),
            unexpectedSubjects = snow.Where(r => HasProtectedParent(r.transform)).Select(r => r.name).ToArray()
        }, Newtonsoft.Json.Formatting.Indented);
    }

    static bool HasProtectedParent(Transform t)
    {
        for (; t != null; t = t.parent)
            if (t.name == "Dragon" || t.name == "Character" || t.name == "PlayerArmature" || t.name == "HandSword") return true;
        return false;
    }
}
