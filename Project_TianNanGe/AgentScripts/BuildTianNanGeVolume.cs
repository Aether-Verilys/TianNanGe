using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

// Run through Pipeline run_script. This file stays outside Assets and does not enter builds.
public static class BuildTianNanGeVolume
{
    const string Folder = "Assets/TianNanGe/Lighting/VolumeProfiles";
    const string Prefab = "Assets/TianNanGe/Prefabs/Lighting/TianNanGeVolume.prefab";
    const string Rig = "Assets/TianNanGe/Prefabs/Lighting/LightingRig.prefab";
    const string Scene = "Assets/TianNanGe/Scenes/TianNanGe_Main.unity";

    public static string Build()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Exit Play Mode first.");
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if (scene.path != Scene || scene.isDirty)
            throw new InvalidOperationException("Open the saved main scene before installing the Volume.");
        if (AssetDatabase.LoadAssetAtPath<GameObject>(Prefab) != null || AssetDatabase.IsValidFolder(Folder))
            throw new InvalidOperationException("Volume already exists; edit its profiles instead of overwriting tuning.");
        var source = AssetDatabase.LoadAssetAtPath<VolumeProfile>("Assets/TianNanGe/Lighting/KloofendalSkyVolume.asset");
        if (source == null || !source.TryGet<HDRISky>(out var originalSky))
            throw new InvalidOperationException("Kloofendal sky profile is missing.");
        AssetDatabase.CreateFolder("Assets/TianNanGe/Lighting", "VolumeProfiles");

        var sky = Profile("TianNanGe_Sky");
        var environment = Add<VisualEnvironment>(sky);
        environment.skyType.Override((int)SkyType.HDRI);
        environment.skyAmbientMode.Override(SkyAmbientMode.Dynamic);
        var hdri = UnityEngine.Object.Instantiate(originalSky);
        hdri.name = "HDRISky";
        sky.components.Add(hdri);
        AssetDatabase.AddObjectToAsset(hdri, sky);

        var atmosphere = Profile("TianNanGe_Fog");
        var fog = Add<Fog>(atmosphere);
        fog.enabled.Override(true);
        fog.colorMode.Override(FogColorMode.SkyColor);
        fog.tint.Override(new Color(0.91f, 0.96f, 1f));
        fog.baseHeight.Override(-5f);
        fog.maximumHeight.Override(35f);
        fog.meanFreePath.Override(180f);
        fog.maxFogDistance.Override(1000f);
        fog.enableVolumetricFog.Override(true);
        fog.albedo.Override(new Color(0.82f, 0.9f, 1f));
        fog.globalLightProbeDimmer.Override(0.7f);
        fog.anisotropy.Override(0.1f);
        fog.depthExtent.Override(128f);
        fog.denoisingMode.Override(FogDenoisingMode.Gaussian);
        fog.quality.Override(3); // Custom, so the following explicit quality values are used.
        fog.fogControlMode = FogControl.Manual;
        OverrideSerialized(fog, "m_FogControlMode");
        fog.screenResolutionPercentage.Override(25f);
        fog.volumeSliceCount.Override(64);

        var shadow = Profile("TianNanGe_Shadow");
        var ao = Add<ScreenSpaceAmbientOcclusion>(shadow);
        ao.intensity.Override(0.85f);
        ao.radius.Override(1.15f);
        ao.directLightingStrength.Override(0.25f);
        ao.quality.Override(1); // Medium quality, follows the project's sample budget.
        var micro = Add<MicroShadowing>(shadow);
        micro.enable.Override(true);
        micro.opacity.Override(0.7f);
        var contact = Add<ContactShadows>(shadow);
        contact.enable.Override(true);
        contact.length.Override(0.2f);
        contact.opacity.Override(0.8f);
        contact.distanceScaleFactor.Override(0.15f);
        contact.maxDistance.Override(60f);
        contact.fadeDistance.Override(10f);
        contact.quality.Override(1);
        var shadows = Add<HDShadowSettings>(shadow);
        shadows.maxShadowDistance.Override(180f);
        shadows.cascadeShadowSplitCount.Override(4);
        shadows.cascadeShadowSplit0.Override(0.06f);
        shadows.cascadeShadowSplit1.Override(0.2f);
        shadows.cascadeShadowSplit2.Override(0.5f);

        var post = Profile("TianNanGe_Post");
        var exposure = Add<Exposure>(post);
        exposure.mode.Override(ExposureMode.Fixed);
        exposure.fixedExposure.Override(12f);
        var tone = Add<Tonemapping>(post);
        tone.mode.Override(TonemappingMode.ACES);
        var color = Add<ColorAdjustments>(post);
        color.postExposure.Override(0f);
        color.contrast.Override(-8f);
        color.saturation.Override(-7f);
        var split = Add<SplitToning>(post);
        split.shadows.Override(new Color(0.47f, 0.5f, 0.54f));
        split.highlights.Override(new Color(0.53f, 0.51f, 0.48f));
        split.balance.Override(5f);
        var bloom = Add<Bloom>(post);
        bloom.intensity.Override(0.18f);
        bloom.scatter.Override(0.65f);
        bloom.threshold.Override(1f);
        bloom.dirtIntensity.Override(0f);
        bloom.tint.Override(Color.white);

        var lens = Profile("TianNanGe_Lens");
        var vignette = Add<Vignette>(lens);
        vignette.intensity.Override(0.16f);
        vignette.smoothness.Override(0.7f);
        vignette.color.Override(new Color(0.025f, 0.035f, 0.055f));
        Add<MotionBlur>(lens).intensity.Override(0f);
        Add<DepthOfField>(lens).focusMode.Override(DepthOfFieldMode.Off);
        Add<ChromaticAberration>(lens).intensity.Override(0f);
        Add<PaniniProjection>(lens).distance.Override(0f);
        var grain = Add<FilmGrain>(lens);
        grain.type.Override(FilmGrainLookup.Thin1);
        grain.intensity.Override(0.035f);
        grain.response.Override(0.8f);

        var root = new GameObject("TianNanGeVolume");
        try
        {
            Layer(root, "00 Sky", sky, 110);
            Layer(root, "10 Fog", atmosphere, 120);
            Layer(root, "20 Shadow", shadow, 130);
            Layer(root, "30 Post", post, 140);
            Layer(root, "40 Lens", lens, 150);
            PrefabUtility.SaveAsPrefabAsset(root, Prefab);
        }
        finally { UnityEngine.Object.DestroyImmediate(root); }
        foreach (var profile in new[] { sky, atmosphere, shadow, post, lens })
        {
            EditorUtility.SetDirty(profile);
            foreach (var component in profile.components) EditorUtility.SetDirty(component);
        }
        AssetDatabase.SaveAssets();

        var rig = PrefabUtility.LoadPrefabContents(Rig);
        try
        {
            var legacy = rig.transform.Find("Global Look");
            if (legacy == null) throw new InvalidOperationException("Expected the original Global Look in LightingRig.");
            legacy.gameObject.SetActive(false); // Preserve the original look for comparison/rollback.
            PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(Prefab), rig.transform);
            var sun = rig.GetComponentsInChildren<HDAdditionalLightData>(true).Single(x => x.name == "Sun");
            var serialized = new SerializedObject(sun);
            serialized.FindProperty("m_UseContactShadow.m_UseOverride").boolValue = true;
            serialized.FindProperty("m_UseContactShadow.m_Override").boolValue = true;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            sun.SetShadowResolutionOverride(true);
            sun.SetShadowResolution(2048);
            PrefabUtility.SaveAsPrefabAsset(rig, Rig);
        }
        finally { PrefabUtility.UnloadPrefabContents(rig); }

        // These are scene overrides: don't change the imported Starter Assets camera prefab.
        foreach (var camera in UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            var data = camera.GetComponent<HDAdditionalCameraData>();
            if (data == null || camera.gameObject.scene != scene) continue;
            Undo.RecordObject(data, "TianNanGe Volume camera setup");
            data.volumeLayerMask |= 1;
            data.antialiasing = HDAdditionalCameraData.AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            data.SMAAQuality = HDAdditionalCameraData.SMAAQualityLevel.High;
            data.dithering = true;
            PrefabUtility.RecordPrefabInstancePropertyModifications(data);
        }
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Selection.activeObject = AssetDatabase.LoadAssetAtPath<GameObject>(Prefab);
        EditorGUIUtility.PingObject(Selection.activeObject);
        return "Created five native HDRP Volume profiles and TianNanGeVolume prefab; nested in LightingRig, legacy look disabled, Sun contact shadows and 2048 shadows enabled, main scene saved.";
    }

    static VolumeProfile Profile(string name)
    {
        var profile = ScriptableObject.CreateInstance<VolumeProfile>();
        profile.name = name;
        AssetDatabase.CreateAsset(profile, Folder + "/" + name + ".asset");
        return profile;
    }

    static T Add<T>(VolumeProfile profile) where T : VolumeComponent
    {
        var component = profile.Add<T>(false);
        component.name = typeof(T).Name;
        AssetDatabase.AddObjectToAsset(component, profile);
        return component;
    }

    static void OverrideSerialized(VolumeComponent component, string property)
    {
        var serialized = new SerializedObject(component);
        serialized.FindProperty(property + ".m_OverrideState").boolValue = true;
        serialized.ApplyModifiedPropertiesWithoutUndo();
    }

    static void Layer(GameObject root, string name, VolumeProfile profile, float priority)
    {
        var child = new GameObject(name);
        child.transform.SetParent(root.transform, false);
        child.layer = 0;
        var volume = child.AddComponent<Volume>();
        volume.isGlobal = true;
        volume.priority = priority;
        volume.weight = 1;
        volume.sharedProfile = profile;
    }
}
