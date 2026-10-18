using UnityEngine;
using UnityEngine.InputSystem;
using Unity.Cinemachine;
using StarterAssets;

/// <summary>Switches the official controller between gameplay and the original scene view.</summary>
public sealed class StarterAssetsCameraMode : MonoBehaviour
{
    public ThirdPersonController player;
    public CinemachineCamera followCamera;
    public CinemachineCamera overviewCamera;
    public bool startInThirdPerson = true;
    public bool IsThirdPerson { get; private set; }
    private StarterAssetsInputs inputs;
    private PlayerInput playerInput;

    private void Start()
    {
        inputs = player.GetComponent<StarterAssetsInputs>();
        playerInput = player.GetComponent<PlayerInput>();
        SetThirdPerson(startInThirdPerson);
    }

    private void Update()
    {
        var keyboard = Keyboard.current;
        if (keyboard != null && keyboard.fKey.wasPressedThisFrame)
            SetThirdPerson(!IsThirdPerson);
        if (IsThirdPerson && keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
            SetCursor(false);
        if (IsThirdPerson && Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
            SetCursor(true);
    }

    public void SetThirdPerson(bool active)
    {
        IsThirdPerson = active;
        followCamera.Priority = active ? 20 : 0;
        overviewCamera.Priority = active ? 0 : 20;
        if (active)
        {
            player.ResetCameraRotation(player.transform.eulerAngles.y, 12f);
            playerInput.ActivateInput();
        }
        else
        {
            playerInput.DeactivateInput();
            inputs.MoveInput(Vector2.zero);
            inputs.JumpInput(false);
            inputs.SprintInput(false);
        }
        SetCursor(active);
    }

    private void SetCursor(bool locked)
    {
        inputs.cursorLocked = locked;
        inputs.cursorInputForLook = locked;
        inputs.LookInput(Vector2.zero);
        player.LockCameraPosition = !locked;
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !locked;
    }

    private void OnDisable()
    {
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }
}
