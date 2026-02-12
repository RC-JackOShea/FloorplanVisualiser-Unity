using System;
using System.Collections;
using System.Threading.Tasks;
using FloorplanVectoriser.Capture;
using FloorplanVectoriser.CameraSystem;
using FloorplanVectoriser.Data;
using FloorplanVectoriser.Inference;
using FloorplanVectoriser.MeshGen;
using FloorplanVectoriser.PostProcessing;
using UnityEngine;
using UnityEngine.UI;

namespace FloorplanVectoriser.App
{
    /// <summary>
    /// Top-level state machine that coordinates the full app flow:
    /// CameraPreview → ImageReview → Processing → CameraTransition → Viewing.
    /// Assign all references in the Unity Inspector.
    /// </summary>
    public class AppController : MonoBehaviour
    {
        [Header("Components")]
        [SerializeField] private ImageCapture imageCapture;
        [SerializeField] private CameraController cameraController;
        [SerializeField] private FloorplanInference inference;

        [Header("Materials")]
        [SerializeField] private Material wallMaterial;
        [SerializeField] private Material doorMaterial;
        [SerializeField] private Material windowMaterial;

        [Header("Mesh Settings")]
        [SerializeField] private float worldScale = 10f;
        [SerializeField] private float extrudeHeight = 2f;

        [Header("Mesh Animation")]
        [SerializeField] private float meshScaleUpDuration = 0.6f;

        [Header("Post-Processing")]
        [SerializeField] private float detectionThreshold = 0.5f;

        [Header("UI - Capture")]
        [SerializeField] private GameObject captureUI;
        [SerializeField] private Button captureButton;
        [SerializeField] private Button galleryButton;

        [Header("UI - Review")]
        [SerializeField] private GameObject reviewUI;
        [SerializeField] private Button approveButton;
        [SerializeField] private Button retakeButton;

        [Header("UI - Processing")]
        [SerializeField] private GameObject processingUI;

        AppState _currentState;
        GameObject _generatedMeshRoot;

        void Start()
        {
            // Wire up buttons
            if (captureButton != null) captureButton.onClick.AddListener(OnCapturePressed);
            if (galleryButton != null) galleryButton.onClick.AddListener(OnGalleryPressed);
            if (approveButton != null) approveButton.onClick.AddListener(OnApprovePressed);
            if (retakeButton != null) retakeButton.onClick.AddListener(OnRetakePressed);

            TransitionTo(AppState.CameraPreview);
        }

        void TransitionTo(AppState newState)
        {
            Debug.Log("Transitioning to " + newState);
            _currentState = newState;

            // Hide all UI
            SetActive(captureUI, false);
            SetActive(reviewUI, false);
            SetActive(processingUI, false);

            switch (newState)
            {
                case AppState.CameraPreview:
                    SetActive(captureUI, true);
                    cameraController.SetupOrthographic(worldScale / 2f);
                    cameraController.StopOrbit();
                    // Set up the 3D preview plane centered in camera view BEFORE starting preview
                    // so the texture can be applied to the plane material
                    imageCapture.SetupPreviewPlane(worldScale);
                    imageCapture.StartPreview();
                    // Clean up any previous meshes
                    if (_generatedMeshRoot != null)
                    {
                        Destroy(_generatedMeshRoot);
                        _generatedMeshRoot = null;
                    }
                    break;

                case AppState.ImageReview:
                    Debug.Log("Transitioning to ImageReview. reviewUI null: " + (reviewUI == null));
                    SetActive(reviewUI, true);
                    break;

                case AppState.Processing:
                    SetActive(processingUI, true);
                    RunPipeline();
                    break;

                case AppState.CameraTransition:
                    // No UI shown during transition
                    // Preview plane remains visible under the generated mesh
                    break;

                case AppState.Viewing:
                    // Orbit is enabled by CameraController after transition
                    break;
            }
        }

        // --- Button handlers ---

        void OnCapturePressed()
        {
            Debug.Log("OnCapturePressed");
            if (_currentState != AppState.CameraPreview) return;
            Debug.Log("Capturing image");
            imageCapture.Capture();
            TransitionTo(AppState.ImageReview);
        }

        void OnGalleryPressed()
        {
            if (_currentState != AppState.CameraPreview) return;
            imageCapture.StopPreview();
            imageCapture.OpenGallery(tex =>
            {
                if (tex != null)
                    TransitionTo(AppState.ImageReview);
                else
                    imageCapture.StartPreview(); // Cancelled, resume preview
            });
        }

        void OnApprovePressed()
        {
            if (_currentState != AppState.ImageReview) return;
            TransitionTo(AppState.Processing);
        }

        void OnRetakePressed()
        {
            if (_currentState != AppState.ImageReview) return;
            TransitionTo(AppState.CameraPreview);
        }

        // --- Pipeline ---

        void RunPipeline()
        {
            Texture2D captured = imageCapture.GetCapturedImage();
            if (captured == null)
            {
                Debug.LogError("No captured image available.");
                TransitionTo(AppState.CameraPreview);
                return;
            }

            // Run inference → post-processing → mesh generation
            inference.RunInference(captured, OnInferenceComplete);
        }

        async void OnInferenceComplete(float[,,] heatmaps, float[,,] rooms, float[,,] icons)
        {
            // Post-processing is CPU-bound; run off the main thread
            var postProcessor = new PostProcessor(detectionThreshold);
            PolygonResult result = null;

            await Task.Run(() =>
            {
                result = postProcessor.Process(heatmaps, rooms, icons);
            });

            if (result == null || result.Polygons.Count == 0)
            {
                Debug.LogWarning("No structures detected in the floorplan.");
                TransitionTo(AppState.CameraPreview);
                return;
            }

            Debug.Log($"Detected: {CountByCategory(result, StructureCategory.Wall)} walls, " +
                      $"{CountByCategory(result, StructureCategory.Door)} doors, " +
                      $"{CountByCategory(result, StructureCategory.Window)} windows");

            // Build 3D meshes (must be on main thread)
            // Pass the image aspect ratio so meshes align with the preview plane
            float aspectRatio = imageCapture.GetCurrentAspectRatio();
            var meshBuilder = new FloorplanMeshBuilder(
                wallMaterial, doorMaterial, windowMaterial, worldScale, extrudeHeight, aspectRatio);
            var (root, bounds) = meshBuilder.BuildFromResult(result);
            _generatedMeshRoot = root;

            // Start mesh with Y scale at zero for expand animation
            _generatedMeshRoot.transform.localScale = new Vector3(1f, 0f, 1f);

            // Transition camera to perspective orbit
            TransitionTo(AppState.CameraTransition);
            cameraController.LerpToPerspective(bounds.center, bounds, () =>
            {
                // Animate mesh scaling up from ground after camera arrives
                StartCoroutine(AnimateMeshScaleUp(() => TransitionTo(AppState.Viewing)));
            });
        }

        // --- Helpers ---

        IEnumerator AnimateMeshScaleUp(Action onComplete)
        {
            if (_generatedMeshRoot == null)
            {
                onComplete?.Invoke();
                yield break;
            }

            float elapsed = 0f;
            while (elapsed < meshScaleUpDuration)
            {
                float t = Mathf.SmoothStep(0f, 1f, elapsed / meshScaleUpDuration);
                _generatedMeshRoot.transform.localScale = new Vector3(1f, t, 1f);
                elapsed += Time.deltaTime;
                yield return null;
            }

            _generatedMeshRoot.transform.localScale = Vector3.one;
            onComplete?.Invoke();
        }

        static void SetActive(GameObject obj, bool active)
        {
            if (obj != null) obj.SetActive(active);
        }

        static int CountByCategory(PolygonResult result, StructureCategory cat)
        {
            int count = 0;
            foreach (var p in result.Polygons)
                if (p.Category == cat) count++;
            return count;
        }
    }
}
