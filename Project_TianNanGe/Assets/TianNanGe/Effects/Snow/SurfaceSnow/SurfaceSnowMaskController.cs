using UnityEngine;

/// <summary>
/// Supplies the global HeightField arrays expected by the migrated surface-snow shader.
/// Place the generated SurfaceSnowMask prefab in a scene to enable the snow layer.
/// </summary>
[ExecuteAlways]
public sealed class SurfaceSnowMaskController : MonoBehaviour
{
    private const int MaskCount = 4;
    private const string Prefix = "DynamicRadialMasks_HeightField_4_Advanced_Normalized_ID1_Global_";
    private static readonly int CoverageId = Shader.PropertyToID("_TianNanGeSnowCoverage");

    [Range(0f, 1f)]
    [Tooltip("Overall snow coverage after the slope and area masks: 0 reveals the base material, 1 applies the full snow layer.")]
    public float coverageAmount = 0.8f;

    [Tooltip("Use this object's world position as the center of the snow coverage.")]
    public bool followTransform = true;

    [Tooltip("Center used when Follow Transform is disabled.")]
    public Vector3 coverageCenter = Vector3.zero;

    [Min(0.01f)]
    [Tooltip("Radius of the area covered by the snow layer, in world units.")]
    public float coverageRadius = 1000f;

    [Min(0.001f)]
    [Tooltip("Soft edge of the coverage area, in world units.")]
    public float edgeSize = 1f;

    [Range(0f, 1f)]
    [Tooltip("Strength of the spherical area mask. Use Coverage Amount to adjust the overall amount of snow.")]
    public float intensity = 1f;

    // Retained for existing prefab data. The source graph's Noise input is zero,
    // so this field has no effect until a noise signal is connected to that input.
    [HideInInspector, Min(0f)]
    public float noiseStrength = 0f;

    [Min(0.01f)]
    public float smooth = 1f;

    private readonly Vector4[] positions = new Vector4[MaskCount];
    private readonly float[] radii = new float[MaskCount];
    private readonly float[] intensities = new float[MaskCount];
    private readonly float[] noiseStrengths = new float[MaskCount];
    private readonly float[] edgeSizes = new float[MaskCount];
    private readonly float[] smoothValues = new float[MaskCount];
    private bool hasAppliedGlobals;

    private void OnEnable() => Apply();
    private void OnValidate() => Apply();
    private void Update() => Apply();

    private void OnDisable()
    {
        if (!hasAppliedGlobals) return;
        ClearGlobalArrays();
        hasAppliedGlobals = false;
    }

    private void Reset()
    {
        coverageAmount = 0.8f;
        followTransform = true;
        coverageCenter = Vector3.zero;
        coverageRadius = 1000f;
        edgeSize = 1f;
        intensity = 1f;
        noiseStrength = 0f;
        smooth = 1f;
        Apply();
    }

    private void Apply()
    {
        if (!isActiveAndEnabled || !gameObject.scene.IsValid()) return;
#if UNITY_EDITOR
        // Editing the prefab must not replace the active scene's global mask.
        if (UnityEditor.EditorUtility.IsPersistent(this) ||
            UnityEditor.SceneManagement.EditorSceneManager.IsPreviewScene(gameObject.scene) ||
            UnityEditor.SceneManagement.PrefabStageUtility.GetPrefabStage(gameObject) != null)
            return;
#endif

        var center = followTransform ? transform.position : coverageCenter;
        for (var i = 0; i < MaskCount; i++)
        {
            // The shader combines four areas as 1 - product(1 - area).
            // Repeating one area four times would amplify its intensity.
            var activeSlot = i == 0;
            positions[i] = activeSlot ? (Vector4)center : Vector4.zero;
            radii[i] = activeSlot ? Mathf.Max(0.01f, coverageRadius) : 0f;
            intensities[i] = activeSlot ? Mathf.Clamp01(intensity) : 0f;
            noiseStrengths[i] = activeSlot ? Mathf.Max(0f, noiseStrength) : 0f;
            edgeSizes[i] = activeSlot ? Mathf.Max(0.001f, edgeSize) : 1f;
            smoothValues[i] = activeSlot ? Mathf.Max(0.01f, smooth) : 1f;
        }

        Shader.SetGlobalFloat(CoverageId, Mathf.Clamp01(coverageAmount));
        Shader.SetGlobalVectorArray(Prefix + "Position", positions);
        Shader.SetGlobalFloatArray(Prefix + "Radius", radii);
        Shader.SetGlobalFloatArray(Prefix + "Intensity", intensities);
        Shader.SetGlobalFloatArray(Prefix + "NoiseStrength", noiseStrengths);
        Shader.SetGlobalFloatArray(Prefix + "EdgeSize", edgeSizes);
        Shader.SetGlobalFloatArray(Prefix + "Smooth", smoothValues);
        hasAppliedGlobals = true;
    }

    private static void ClearGlobalArrays()
    {
        var zeros = new Vector4[MaskCount];
        var zeroFloats = new float[MaskCount];
        var safeEdgeSizes = new float[MaskCount];
        for (var i = 0; i < MaskCount; i++) safeEdgeSizes[i] = 1f;
        Shader.SetGlobalFloat(CoverageId, 0f);
        Shader.SetGlobalVectorArray(Prefix + "Position", zeros);
        Shader.SetGlobalFloatArray(Prefix + "Radius", zeroFloats);
        Shader.SetGlobalFloatArray(Prefix + "Intensity", zeroFloats);
        Shader.SetGlobalFloatArray(Prefix + "NoiseStrength", zeroFloats);
        Shader.SetGlobalFloatArray(Prefix + "EdgeSize", safeEdgeSizes);
        Shader.SetGlobalFloatArray(Prefix + "Smooth", safeEdgeSizes);
    }
}
