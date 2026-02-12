using System;
using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FloorplanVectoriser.CameraSystem
{
    /// <summary>
    /// Controls the camera throughout the app lifecycle:
    /// 1. Orthographic top-down during capture
    /// 2. Animated transition to perspective after mesh generation
    /// 3. Swipe-to-orbit in viewing mode
    /// </summary>
    public class CameraController : MonoBehaviour
    {
        [Header("Transition Settings")]
        [SerializeField] private float transitionDuration = 3f;
        [SerializeField] private float orbitElevation = 30f;
        [SerializeField] private float perspectiveFOV = 60f;

        [Header("Orbit Settings")]
        [SerializeField] private float swipeSensitivity = 0.3f;
        [SerializeField] private float minPitch = 10f;
        [SerializeField] private float maxPitch = 80f;

        [Header("Zoom Settings")]
        [SerializeField] private float minZoomDistance = 2f;
        [SerializeField] private float maxZoomDistance = 50f;
        [SerializeField] private float mouseScrollSensitivity = 2f;
        [SerializeField] private float pinchSensitivity = 0.02f;
        [SerializeField] private float zoomSmoothing = 10f;

        Camera _cam;
        Vector3 _orbitTarget;
        float _orbitDistance;
        float _targetOrbitDistance;
        float _currentYaw;
        float _currentPitch;
        bool _isOrbiting;
        float _previousPinchDistance;

        void Awake()
        {
            _cam = GetComponent<Camera>();
        }

        /// <summary>
        /// Set up the camera for the initial top-down orthographic view.
        /// The camera looks straight down at the world origin.
        /// </summary>
        public void SetupOrthographic(float orthoSize)
        {
            _cam.orthographic = true;
            _cam.orthographicSize = orthoSize;
            _cam.nearClipPlane = 0.1f;
            _cam.farClipPlane = 100f;

            // Position above center, looking down
            float halfSize = orthoSize;
            transform.position = new Vector3(halfSize, 20f, halfSize);
            transform.rotation = Quaternion.Euler(90f, 0f, 0f);

            _isOrbiting = false;
        }

        /// <summary>
        /// Smoothly transition from orthographic top-down to a perspective orbit view.
        /// </summary>
        /// <param name="targetCenter">Center of the floorplan bounding box.</param>
        /// <param name="bounds">Combined bounds of all generated meshes.</param>
        /// <param name="onComplete">Called when the transition finishes.</param>
        public void LerpToPerspective(Vector3 targetCenter, Bounds bounds, Action onComplete)
        {
            StartCoroutine(TransitionCoroutine(targetCenter, bounds, onComplete));
        }

        IEnumerator TransitionCoroutine(Vector3 center, Bounds bounds, Action onComplete)
        {
            _orbitTarget = center;
            _orbitDistance = bounds.extents.magnitude * 2.5f;
            _targetOrbitDistance = _orbitDistance;
            _currentYaw = 45f; // Start at a nice angle
            _currentPitch = orbitElevation; // Start at the default elevation

            // Calculate destination pose
            Vector3 orbitOffset = Quaternion.Euler(_currentPitch, _currentYaw, 0f) *
                                  new Vector3(0f, 0f, -_orbitDistance);
            Vector3 endPos = center + orbitOffset;
            Quaternion endRot = Quaternion.LookRotation(center - endPos);

            Vector3 startPos = transform.position;
            Quaternion startRot = transform.rotation;

            // Calculate a perspective FOV that matches the current orthographic view
            float startOrthoSize = _cam.orthographicSize;
            float distanceToCenter = Vector3.Distance(startPos, center);
            float startFOV = 2f * Mathf.Atan(startOrthoSize / distanceToCenter) * Mathf.Rad2Deg;

            // Switch to perspective immediately before the lerp begins
            _cam.orthographic = false;
            _cam.fieldOfView = startFOV;

            float elapsed = 0f;
            while (elapsed < transitionDuration)
            {
                float t = elapsed / transitionDuration;
                float smooth = Mathf.SmoothStep(0f, 1f, t);

                transform.position = Vector3.Lerp(startPos, endPos, smooth);
                transform.rotation = Quaternion.Slerp(startRot, endRot, smooth);
                _cam.fieldOfView = Mathf.Lerp(startFOV, perspectiveFOV, smooth);

                elapsed += Time.deltaTime;
                yield return null;
            }

            // Snap to final state
            _cam.orthographic = false;
            _cam.fieldOfView = perspectiveFOV;
            transform.position = endPos;
            transform.rotation = endRot;
            _isOrbiting = true;

            onComplete?.Invoke();
        }

        void Update()
        {
            if (!_isOrbiting) return;

            HandleOrbitInput();
            HandleZoomInput();
            ApplyOrbitAndZoom();
        }

        void HandleOrbitInput()
        {
            float deltaX = 0f;
            float deltaY = 0f;

            // Touch input for mobile (single finger drag)
            if (Touchscreen.current != null && Touchscreen.current.primaryTouch.press.isPressed)
            {
                int activeTouches = GetActiveTouchCount();
                if (activeTouches == 1)
                {
                    deltaX = Touchscreen.current.primaryTouch.delta.x.ReadValue();
                    deltaY = Touchscreen.current.primaryTouch.delta.y.ReadValue();
                }
            }
            // Mouse input for PC
            else if (Mouse.current != null && Mouse.current.leftButton.isPressed)
            {
                deltaX = Mouse.current.delta.x.ReadValue();
                deltaY = Mouse.current.delta.y.ReadValue();
            }

            // Apply yaw (horizontal rotation) from X movement
            _currentYaw += deltaX * swipeSensitivity;

            // Apply pitch (vertical rotation) from Y movement
            // Subtract because moving mouse/finger up should look higher (decrease pitch)
            _currentPitch -= deltaY * swipeSensitivity;
            _currentPitch = Mathf.Clamp(_currentPitch, minPitch, maxPitch);
        }

        void HandleZoomInput()
        {
            // Mouse scroll wheel zoom
            if (Mouse.current != null)
            {
                float scrollDelta = Mouse.current.scroll.y.ReadValue();
                if (Mathf.Abs(scrollDelta) > 0.01f)
                {
                    _targetOrbitDistance -= scrollDelta * mouseScrollSensitivity;
                    _targetOrbitDistance = Mathf.Clamp(_targetOrbitDistance, minZoomDistance, maxZoomDistance);
                }
            }

            // Pinch-to-zoom for touch
            if (Touchscreen.current != null)
            {
                int activeTouches = GetActiveTouchCount();

                if (activeTouches >= 2)
                {
                    var touch0 = Touchscreen.current.touches[0];
                    var touch1 = Touchscreen.current.touches[1];

                    Vector2 pos0 = touch0.position.ReadValue();
                    Vector2 pos1 = touch1.position.ReadValue();
                    float currentPinchDistance = Vector2.Distance(pos0, pos1);

                    // Check if this is a new pinch gesture
                    if (_previousPinchDistance > 0f)
                    {
                        float pinchDelta = currentPinchDistance - _previousPinchDistance;
                        _targetOrbitDistance -= pinchDelta * pinchSensitivity;
                        _targetOrbitDistance = Mathf.Clamp(_targetOrbitDistance, minZoomDistance, maxZoomDistance);
                    }

                    _previousPinchDistance = currentPinchDistance;
                }
                else
                {
                    // Reset pinch tracking when not using two fingers
                    _previousPinchDistance = 0f;
                }
            }
        }

        int GetActiveTouchCount()
        {
            int count = 0;
            if (Touchscreen.current != null)
            {
                foreach (var touch in Touchscreen.current.touches)
                {
                    if (touch.press.isPressed)
                        count++;
                }
            }
            return count;
        }

        void ApplyOrbitAndZoom()
        {
            // Smoothly interpolate to target zoom distance
            _orbitDistance = Mathf.Lerp(_orbitDistance, _targetOrbitDistance, Time.deltaTime * zoomSmoothing);

            // Apply orbit position using current pitch and yaw
            Vector3 offset = Quaternion.Euler(_currentPitch, _currentYaw, 0f) *
                             new Vector3(0f, 0f, -_orbitDistance);
            transform.position = _orbitTarget + offset;
            transform.LookAt(_orbitTarget);
        }

        /// <summary>Stop orbiting (e.g., when resetting to camera preview).</summary>
        public void StopOrbit()
        {
            _isOrbiting = false;
        }
    }
}
