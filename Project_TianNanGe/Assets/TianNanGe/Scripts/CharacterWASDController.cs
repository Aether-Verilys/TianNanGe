using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Small, reusable third-person movement component for a character prefab.
///
/// Assign the project's InputSystem_Actions asset to <see cref="inputActions"/>
/// (Player map / Move action).  If the asset is not assigned or the action cannot
/// be found, the component reads WASD and the arrow keys directly from the new
/// Input System so a newly dropped-in prefab remains usable.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(CharacterController))]
public sealed class CharacterWASDController : MonoBehaviour
{
    [Header("Input")]
    [Tooltip("InputSystem_Actions.inputactions. Leave empty to use the keyboard fallback.")]
    [SerializeField] private InputActionAsset inputActions;
    [SerializeField] private string actionMapName = "Player";
    [SerializeField] private string moveActionName = "Move";

    [Header("Movement")]
    [Min(0f)] [SerializeField] private float moveSpeed = 2.5f;
    [Min(0f)] [SerializeField] private float rotationSpeed = 720f;
    [SerializeField] private bool cameraRelative = true;
    [Min(0f)] [SerializeField] private float gravity = 20f;
    [SerializeField] private Camera movementCamera;

    [Header("Animation")]
    [Tooltip("Animator on the visual child. The controller sets this float to the input magnitude 0..1.")]
    [SerializeField] private Animator animator;
    [SerializeField] private string speedParameter = "Speed";
    [Min(0f)] [SerializeField] private float speedDamping = 0.12f;
    [Tooltip("Set false when the character's animation is deliberately allowed to drive root motion.")]
    [SerializeField] private bool disableRootMotion = true;

    private CharacterController characterController;
    private InputActionAsset runtimeInputActions;
    private InputActionMap moveActionMap;
    private InputAction moveAction;
    private bool hasInputAction;
    private bool hasSpeedParameter;
    private float verticalVelocity;

    /// <summary>Current planar input after clamping diagonals to unit length.</summary>
    public Vector2 MoveInput { get; private set; }

    /// <summary>World-space planar velocity produced by the last Update.</summary>
    public Vector3 PlanarVelocity { get; private set; }

    private void Reset()
    {
        characterController = GetComponent<CharacterController>();
        movementCamera = Camera.main;
        animator = GetComponentInChildren<Animator>();
    }

    private void Awake()
    {
        characterController = GetComponent<CharacterController>();
        if (movementCamera == null)
            movementCamera = Camera.main;
        if (animator == null)
            animator = GetComponentInChildren<Animator>();

        if (animator != null)
        {
            if (disableRootMotion)
                animator.applyRootMotion = false;
            hasSpeedParameter = HasFloatParameter(animator, speedParameter);
        }

        ResolveMoveAction();
    }

    private void OnEnable()
    {
        ResolveMoveAction();
        if (moveActionMap != null)
            moveActionMap.Enable();
    }

    private void OnDisable()
    {
        if (moveActionMap != null)
            moveActionMap.Disable();

        MoveInput = Vector2.zero;
        PlanarVelocity = Vector3.zero;
        verticalVelocity = 0f;
        if (animator != null && hasSpeedParameter)
            animator.SetFloat(speedParameter, 0f);
    }

    private void OnDestroy()
    {
        if (runtimeInputActions != null)
            Destroy(runtimeInputActions);
    }

    private void Update()
    {
        MoveInput = ReadMoveInput();
        Vector3 direction = ToWorldDirection(MoveInput);
        PlanarVelocity = direction * moveSpeed;

        if (direction.sqrMagnitude > 0.0001f)
        {
            Quaternion desiredRotation = Quaternion.LookRotation(direction, Vector3.up);
            transform.rotation = Quaternion.RotateTowards(
                transform.rotation, desiredRotation, rotationSpeed * Time.deltaTime);
        }

        if (characterController.isGrounded && verticalVelocity < 0f)
            verticalVelocity = -2f;
        verticalVelocity -= gravity * Time.deltaTime;

        Vector3 velocity = PlanarVelocity + Vector3.up * verticalVelocity;
        characterController.Move(velocity * Time.deltaTime);

        UpdateAnimation(MoveInput.magnitude);
    }

    private void ResolveMoveAction()
    {
        moveActionMap = null;
        moveAction = null;
        hasInputAction = false;

        if (inputActions == null || string.IsNullOrEmpty(actionMapName) ||
            string.IsNullOrEmpty(moveActionName))
            return;

        // A private copy prevents a disabled prefab from disabling input for
        // another character or a PlayerInput component using the same asset.
        if (runtimeInputActions == null)
            runtimeInputActions = Instantiate(inputActions);
        moveActionMap = runtimeInputActions.FindActionMap(actionMapName, false);
        if (moveActionMap == null)
            return;

        moveAction = moveActionMap.FindAction(moveActionName, false);
        hasInputAction = moveAction != null;
    }

    private Vector2 ReadMoveInput()
    {
        if (hasInputAction)
            return Vector2.ClampMagnitude(moveAction.ReadValue<Vector2>(), 1f);

        Keyboard keyboard = Keyboard.current;
        if (keyboard == null)
            return Vector2.zero;

        Vector2 input = Vector2.zero;
        if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed) input.y += 1f;
        if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed) input.y -= 1f;
        if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed) input.x += 1f;
        if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed) input.x -= 1f;
        return Vector2.ClampMagnitude(input, 1f);
    }

    private Vector3 ToWorldDirection(Vector2 input)
    {
        Vector3 direction = new Vector3(input.x, 0f, input.y);
        if (!cameraRelative)
            return direction.sqrMagnitude > 1f ? direction.normalized : direction;

        if (movementCamera == null)
            movementCamera = Camera.main;
        if (movementCamera == null)
            return direction.sqrMagnitude > 1f ? direction.normalized : direction;

        Vector3 forward = Vector3.ProjectOnPlane(movementCamera.transform.forward, Vector3.up);
        Vector3 right = Vector3.ProjectOnPlane(movementCamera.transform.right, Vector3.up);
        if (forward.sqrMagnitude < 0.0001f || right.sqrMagnitude < 0.0001f)
            return direction.sqrMagnitude > 1f ? direction.normalized : direction;

        forward.Normalize();
        right.Normalize();
        Vector3 worldDirection = right * input.x + forward * input.y;
        return worldDirection.sqrMagnitude > 1f ? worldDirection.normalized : worldDirection;
    }

    private void UpdateAnimation(float normalizedSpeed)
    {
        if (animator == null || !hasSpeedParameter)
            return;
        animator.SetFloat(speedParameter, normalizedSpeed, speedDamping, Time.deltaTime);
    }

    private static bool HasFloatParameter(Animator target, string parameterName)
    {
        if (target == null || string.IsNullOrEmpty(parameterName))
            return false;
        foreach (AnimatorControllerParameter parameter in target.parameters)
        {
            if (parameter.name == parameterName &&
                parameter.type == AnimatorControllerParameterType.Float)
                return true;
        }
        return false;
    }
}
