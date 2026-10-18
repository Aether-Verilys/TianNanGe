using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Creates the reusable HDRP snowfall effect migrated from the Snow project.
/// This is an editor-only setup utility; the generated prefab has no editor dependency.
/// </summary>
public static class CreateSnowfallPrefab
{
    private const string RootFolder = "Assets/TianNanGe/Effects/Snow";
    private const string MaterialPath = RootFolder + "/Snowfall Particles.mat";
    private const string PrefabPath = RootFolder + "/Snowfall.prefab";
    private const string MainScenePath = "Assets/TianNanGe/Scenes/TianNanGe_Main.unity";

    [MenuItem("TianNanGe/Create Snowfall Prefab")]
    public static void Create()
    {
        EnsureFolder("Assets/TianNanGe/Effects");
        EnsureFolder(RootFolder);

        var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
        if (material == null)
        {
            material = CreateSnowMaterial();
            AssetDatabase.CreateAsset(material, MaterialPath);
        }

        var root = new GameObject("Snowfall");
        root.transform.position = Vector3.zero;

        var particlesObject = new GameObject("Snow Particles");
        particlesObject.transform.SetParent(root.transform, false);
        particlesObject.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);

        var particleSystem = particlesObject.AddComponent<ParticleSystem>();
        var renderer = particlesObject.GetComponent<ParticleSystemRenderer>();
        ConfigureParticleSystem(particleSystem, renderer, material);

        // Keep the volume near the active camera while playing, so a reusable prefab
        // continues to snow when the player moves through a large outdoor scene.
        var follow = root.AddComponent<SnowfallFollowCamera>();
        follow.height = 18f;
        follow.horizontalSize = 36f;
        follow.followCamera = true;

        var prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        Object.DestroyImmediate(root);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        InstallInMainScene(prefab);
        Debug.Log("Snowfall prefab created at " + PrefabPath + " and installed in " + MainScenePath);
    }

    private static Material CreateSnowMaterial()
    {
        var shader = AssetDatabase.LoadAssetAtPath<Shader>(RootFolder + "/Shader/Snow (Particles).shadergraph");
        if (shader == null)
            shader = Shader.Find("HDRP/Unlit");
        if (shader == null)
            shader = Shader.Find("Particles/Standard Unlit");
        if (shader == null)
            throw new System.InvalidOperationException("Could not find a particle-compatible HDRP unlit shader.");

        var material = new Material(shader) { name = "Snowfall Particles" };
        if (material.HasProperty("_SurfaceType")) material.SetFloat("_SurfaceType", 1f);
        if (material.HasProperty("_BlendMode")) material.SetFloat("_BlendMode", 0f);
        if (material.HasProperty("_AlphaCutoffEnable")) material.SetFloat("_AlphaCutoffEnable", 0f);
        if (material.HasProperty("_ZWrite")) material.SetFloat("_ZWrite", 0f);
        if (material.HasProperty("_EnableFogOnTransparent")) material.SetFloat("_EnableFogOnTransparent", 1f);
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", new Color(0.92f, 0.97f, 1f, 0.78f));
        if (material.HasProperty("_Color")) material.SetColor("_Color", new Color(0.92f, 0.97f, 1f, 0.78f));
        var defaultParticle = AssetDatabase.GetBuiltinExtraResource<Texture2D>("Default-Particle.psd");
        if (defaultParticle != null && material.HasProperty("_MainTex"))
            material.SetTexture("_MainTex", defaultParticle);
        material.renderQueue = 3000;
        return material;
    }

    private static void ConfigureParticleSystem(ParticleSystem ps, ParticleSystemRenderer renderer, Material material)
    {
        var main = ps.main;
        main.loop = true;
        main.playOnAwake = true;
        main.prewarm = true;
        main.duration = 5f;
        main.startLifetime = 15f;
        main.startSpeed = 3f;
        main.startSize = new ParticleSystem.MinMaxCurve(0.05f, 0.4f);
        main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.92f, 0.97f, 1f, 0.78f));
        main.maxParticles = 600000;
        main.gravityModifier = 0f;
        main.simulationSpace = ParticleSystemSimulationSpace.Local;
        main.scalingMode = ParticleSystemScalingMode.Hierarchy;

        var emission = ps.emission;
        emission.enabled = true;
        emission.rateOverTime = 6000f;

        var shape = ps.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.Box;
        shape.scale = new Vector3(36f, 1f, 36f);

        var velocity = ps.velocityOverLifetime;
        velocity.enabled = true;
        velocity.space = ParticleSystemSimulationSpace.Local;
        velocity.x = new ParticleSystem.MinMaxCurve(-0.25f, 0.25f);
        velocity.y = new ParticleSystem.MinMaxCurve(-0.4f, 0.1f);
        velocity.z = new ParticleSystem.MinMaxCurve(-0.25f, 0.25f);

        var noise = ps.noise;
        noise.enabled = true;
        noise.strength = 0.12f;
        noise.frequency = 0.18f;
        noise.scrollSpeed = 0.08f;

        renderer.renderMode = ParticleSystemRenderMode.Billboard;
        renderer.alignment = ParticleSystemRenderSpace.View;
        renderer.normalDirection = 1f;
        renderer.lengthScale = 2f;
        renderer.enableGPUInstancing = true;
        renderer.receiveShadows = false;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.material = material;
    }

    private static void InstallInMainScene(GameObject prefab)
    {
        var scene = EditorSceneManager.OpenScene(MainScenePath, OpenSceneMode.Single);
        var old = GameObject.Find("Snowfall");
        if (old != null)
            Object.DestroyImmediate(old);

        var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
        instance.name = "Snowfall";
        instance.transform.position = Vector3.zero;
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
    }

    private static void EnsureFolder(string folder)
    {
        var parts = folder.Split('/');
        var current = parts[0];
        for (var i = 1; i < parts.Length; i++)
        {
            var next = current + "/" + parts[i];
            if (!AssetDatabase.IsValidFolder(next))
                AssetDatabase.CreateFolder(current, parts[i]);
            current = next;
        }
    }
}
