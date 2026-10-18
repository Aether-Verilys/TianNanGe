using UnityEngine;

/// <summary>
/// Keeps a reusable snowfall volume centered above the active camera.
/// </summary>
public sealed class SnowfallFollowCamera : MonoBehaviour
{
    public bool followCamera = true;
    public float height = 18f;
    public float horizontalSize = 36f;

    private void LateUpdate()
    {
        if (!followCamera)
            return;
        var camera = Camera.main;
        if (camera == null)
            return;
        var position = camera.transform.position;
        transform.position = new Vector3(position.x, position.y + height, position.z);
    }
}
