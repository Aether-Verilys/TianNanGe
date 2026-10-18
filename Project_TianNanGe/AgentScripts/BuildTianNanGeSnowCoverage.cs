using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Rendering.HighDefinition;
using UnityEngine;

/// <summary>
/// Installs the migrated SurfaceSnow shader on the environment materials used by TianNanGe.
///
/// This is intentionally an editor-only, one-shot migration. It creates independent material
/// assets under SnowVariants and edits only the explicitly listed environment prefabs. Character,
/// dragon, sword, eye, hair and other special materials are never searched or changed.
/// Run <see cref="Build"/> from Unity's Pipeline eval/run_script command after the graph has
/// imported successfully.
/// </summary>
public static class BuildTianNanGeSnowCoverage
{
    private const string MaterialsRoot = "Assets/TianNanGe/Materials";
    private const string VariantsFolder = MaterialsRoot + "/SnowVariants";
    private const string SnowShaderGraph = "Assets/TianNanGe/Effects/Snow/SurfaceSnow/SurfaceSnow.shadergraph";
    private const string SurfaceSnowTemplate = "Assets/TianNanGe/Effects/Snow/SurfaceSnow/SurfaceSnow.mat";

    private static readonly string[] EnvironmentPrefabs =
    {
        "Assets/TianNanGe/Prefabs/Architecture/MainPavilion.prefab",
        "Assets/TianNanGe/Prefabs/Architecture/LeftPavilion.prefab",
        "Assets/TianNanGe/Prefabs/Architecture/FarPavilion.prefab",
        "Assets/TianNanGe/Prefabs/Architecture/FarRight.prefab",
        "Assets/TianNanGe/Prefabs/Environment/Bridge.prefab",
        "Assets/TianNanGe/Prefabs/Environment/Mountains.prefab"
    };

    private static readonly MaterialSpec[] Specs =
    {
        new MaterialSpec("Snow",         0.32f, new Color(0.96f, 0.985f, 1f)),
        new MaterialSpec("BlueTimber",   0.12f, new Color(0.95f, 0.98f, 1f)),
        new MaterialSpec("Roof",         0.52f, new Color(0.97f, 0.99f, 1f)),
        new MaterialSpec("Stone",        0.20f, new Color(0.94f, 0.975f, 1f)),
        new MaterialSpec("MountainRock", 0.24f, new Color(0.93f, 0.97f, 1f))
    };

    /// <summary>Builds variants and updates the six environment prefabs.</summary>
    public static string Build()
    {
        if (EditorApplication.isPlaying)
            throw new InvalidOperationException("Exit Play Mode before installing snow materials.");

        var snowShader = AssetDatabase.LoadAssetAtPath<Shader>(SnowShaderGraph);
        var template = AssetDatabase.LoadAssetAtPath<Material>(SurfaceSnowTemplate);
        if (snowShader == null)
            throw new InvalidOperationException("SurfaceSnow.shadergraph did not import as a Shader: " + SnowShaderGraph);
        if (template == null)
            throw new InvalidOperationException("SurfaceSnow material template is missing: " + SurfaceSnowTemplate);

        // Check every input before creating assets or saving any prefab.
        foreach (var prefabPath in EnvironmentPrefabs)
            if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) == null)
                throw new InvalidOperationException("Environment prefab is missing: " + prefabPath);
        foreach (var spec in Specs)
        {
            var sourcePath = MaterialsRoot + "/" + spec.SourceName + ".mat";
            var source = AssetDatabase.LoadAssetAtPath<Material>(sourcePath);
            if (source == null)
                throw new InvalidOperationException("Environment material is missing: " + sourcePath);
            if (source.HasProperty("_SurfaceType") && source.GetFloat("_SurfaceType") > 0.5f)
                throw new InvalidOperationException("Snow migration only supports opaque environment materials: " + sourcePath);
            var existingPath = VariantsFolder + "/" + spec.SourceName + "_Snow.mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(existingPath);
            if (existing != null && existing.shader != snowShader)
                throw new InvalidOperationException("Existing variant uses a different shader; refusing to overwrite: " + existingPath);
        }
        foreach (var prefabPath in EnvironmentPrefabs) BackupPrefab(prefabPath);

        EnsureFolder(MaterialsRoot, "SnowVariants");
        var variants = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);
        foreach (var spec in Specs)
        {
            var sourcePath = MaterialsRoot + "/" + spec.SourceName + ".mat";
            var source = AssetDatabase.LoadAssetAtPath<Material>(sourcePath);
            if (source == null)
                throw new InvalidOperationException("Environment material is missing: " + sourcePath);

            var variantPath = VariantsFolder + "/" + spec.SourceName + "_Snow.mat";
            var variant = AssetDatabase.LoadAssetAtPath<Material>(variantPath);
            if (variant == null)
            {
                variant = CreateVariant(source, template, snowShader, spec);
                AssetDatabase.CreateAsset(variant, variantPath);
                EditorUtility.SetDirty(variant);
            }
            else
            {
                // Existing variants may contain artist tuning. Never overwrite them; only use them.
                if (variant.shader != snowShader)
                    throw new InvalidOperationException("Existing variant uses a different shader; refusing to overwrite: " + variantPath);
            }
            variants.Add(sourcePath, variant);
        }
        AssetDatabase.SaveAssets();

        var changedPrefabs = 0;
        var changedRenderers = 0;
        foreach (var prefabPath in EnvironmentPrefabs)
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) == null)
                throw new InvalidOperationException("Environment prefab is missing: " + prefabPath);
            var changed = PatchPrefab(prefabPath, variants, out var count);
            if (changed) changedPrefabs++;
            changedRenderers += count;
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Selection.activeObject = variants[MaterialsRoot + "/Snow.mat"];
        EditorGUIUtility.PingObject(Selection.activeObject);
        return $"Snow coverage installed: {variants.Count} variants, {changedRenderers} renderer material slots updated across {changedPrefabs} prefabs. Existing variants were preserved.";
    }

    private static Material CreateVariant(Material source, Material template, Shader shader, MaterialSpec spec)
    {
        // Start from the migrated template so all Snow_* defaults and HeightField bindings survive.
        var variant = new Material(template) { name = source.name + "_Snow" };
        variant.shader = shader;

        CopyTexture(source, variant, "_BaseColorMap", "_MainTex");
        CopyTexture(source, variant, "_NormalMap");
        CopyTexture(source, variant, "_MaskMap");
        CopyColor(source, variant, "_BaseColor", Color.white);

        // Keep the source's physically based response where the SurfaceSnow graph exposes it.
        CopyFloat(source, variant, "_Metallic", "_Metalness");
        CopyFloat(source, variant, "_Metalness");
        bool hasMask = source.HasProperty("_MaskMap") && source.GetTexture("_MaskMap") != null;
        if (hasMask)
        {
            CopyFloat(source, variant, "_SmoothnessRemapMin");
            CopyFloat(source, variant, "_SmoothnessRemapMax");
        }
        else if (source.HasProperty("_Smoothness"))
        {
            var smoothness = Mathf.Clamp01(source.GetFloat("_Smoothness"));
            SetFloat(variant, "_SmoothnessRemapMin", smoothness);
            SetFloat(variant, "_SmoothnessRemapMax", smoothness);
        }
        CopyFloat(source, variant, "_AORemapMin", "Ao_Min");
        CopyFloat(source, variant, "_AORemapMax", "Ao_Max");
        CopyFloat(source, variant, "_NormalScale");
        CopyFloat(source, variant, "_AlphaCutoff");
        CopyFloat(source, variant, "_AlphaCutoffEnable");

        // Roofs in the source scene are double-sided. Preserve that behavior on the snow variant.
        if (source.HasProperty("_DoubleSidedEnable"))
            SetFloat(variant, "_DoubleSidedEnable", source.GetFloat("_DoubleSidedEnable"));
        else if (source.doubleSidedGI)
            SetFloat(variant, "_DoubleSidedEnable", 1f);
        variant.doubleSidedGI = source.doubleSidedGI;

        foreach (var property in new[]
        {
            "_CullMode", "_OpaqueCullMode", "_SurfaceType", "_BlendMode", "_ZWrite",
            "_AlphaSrcBlend", "_AlphaDstBlend", "_SrcBlend", "_DstBlend",
            "_TransparentCullMode", "_TransparentZWrite", "_TransparentSortPriority"
        }) CopyFloat(source, variant, property);

        // These values are the exposed snow controls that artists can tune at runtime.
        SetColor(variant, "Snow_Color", spec.SnowColor);
        SetFloat(variant, "Snow_Distortion", spec.Distortion);
        // Keep custom queues from the source prefab so rails and roof layers retain their ordering.
        if (source.renderQueue >= 0) variant.renderQueue = source.renderQueue;
        variant.enableInstancing = source.enableInstancing;
        HDShaderUtils.ResetMaterialKeywords(variant);
        return variant;
    }

    private static bool PatchPrefab(string prefabPath, IReadOnlyDictionary<string, Material> variants, out int changedSlots)
    {
        changedSlots = 0;
        BackupPrefab(prefabPath);
        var root = PrefabUtility.LoadPrefabContents(prefabPath);
        var changed = false;
        try
        {
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                var materials = renderer.sharedMaterials;
                var rendererChanged = false;
                for (var i = 0; i < materials.Length; i++)
                {
                    var material = materials[i];
                    if (material == null) continue;
                    var sourcePath = AssetDatabase.GetAssetPath(material);
                    if (!variants.TryGetValue(sourcePath, out var variant)) continue;
                    if (materials[i] == variant) continue;
                    materials[i] = variant;
                    rendererChanged = true;
                    changedSlots++;
                }
                if (!rendererChanged) continue;
                renderer.sharedMaterials = materials;
                EditorUtility.SetDirty(renderer);
                PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
                changed = true;
            }
            if (changed) PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
        return changed;
    }

    private static void BackupPrefab(string prefabPath)
    {
        var projectRoot = Directory.GetParent(Application.dataPath).FullName;
        var absolute = Path.Combine(projectRoot, prefabPath);
        var backup = Path.Combine(projectRoot, "Backups/BeforeSnowCoverage", prefabPath);
        if (File.Exists(backup)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(backup));
        File.Copy(absolute, backup, false);
        if (File.Exists(absolute + ".meta")) File.Copy(absolute + ".meta", backup + ".meta", false);
    }

    private static void CopyTexture(Material source, Material destination, string property, string fallback = null)
    {
        if (!destination.HasProperty(property)) return;
        var sourceProperty = source.HasProperty(property) ? property : fallback;
        if (sourceProperty == null || !source.HasProperty(sourceProperty)) return;
        var texture = source.GetTexture(sourceProperty);
        if (texture == null) return;
        destination.SetTexture(property, texture);
        destination.SetTextureScale(property, source.GetTextureScale(sourceProperty));
        destination.SetTextureOffset(property, source.GetTextureOffset(sourceProperty));
    }

    private static void CopyColor(Material source, Material destination, string property, Color fallback)
    {
        if (!destination.HasProperty(property)) return;
        destination.SetColor(property, source.HasProperty(property) ? source.GetColor(property) : fallback);
    }

    private static void CopyFloat(Material source, Material destination, string property, string destinationProperty = null)
    {
        destinationProperty = destinationProperty ?? property;
        if (source.HasProperty(property) && destination.HasProperty(destinationProperty))
            destination.SetFloat(destinationProperty, source.GetFloat(property));
    }

    private static void SetFloat(Material material, string property, float value)
    {
        if (material.HasProperty(property)) material.SetFloat(property, value);
    }

    private static void SetColor(Material material, string property, Color value)
    {
        if (material.HasProperty(property)) material.SetColor(property, value);
    }

    private static void EnsureFolder(string parent, string name)
    {
        var path = parent + "/" + name;
        if (!AssetDatabase.IsValidFolder(path)) AssetDatabase.CreateFolder(parent, name);
    }

    private readonly struct MaterialSpec
    {
        public readonly string SourceName;
        public readonly float Distortion;
        public readonly Color SnowColor;

        public MaterialSpec(string sourceName, float distortion, Color snowColor)
        {
            SourceName = sourceName;
            Distortion = distortion;
            SnowColor = snowColor;
        }
    }
}
