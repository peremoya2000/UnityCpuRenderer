using UnityEngine;
using UnityEngine.InputSystem;
using static UnityEditor.Searcher.SearcherWindow.Alignment;

[RequireComponent(typeof(Camera))]
public class DemoCamera : MonoBehaviour
{
    private const int MAX_VIEW_COUNT = 9;
    [SerializeField] private Transform[] viewPoints;

    [SerializeField] private float moveSpeed = 5f;
    [SerializeField] private float fastMultiplier = 4f;
    [SerializeField] private float mouseLookSensitivity = .1f;
    [SerializeField] private float stickLookSpeed = 60f;
    [SerializeField] private float lookStickDamping = 8f;
    [SerializeField] private float moveDampingSpeed = 8f;

    private bool locked = true;
    private InputControls controls;
    private Vector2 smoothedLookRate;
    private Vector3 smoothMoveVelocity;
    private float pitch, yaw = 0f;
    private int currentViewIndex = 0;
    private int previousViewInput = 0;

    private void Awake() { controls = new InputControls(); }
    private void OnEnable() { controls.DemoCamera.Enable(); }
    private void OnDisable() { controls.DemoCamera.Disable(); }
    private void OnDestroy() { controls.Dispose(); }

    private void Start()
    {
        Debug.Assert(viewPoints != null, "viewPoints must not be null", this);
        Debug.Assert(viewPoints.Length > 0, "viewPoints.Length must be greater than 0", this);
        SnapToView(0);
    }

    private void Update()
    {
        int viewCount = Mathf.Min(MAX_VIEW_COUNT, viewPoints.Length);
        for (int i = 0; i < viewCount; ++i)
        {
            if (Keyboard.current[Key.Digit1 + i].wasPressedThisFrame)
            {
                SnapToView(i);
                locked = true;
                return;
            }
        }

        int viewInput = Mathf.RoundToInt(controls.DemoCamera.SwitchView.ReadValue<float>());
        if (previousViewInput == 0 && viewInput != 0)
        {
            int viewIndex = (int)Mathf.Repeat(currentViewIndex + viewInput, viewPoints.Length);
            SnapToView(viewIndex);
            locked = true;
        }
        previousViewInput = viewInput;

        if (controls.DemoCamera.ToggleLock.WasPerformedThisFrame())
        {
            locked = !locked;
        }

        if (!locked)
        {
            Vector2 mouseDegrees = controls.DemoCamera.MouseLookDelta.ReadValue<Vector2>() * mouseLookSensitivity;
            Vector2 stick = controls.DemoCamera.LookStick.ReadValue<Vector2>();
            stick = stick.normalized * stick.sqrMagnitude;
            smoothedLookRate = Vector2.Lerp(smoothedLookRate, stick * stickLookSpeed, 1f - Mathf.Exp(-lookStickDamping * Time.deltaTime));
            Vector2 stickDegrees = smoothedLookRate * Time.deltaTime;

            Vector2 lookDegrees = mouseDegrees + stickDegrees;

            yaw += lookDegrees.x;
            pitch = Mathf.Clamp(pitch - lookDegrees.y, -89f, 89f);
            transform.rotation = Quaternion.Euler(pitch, yaw, 0f);

            Vector2 moveDelta = Vector2.ClampMagnitude(controls.DemoCamera.Move.ReadValue<Vector2>(), 1f);
            float speed = moveSpeed * (controls.DemoCamera.FastMove.IsPressed() ? fastMultiplier : 1f);
            Vector3 targetMove = speed * (transform.forward * moveDelta.y 
                                                            + transform.right * moveDelta.x 
                                                            + transform.up * controls.DemoCamera.Vertical.ReadValue<float>());
            smoothMoveVelocity = Vector3.Lerp(smoothMoveVelocity, targetMove, 1f - Mathf.Exp(-moveDampingSpeed * Time.deltaTime));
            transform.position += smoothMoveVelocity * Time.deltaTime;
        }
    }

    private void SnapToView(int viewIndex)
    {
        Debug.Assert(viewIndex >= 0 && viewIndex < viewPoints.Length, "viewIndex is out of range", this);
        transform.SetPositionAndRotation(viewPoints[viewIndex].position, viewPoints[viewIndex].rotation);
        pitch = Mathf.DeltaAngle(0f, viewPoints[viewIndex].eulerAngles.x);
        yaw = viewPoints[viewIndex].eulerAngles.y;
        smoothedLookRate = Vector2.zero;
        smoothMoveVelocity = Vector2.zero;
        currentViewIndex = viewIndex;
    }
}
