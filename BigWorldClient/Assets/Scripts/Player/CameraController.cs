using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BigWorldClient
{
    public class CameraController : MonoBehaviour
    {
        public static CameraController Instance { get; private set; }

        [field: SerializeField] public float DefaultHorizontalWaitTime { get; private set; } = 0f;
        [field: SerializeField] public float DefaultHorizontalRecenteringTime { get; private set; } = 4f;

        [Header("Recentering Data")]
        [SerializeField] private List<PlayerCameraRecenteringData> backwardsRecenteringData;
        [SerializeField] private List<PlayerCameraRecenteringData> sidewaysRecenteringData;

        [Header("Target")]
        [SerializeField] private Transform target;

        [Header("Orbit")]
        [SerializeField] private float orbitSensitivity = 2f;
        [SerializeField] private float minPitch = -30f;
        [SerializeField] private float maxPitch = 80f;

        [Header("Zoom")]
        [SerializeField] private float defaultDistance = 6f;
        [SerializeField] private float minDistance = 1f;
        [SerializeField] private float maxDistance = 10f;
        [SerializeField] private float zoomSensitivity = 0.1f;
        [SerializeField] private float zoomSmoothing = 4f;

        [Header("Obstacle Avoidance")]
        [SerializeField] private LayerMask obstacleMask;
        [SerializeField] private float obstacleCameraRadius = 0.2f;

        public Transform MainCameraTransform { get; private set; }

        // Spherical coordinates
        private float currentYaw;
        private float currentPitch;
        private float currentDistance;
        private float targetDistance;
        private float zoomVelocity;

        // Recentering state
        private bool recenteringEnabled;
        private bool isRecentering;
        private float recenteringWaitTimer;
        private float recenteringTimer;
        private float recenteringStartYaw;
        private float recenteringTargetYaw;
        private float recenteringWaitTime;
        private float recenteringTimeDuration;

        // Orbit input state
        private bool isOrbiting;

        private void Awake()
        {
            Instance = this;
            MainCameraTransform = Camera.main.transform;

            currentDistance = defaultDistance;
            targetDistance = defaultDistance;

            // Initialize spherical coords from current camera position
            if (target != null)
            {
                Vector3 offset = MainCameraTransform.position - target.position;
                currentDistance = offset.magnitude;
                targetDistance = currentDistance;
                currentYaw = Mathf.Atan2(offset.x, offset.z) * Mathf.Rad2Deg;
                currentPitch = -Mathf.Asin(offset.y / currentDistance) * Mathf.Rad2Deg;
            }

            lastMousePosition = GetMousePosition();
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        private Vector2 lastMousePosition;

        private void LateUpdate()
        {
            if (target == null)
                return;

            HandleOrbitInput();
            HandleRecentering();
            HandleZoomInput();

            // Build rotation from spherical coords
            float effectiveYaw = currentYaw;
            if (recenteringEnabled && isRecentering)
            {
                float t = recenteringTimeDuration > 0f
                    ? Mathf.Clamp01(recenteringTimer / recenteringTimeDuration)
                    : 1f;
                // Smoothstep easing
                float easedT = t * t * (3f - 2f * t);
                effectiveYaw = Mathf.LerpAngle(recenteringStartYaw, recenteringTargetYaw, easedT);
            }

            Quaternion rotation = Quaternion.Euler(currentPitch, effectiveYaw, 0f);
            Vector3 desiredPosition = target.position + rotation * (Vector3.back * currentDistance);
            // Obstacle avoidance
            desiredPosition = ApplyObstacleAvoidance(desiredPosition);

            // Apply to camera transform
            MainCameraTransform.position = desiredPosition;
            MainCameraTransform.LookAt(target.position);
        }

        #region Orbit Input

        private void HandleOrbitInput()
        {
            if (Mouse.current == null)
                return;

            Vector2 mousePosition = GetMousePosition();
            bool leftButtonPressed = Mouse.current.leftButton.isPressed;
            bool mouseInWindow = IsMouseInGameWindow(mousePosition);

            if (leftButtonPressed && mouseInWindow)
            {
                if (!isOrbiting)
                {
                    // Orbit just started — reset last position to avoid jump
                    isOrbiting = true;
                    lastMousePosition = mousePosition;
                    return;
                }

                Vector2 delta = mousePosition - lastMousePosition;
                lastMousePosition = mousePosition;

                if (delta.magnitude > 0.01f)
                {
                    currentYaw += delta.x * orbitSensitivity * 0.1f;
                    currentPitch -= delta.y * orbitSensitivity * 0.1f;
                    currentPitch = Mathf.Clamp(currentPitch, minPitch, maxPitch);

                    // Cancel recentering when user manually orbits
                    if (delta.magnitude > 0.5f)
                    {
                        DisableRecentering();
                    }
                }
            }
            else
            {
                isOrbiting = false;
            }
        }

        private static Vector2 GetMousePosition()
        {
            return Mouse.current?.position.ReadValue() ?? Vector2.zero;
        }

        private static bool IsMouseInGameWindow(Vector2 mousePosition)
        {
            return mousePosition.x >= 0f && mousePosition.x <= Screen.width &&
                   mousePosition.y >= 0f && mousePosition.y <= Screen.height &&
                   Application.isFocused;
        }

        #endregion

        #region Recentering

        private void HandleRecentering()
        {
            if (!recenteringEnabled)
                return;
            
            // Don't recenter while orbiting
            if (isOrbiting)
            {
                recenteringEnabled = false;
                isRecentering = false;
                return;
            }

            if (isRecentering)
            {
                recenteringTimer += Time.deltaTime;

                if (recenteringTimer >= recenteringTimeDuration)
                {
                    // Recentering complete — snap to target
                    currentYaw = recenteringTargetYaw;
                    isRecentering = false;
                    recenteringEnabled = false;
                }
            }
            else
            {
                // Waiting phase
                if (recenteringWaitTimer < recenteringWaitTime)
                {
                    recenteringWaitTimer += Time.deltaTime;
                }
                else
                {
                    // Start the recentering movement
                    isRecentering = true;
                    recenteringStartYaw = currentYaw;
                    recenteringTargetYaw = target.eulerAngles.y;
                    recenteringTimer = 0f;
                }
            }
        }

        #endregion

        #region Zoom

        private void HandleZoomInput()
        {
            if (Mouse.current == null)
                return;

            // Mouse scroll returns ~120 per notch on Windows; normalize to ~1 per notch
            float scrollValue = Mouse.current.scroll.y.ReadValue() / 120f;

            if (Mathf.Abs(scrollValue) > 0.001f)
            {
                targetDistance -= scrollValue * zoomSensitivity;
                targetDistance = Mathf.Clamp(targetDistance, minDistance, maxDistance);
            }

            // Smooth zoom
            if (Mathf.Abs(currentDistance - targetDistance) > 0.001f)
            {
                currentDistance = Mathf.SmoothDamp(currentDistance, targetDistance,
                    ref zoomVelocity, 1f / zoomSmoothing);
            }
            else
            {
                currentDistance = targetDistance;
            }
        }

        #endregion

        #region Obstacle Avoidance

        private Vector3 ApplyObstacleAvoidance(Vector3 desiredPosition)
        {
            if (obstacleMask == 0)
                return desiredPosition;

            Vector3 direction = (desiredPosition - target.position).normalized;
            float maxDistance = Vector3.Distance(target.position, desiredPosition);

            if (Physics.SphereCast(target.position, obstacleCameraRadius, direction,
                out RaycastHit hit, maxDistance, obstacleMask, QueryTriggerInteraction.Ignore))
            {
                // Pull camera in front of the obstacle
                return target.position + direction * Mathf.Max(0.3f, hit.distance - 0.2f);
            }

            return desiredPosition;
        }

        #endregion

        #region Public API

        public void SetTarget(Transform newTarget)
        {
            target = newTarget;
        }

        public void UpdateRecenteringState(Vector2 movementInput)
        {
            if (movementInput == Vector2.zero)
                return;

            if (movementInput == Vector2.up)
            {
                DisableRecentering();
                return;
            }

            float cameraVerticalAngle = MainCameraTransform.eulerAngles.x;
            if (cameraVerticalAngle >= 270f)
                cameraVerticalAngle -= 360f;
            cameraVerticalAngle = Mathf.Abs(cameraVerticalAngle);

            var recenteringData = (movementInput == Vector2.down)
                ? backwardsRecenteringData
                : sidewaysRecenteringData;

            ApplyRecenteringState(cameraVerticalAngle, recenteringData);
        }

        private void ApplyRecenteringState(
            float cameraVerticalAngle,
            List<PlayerCameraRecenteringData> recenteringDataList)
        {
            
            foreach (var recenteringData in recenteringDataList)
            {
                if (!recenteringData.IsWithinRange(cameraVerticalAngle))
                    continue;

                EnableRecentering(recenteringData.WaitTime, recenteringData.RecenteringTime);
                return;
            }

            DisableRecentering();
        }

        public void EnableRecentering(float waitTime = -1f, float recenteringTime = -1f)
        {
            float resolvedWaitTime = waitTime < 0f ? DefaultHorizontalWaitTime : waitTime;
            float resolvedRecenteringTime = recenteringTime < 0f ? DefaultHorizontalRecenteringTime : recenteringTime;

            // Already enabled with the same parameters — don't reset the wait timer
            if (recenteringEnabled
                && Mathf.Approximately(recenteringWaitTime, resolvedWaitTime)
                && Mathf.Approximately(recenteringTimeDuration, resolvedRecenteringTime))
                return;

            recenteringEnabled = true;
            isRecentering = false;
            recenteringWaitTimer = 0f;
            recenteringWaitTime = resolvedWaitTime;
            recenteringTimeDuration = resolvedRecenteringTime;
        }

        public void DisableRecentering()
        {
            recenteringEnabled = false;
            isRecentering = false;
        }

        #endregion

        private void OnApplicationFocus(bool hasFocus)
        {
            if (!hasFocus)
            {
                isOrbiting = false;
            }
        }
    }
}
