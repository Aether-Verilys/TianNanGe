using UnityEngine;

/// <summary>Forwards animation events from the visual child's Animator to the player root.</summary>
public sealed class StarterAssetsAnimationEvents : MonoBehaviour
{
    private StarterAssets.ThirdPersonController controller;
    private void Awake() => controller = GetComponentInParent<StarterAssets.ThirdPersonController>();
    public void OnFootstep(AnimationEvent animationEvent) => controller?.OnFootstep(animationEvent);
    public void OnLand(AnimationEvent animationEvent) => controller?.OnLand(animationEvent);
}
