using BigWorldClient.Network.Protocol;
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

        [Header("Target")]
        [SerializeField] private Transform _target;

        [Header("Orbit")]
        [SerializeField] private float _orbitSensitivity = 2f;
        [SerializeField] private float _minPitch = -30f;
        [SerializeField] private float _maxPitch = 80f;

        [Header("Zoom")]
        [SerializeField] private float _defaultDistance = 6f;
        [SerializeField] private float _minDistance = 1f;
        [SerializeField] private float _maxDistance = 10f;
        [SerializeField] private float _zoomSensitivity = 0.1f;
        [SerializeField] private float _zoomSmoothing = 4f;

        [Header("Obstacle Avoidance")]
        [SerializeField] private LayerMask _obstacleMask;
        [SerializeField] private float _obstacleCameraRadius = 0.2f;

        public Transform MainCameraTransform { get; private set; }

        private float _currentYaw;
        private float _currentPitch;
        private float _currentDistance;
        private float _targetDistance;
        private float _zoomVelocity;

        private bool _recenteringEnabled;
        private bool _isRecentering;
        private float _recenteringWaitTimer;
        private float _recenteringTimer;
        private float _recenteringStartYaw;
        private float _recenteringTargetYaw;
        private float _recenteringWaitTime;
        private float _recenteringTimeDuration;

        private bool _isOrbiting;

        private void Awake()
        {
            Instance = this;
            MainCameraTransform = Camera.main.transform;

            _currentDistance = _defaultDistance;
            _targetDistance = _defaultDistance;

            if (_target != null)
            {
                Vector3 offset = MainCameraTransform.position - _target.position;
                _currentDistance = offset.magnitude;
                _targetDistance = _currentDistance;
                _currentYaw = Mathf.Atan2(offset.x, offset.z) * Mathf.Rad2Deg;
                _currentPitch = -Mathf.Asin(offset.y / _currentDistance) * Mathf.Rad2Deg;
            }

            _lastMousePosition = GetMousePosition();
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        private Vector2 _lastMousePosition;

        private void LateUpdate()
        {
            if (_target == null)
                return;

            HandleOrbitInput();
            HandleRecentering();
            HandleZoomInput();

            float effectiveYaw = _currentYaw;
            if (_recenteringEnabled && _isRecentering)
            {
                float t = _recenteringTimeDuration > 0f
                    ? Mathf.Clamp01(_recenteringTimer / _recenteringTimeDuration)
                    : 1f;

                float easedT = t * t * (3f - 2f * t);
                effectiveYaw = Mathf.LerpAngle(_recenteringStartYaw, _recenteringTargetYaw, easedT);
            }

            Quaternion rotation = Quaternion.Euler(_currentPitch, effectiveYaw, 0f);
            Vector3 desiredPosition = _target.position + rotation * (Vector3.back * _currentDistance);

            desiredPosition = ApplyObstacleAvoidance(desiredPosition);

            MainCameraTransform.position = desiredPosition;
            MainCameraTransform.LookAt(_target.position);
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
                if (!_isOrbiting)
                {

                    _isOrbiting = true;
                    _lastMousePosition = mousePosition;
                    return;
                }

                Vector2 delta = mousePosition - _lastMousePosition;
                _lastMousePosition = mousePosition;

                if (delta.magnitude > 0.01f)
                {
                    _currentYaw += delta.x * _orbitSensitivity * 0.1f;
                    _currentPitch -= delta.y * _orbitSensitivity * 0.1f;
                    _currentPitch = Mathf.Clamp(_currentPitch, _minPitch, _maxPitch);

                    if (delta.magnitude > 0.5f)
                    {
                        DisableRecentering();
                    }
                }
            }
            else
            {
                _isOrbiting = false;
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
            if (!_recenteringEnabled)
                return;

            if (_isOrbiting)
            {
                _recenteringEnabled = false;
                _isRecentering = false;
                return;
            }

            if (_isRecentering)
            {
                _recenteringTimer += Time.deltaTime;

                if (_recenteringTimer >= _recenteringTimeDuration)
                {

                    _currentYaw = _recenteringTargetYaw;
                    _isRecentering = false;
                    _recenteringEnabled = false;
                }
            }
            else
            {

                if (_recenteringWaitTimer < _recenteringWaitTime)
                {
                    _recenteringWaitTimer += Time.deltaTime;
                }
                else
                {

                    _isRecentering = true;
                    _recenteringStartYaw = _currentYaw;
                    _recenteringTargetYaw = _target.eulerAngles.y;
                    _recenteringTimer = 0f;
                }
            }
        }

        #endregion

        #region Zoom

        private void HandleZoomInput()
        {
            if (Mouse.current == null)
                return;

            float scrollValue = Mouse.current.scroll.y.ReadValue() / 120f;

            if (Mathf.Abs(scrollValue) > 0.001f)
            {
                _targetDistance -= scrollValue * _zoomSensitivity;
                _targetDistance = Mathf.Clamp(_targetDistance, _minDistance, _maxDistance);
            }

            if (Mathf.Abs(_currentDistance - _targetDistance) > 0.001f)
            {
                _currentDistance = Mathf.SmoothDamp(_currentDistance, _targetDistance,
                    ref _zoomVelocity, 1f / _zoomSmoothing);
            }
            else
            {
                _currentDistance = _targetDistance;
            }
        }

        #endregion

        #region Obstacle Avoidance

        private Vector3 ApplyObstacleAvoidance(Vector3 desiredPosition)
        {
            if (_obstacleMask == 0)
                return desiredPosition;

            Vector3 direction = (desiredPosition - _target.position).normalized;
            float _maxDistance = Vector3.Distance(_target.position, desiredPosition);

            if (Physics.SphereCast(_target.position, _obstacleCameraRadius, direction,
                out RaycastHit hit, _maxDistance, _obstacleMask, QueryTriggerInteraction.Ignore))
            {

                return _target.position + direction * Mathf.Max(0.3f, hit.distance - 0.2f);
            }

            return desiredPosition;
        }

        #endregion

        #region Public API

        public void SetTarget(Transform newTarget)
        {
            _target = newTarget;
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

            PlayerConfigTable config = PlayerConfigTable.Instance;
            if (config == null)
            {
                DisableRecentering();
                return;
            }

            var recenteringData = (movementInput == Vector2.down)
                ? config.BackwardsRecenteringData
                : config.SidewaysRecenteringData;

            ApplyRecenteringState(cameraVerticalAngle, recenteringData);
        }

        private void ApplyRecenteringState(
            float cameraVerticalAngle,
            List<CameraRecenteringEntry> recenteringDataList)
        {
            if (recenteringDataList != null)
            {
                foreach (var recenteringData in recenteringDataList)
                {
                    if (cameraVerticalAngle < recenteringData.MinimumAngle ||
                        cameraVerticalAngle > recenteringData.MaximumAngle)
                        continue;

                    EnableRecentering(recenteringData.WaitTime, recenteringData.RecenteringTime);
                    return;
                }
            }

            DisableRecentering();
        }

        public void EnableRecentering(float waitTime = -1f, float recenteringTime = -1f)
        {
            float resolvedWaitTime = waitTime < 0f ? DefaultHorizontalWaitTime : waitTime;
            float resolvedRecenteringTime = recenteringTime < 0f ? DefaultHorizontalRecenteringTime : recenteringTime;

            if (_recenteringEnabled
                && Mathf.Approximately(_recenteringWaitTime, resolvedWaitTime)
                && Mathf.Approximately(_recenteringTimeDuration, resolvedRecenteringTime))
                return;

            _recenteringEnabled = true;
            _isRecentering = false;
            _recenteringWaitTimer = 0f;
            _recenteringWaitTime = resolvedWaitTime;
            _recenteringTimeDuration = resolvedRecenteringTime;
        }

        public void DisableRecentering()
        {
            _recenteringEnabled = false;
            _isRecentering = false;
        }

        #endregion

        private void OnApplicationFocus(bool hasFocus)
        {
            if (!hasFocus)
            {
                _isOrbiting = false;
            }
        }
    }
}
