using System;
using System.Collections;
using System.IO;
using System.Threading.Tasks;
using FloorplanVectoriser.Capture;
using FloorplanVectoriser.CameraSystem;
using FloorplanVectoriser.Data;
using FloorplanVectoriser.Inference;
using FloorplanVectoriser.Conversion;
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
        [Tooltip("Number of meshes to create per frame during generation (lower = less GPU pressure on mobile)")]
        [SerializeField] private int meshesPerFrame = 4;

        [Header("Mesh Animation")]
        [SerializeField] private float meshScaleUpDuration = 0.6f;

        [Header("Post-Processing")]
        [Tooltip("Confidence threshold for wall/door/window detection (lower = more detections, may include false positives)")]
        [SerializeField] private float detectionThreshold = 0.5f;
        [Tooltip("Use lower threshold on mobile due to CPU inference differences")]
        [SerializeField] private float mobileThresholdMultiplier = 0.7f;

        [Header("Sketch Export")]
        [Tooltip("Photo capture size in metres — maps normalized [0,1] coordinates to world scale")]
        [SerializeField] private Vector2 photoCaptureSize = new Vector2(7f, 7f);

        [Header("Debug Visualisation")]
        [SerializeField] private bool showDebugPoints = true;
        [SerializeField] private bool showWeldedPoints = true;
        [SerializeField] private bool showSplinePoints = true;
        [Tooltip("Max distance (m) to weld nearby wall segment endpoints together")]
        [SerializeField] private float weldTolerance = 0.25f;

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

        [Header("UI - Viewing")]
        [SerializeField] private GameObject viewingUI;
        [SerializeField] private Button resetButton;
        [SerializeField] private Button saveButton;

        AppState _currentState;
        GameObject _generatedMeshRoot;
        SketchFile _lastSketch;
        string _lastSketchJson;

        void Start()
        {
            Application.targetFrameRate = 120;
            
            // Wire up buttons
            if (captureButton != null) captureButton.onClick.AddListener(OnCapturePressed);
            if (galleryButton != null) galleryButton.onClick.AddListener(OnGalleryPressed);
            if (approveButton != null) approveButton.onClick.AddListener(OnApprovePressed);
            if (retakeButton != null) retakeButton.onClick.AddListener(OnRetakePressed);
            if (resetButton != null) resetButton.onClick.AddListener(OnResetPressed);
            if (saveButton != null) saveButton.onClick.AddListener(OnSavePressed);

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
            SetActive(viewingUI, false);

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
                    // Clean up previous state
                    _lastSketch = null;
                    _lastSketchJson = null;
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
                    SetActive(viewingUI, true);
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

        void OnResetPressed()
        {
            if (_currentState != AppState.Viewing) return;
            // Stop orbit and go back to camera preview to start fresh
            cameraController.StopOrbit();
            TransitionTo(AppState.CameraPreview);
        }

        void OnSavePressed()
        {
            if (_currentState != AppState.Viewing || _lastSketch == null) return;

            string sketchesDir = Path.Combine(Application.persistentDataPath, "Sketches");
            Directory.CreateDirectory(sketchesDir);

            string fileName = $"{_lastSketch.displayName}_{_lastSketch.guid}.sketch";
            string path = Path.Combine(sketchesDir, fileName);
            SketchSerializer.WriteSketchFile(path, _lastSketch);
            Debug.Log($"Sketch saved to: {path}");
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
            // Apply lower threshold on mobile (CPU inference can produce slightly different confidence values)
            bool isMobile = Application.platform == RuntimePlatform.Android || 
                           Application.platform == RuntimePlatform.IPhonePlayer;
            float threshold = isMobile ? detectionThreshold * mobileThresholdMultiplier : detectionThreshold;
            Debug.Log($"Using detection threshold: {threshold} (mobile: {isMobile})");
            
            // Post-processing is CPU-bound; run off the main thread
            var postProcessor = new PostProcessor(threshold);
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

            // Convert to sketch format — this is now the single source of truth
            _lastSketch = SketchConverter.Convert(result, photoCaptureSize);
            _lastSketchJson = SketchSerializer.SerializeJson(_lastSketch);
            Debug.Log($"Sketch JSON ({_lastSketch.entities.Count} entities):\n{_lastSketchJson}");

            // Auto-save sketch file
            string sketchesDir = Path.Combine(Application.persistentDataPath, "Sketches");
            Directory.CreateDirectory(sketchesDir);
            string autoSavePath = Path.Combine(sketchesDir,
                $"{_lastSketch.displayName}_{_lastSketch.guid}.sketch");
            SketchSerializer.WriteSketchFile(autoSavePath, _lastSketch);
            Debug.Log($"Sketch auto-saved to: {autoSavePath}");

            // DEBUG: Visualize wall segment endpoints as spheres
            // Red = unique endpoint, Green = welded (was within weldTolerance of another)
            {
                // Extract wall centerline segments (same as the conversion pipeline does)
                var walls = new System.Collections.Generic.List<PolygonEntry>();
                foreach (var poly in result.Polygons)
                    if (poly.Category == StructureCategory.Wall) walls.Add(poly);

                // Collect all endpoints
                var endpoints = new System.Collections.Generic.List<Vector3>(walls.Count * 2);
                var endpointLabels = new System.Collections.Generic.List<string>(walls.Count * 2);
                for (int i = 0; i < walls.Count; i++)
                {
                    var seg = WallChainBuilder.ExtractCenterline(walls[i], photoCaptureSize);
                    endpoints.Add(seg.Start);
                    endpointLabels.Add($"Seg[{i}]_Start");
                    endpoints.Add(seg.End);
                    endpointLabels.Add($"Seg[{i}]_End");
                }

                // Union-find welding: merge endpoints within weldTolerance
                int epCount = endpoints.Count;
                int[] parent = new int[epCount];
                for (int i = 0; i < epCount; i++) parent[i] = i;

                float weldSq = weldTolerance * weldTolerance;
                for (int i = 0; i < epCount; i++)
                {
                    for (int j = i + 1; j < epCount; j++)
                    {
                        float dx = endpoints[i].x - endpoints[j].x;
                        float dz = endpoints[i].z - endpoints[j].z;
                        if (dx * dx + dz * dz < weldSq)
                        {
                            // Union
                            int ri = i, rj = j;
                            while (parent[ri] != ri) ri = parent[ri];
                            while (parent[rj] != rj) rj = parent[rj];
                            if (ri != rj) parent[ri] = rj;
                        }
                    }
                }

                // Build clusters: root → list of member indices
                var clusters = new System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<int>>();
                for (int i = 0; i < epCount; i++)
                {
                    int root = i;
                    while (parent[root] != root) root = parent[root];
                    if (!clusters.ContainsKey(root))
                        clusters[root] = new System.Collections.Generic.List<int>();
                    clusters[root].Add(i);
                }

                // Compute welded positions (centroid of each cluster)
                var weldedPos = new Vector3[epCount];
                var isWelded = new bool[epCount]; // true if part of a multi-member cluster
                foreach (var kvp in clusters)
                {
                    Vector3 centroid = Vector3.zero;
                    foreach (int idx in kvp.Value) centroid += endpoints[idx];
                    centroid /= kvp.Value.Count;
                    bool multi = kvp.Value.Count > 1;
                    foreach (int idx in kvp.Value)
                    {
                        weldedPos[idx] = centroid;
                        isWelded[idx] = multi;
                    }
                }

                // Create spheres
                float scaleX = worldScale / photoCaptureSize.x;
                float scaleZ = worldScale / photoCaptureSize.y;

                var debugRoot = new GameObject("DebugWallEndpoints");
                _generatedMeshRoot = debugRoot;

                var redMat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                redMat.color = Color.red;
                var greenMat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                greenMat.color = Color.green;

                int weldedCount = 0;
                for (int i = 0; i < epCount; i++)
                {
                    bool welded = isWelded[i];

                    // Skip based on toggle settings
                    if (!showDebugPoints && !welded) continue;
                    if (!showWeldedPoints && welded) continue;

                    var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    sphere.name = endpointLabels[i];
                    sphere.transform.SetParent(debugRoot.transform);
                    sphere.transform.localPosition = weldedPos[i];
                    sphere.transform.localScale = Vector3.one * 0.25f;
                    sphere.GetComponent<Renderer>().sharedMaterial = welded ? greenMat : redMat;
                    Destroy(sphere.GetComponent<Collider>());
                    if (isWelded[i]) weldedCount++;
                }

                // Spline points: one per cluster at the welded/solo position (blue)
                if (showSplinePoints)
                {
                    var blueMat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                    blueMat.color = Color.blue;

                    int splineIdx = 0;
                    foreach (var kvp in clusters)
                    {
                        var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                        sphere.name = $"Spline[{splineIdx}]";
                        sphere.transform.SetParent(debugRoot.transform);
                        sphere.transform.localPosition = weldedPos[kvp.Value[0]];
                        sphere.transform.localScale = Vector3.one * 0.25f;
                        sphere.GetComponent<Renderer>().sharedMaterial = blueMat;
                        Destroy(sphere.GetComponent<Collider>());
                        splineIdx++;
                    }

                    Debug.Log($"[Debug] {splineIdx} spline points (blue)");
                }

                debugRoot.transform.localScale = new Vector3(scaleX, 1f, scaleZ);

                Debug.Log($"[Debug] {epCount} endpoints, {clusters.Count} unique positions " +
                          $"({weldedCount} welded, {epCount - weldedCount} solo) " +
                          $"[weldTolerance={weldTolerance:F3}m]");

                TransitionTo(AppState.Viewing);
            }

            /* COMMENTED OUT: Normal mesh building from sketch data
            var meshBuilder = new FloorplanMeshBuilder(wallMaterial, doorMaterial, windowMaterial);

            StartCoroutine(meshBuilder.BuildFromSketchAsync(_lastSketch, meshesPerFrame, (root, bounds) =>
            {
                _generatedMeshRoot = root;

                float scaleX = worldScale / photoCaptureSize.x;
                float scaleZ = worldScale / photoCaptureSize.y;

                _generatedMeshRoot.transform.localScale = new Vector3(scaleX, 0f, scaleZ);

                var worldBounds = new Bounds(
                    new Vector3(bounds.center.x * scaleX, bounds.center.y, bounds.center.z * scaleZ),
                    new Vector3(bounds.size.x * scaleX, bounds.size.y, bounds.size.z * scaleZ)
                );

                TransitionTo(AppState.CameraTransition);
                cameraController.LerpToPerspective(worldBounds.center, worldBounds, () =>
                {
                    StartCoroutine(AnimateMeshScaleUp(() => TransitionTo(AppState.Viewing)));
                });
            }));
            */
        }

        // --- Helpers ---

        IEnumerator AnimateMeshScaleUp(Action onComplete)
        {
            if (_generatedMeshRoot == null)
            {
                onComplete?.Invoke();
                yield break;
            }

            float scaleX = worldScale / photoCaptureSize.x;
            float scaleZ = worldScale / photoCaptureSize.y;

            float elapsed = 0f;
            while (elapsed < meshScaleUpDuration)
            {
                float t = Mathf.SmoothStep(0f, 1f, elapsed / meshScaleUpDuration);
                _generatedMeshRoot.transform.localScale = new Vector3(scaleX, t, scaleZ);
                elapsed += Time.deltaTime;
                yield return null;
            }

            _generatedMeshRoot.transform.localScale = new Vector3(scaleX, 1f, scaleZ);
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
