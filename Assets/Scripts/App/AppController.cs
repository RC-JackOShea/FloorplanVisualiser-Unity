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
using TMPro;
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
        [SerializeField] private bool showIntersectionPoints = true;
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
        GameObject _debugRoot;
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
                    if (_debugRoot != null)
                    {
                        Destroy(_debugRoot);
                        _debugRoot = null;
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

            // DEBUG: Visualize junctions, connections, and intersection points
            // Uses RoomOutlineExtractor as single source of truth (no duplicate union-find)
            {
                // Extract wall centerline segments (same as the conversion pipeline does)
                var walls = new System.Collections.Generic.List<PolygonEntry>();
                foreach (var poly in result.Polygons)
                    if (poly.Category == StructureCategory.Wall) walls.Add(poly);

                var wallSegments = new System.Collections.Generic.List<WallChainBuilder.WallSegment>(walls.Count);
                for (int i = 0; i < walls.Count; i++)
                    wallSegments.Add(WallChainBuilder.ExtractCenterline(walls[i], photoCaptureSize));

                // Run extraction — this is now the single source of truth for junctions & connections
                var extraction = RoomOutlineExtractor.Extract(wallSegments, weldTolerance);

                float scaleX = worldScale / photoCaptureSize.x;
                float scaleZ = worldScale / photoCaptureSize.y;

                var debugRoot = new GameObject("DebugWallEndpoints");
                _debugRoot = debugRoot;

                // Junction points (blue spheres with J[n] labels — IDs match extraction data)
                if (showSplinePoints)
                {
                    var blueMat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                    blueMat.color = Color.blue;

                    foreach (var junction in extraction.Junctions)
                    {
                        var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                        sphere.name = $"J[{junction.Id}]";
                        sphere.transform.SetParent(debugRoot.transform);
                        sphere.transform.localPosition = junction.Position;
                        sphere.transform.localScale = Vector3.one * 0.25f;
                        sphere.GetComponent<Renderer>().sharedMaterial = blueMat;
                        Destroy(sphere.GetComponent<Collider>());

                        CreateDebugLabel($"J{junction.Id}", junction.Position, debugRoot.transform, Color.blue);
                    }

                    Debug.Log($"[Debug] {extraction.Junctions.Count} junction points (blue)");
                }

                // Connection lines (green = interior, red = outer boundary)
                {
                    var connMat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                    connMat.color = Color.green;
                    var outerMat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                    outerMat.color = Color.red;

                    const float buffer = 0.15f; // pull line ends away from junction centres

                    // Build a quick lookup from junction ID to position
                    var junctionPos = new System.Collections.Generic.Dictionary<int, Vector3>();
                    foreach (var j in extraction.Junctions)
                        junctionPos[j.Id] = j.Position;

                    int outerCount = 0;
                    foreach (var conn in extraction.Connections)
                    {
                        if (!junctionPos.TryGetValue(conn.JunctionA, out var posA)) continue;
                        if (!junctionPos.TryGetValue(conn.JunctionB, out var posB)) continue;

                        bool isOuter = extraction.OuterBoundaryConnectionIds.Contains(conn.Id);
                        if (isOuter) outerCount++;

                        // Shorten the line by 'buffer' at each end so it doesn't overlap the spheres
                        Vector3 dir = (posB - posA).normalized;
                        Vector3 lineStart = posA + dir * buffer;
                        Vector3 lineEnd = posB - dir * buffer;

                        var lineObj = new GameObject($"C[{conn.Id}] J{conn.JunctionA}-J{conn.JunctionB}{(isOuter ? " [OUTER]" : "")}");
                        lineObj.transform.SetParent(debugRoot.transform);

                        var lr = lineObj.AddComponent<LineRenderer>();
                        lr.useWorldSpace = false;
                        lr.positionCount = 2;
                        lr.SetPosition(0, lineStart);
                        lr.SetPosition(1, lineEnd);
                        lr.startWidth = 0.06f;
                        lr.endWidth = 0.06f;
                        lr.material = isOuter ? outerMat : connMat;

                        // Label at midpoint
                        Vector3 mid = (posA + posB) * 0.5f;
                        Color labelColor = isOuter ? Color.red : Color.green;
                        CreateDebugLabel($"C{conn.Id}", mid, debugRoot.transform, labelColor, 2f);
                    }

                    Debug.Log($"[Debug] {extraction.Connections.Count} connections ({outerCount} outer/red, {extraction.Connections.Count - outerCount} interior/green)");
                }

                // Intersection points (yellow spheres with IX[n] labels)
                if (showIntersectionPoints && extraction.Intersections.Count > 0)
                {
                    var yellowMat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                    yellowMat.color = Color.yellow;

                    for (int i = 0; i < extraction.Intersections.Count; i++)
                    {
                        var ix = extraction.Intersections[i];
                        var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                        sphere.name = $"IX[{i}] (Seg{ix.SegmentA} x Seg{ix.SegmentB})";
                        sphere.transform.SetParent(debugRoot.transform);
                        sphere.transform.localPosition = ix.Position;
                        sphere.transform.localScale = Vector3.one * 0.3f;
                        sphere.GetComponent<Renderer>().sharedMaterial = yellowMat;
                        Destroy(sphere.GetComponent<Collider>());

                        CreateDebugLabel($"IX{i}", ix.Position, debugRoot.transform, Color.yellow);
                    }

                    Debug.Log($"[Debug] {extraction.Intersections.Count} intersection points (yellow)");
                }

                debugRoot.transform.localScale = new Vector3(scaleX, 1f, scaleZ);

                Debug.Log($"[Debug] {extraction.Junctions.Count} junctions, " +
                          $"{extraction.Connections.Count} connections, " +
                          $"{extraction.Intersections.Count} intersections " +
                          $"[weldTolerance={weldTolerance:F3}m]");
            }

            // Mesh generation disabled — focusing on spline/connection validation
            // var meshBuilder = new FloorplanMeshBuilder(wallMaterial, doorMaterial, windowMaterial);
            // StartCoroutine(meshBuilder.BuildFromSketchAsync(_lastSketch, meshesPerFrame, (root, bounds) =>
            // {
            //     _generatedMeshRoot = root;
            //     float scaleX2 = worldScale / photoCaptureSize.x;
            //     float scaleZ2 = worldScale / photoCaptureSize.y;
            //     _generatedMeshRoot.transform.localScale = new Vector3(scaleX2, 0f, scaleZ2);
            //     var worldBounds = new Bounds(
            //         new Vector3(bounds.center.x * scaleX2, bounds.center.y, bounds.center.z * scaleZ2),
            //         new Vector3(bounds.size.x * scaleX2, bounds.size.y, bounds.size.z * scaleZ2));
            //     TransitionTo(AppState.CameraTransition);
            //     cameraController.LerpToPerspective(worldBounds.center, worldBounds, () =>
            //     {
            //         StartCoroutine(AnimateMeshScaleUp(() => TransitionTo(AppState.Viewing)));
            //     });
            // }));

            // Transition directly to viewing with debug points visible
            TransitionTo(AppState.CameraTransition);
            var debugBounds = _debugRoot != null
                ? new Bounds(_debugRoot.transform.position, Vector3.one * worldScale)
                : new Bounds(Vector3.zero, Vector3.one * worldScale);
            cameraController.LerpToPerspective(debugBounds.center, debugBounds, () =>
            {
                TransitionTo(AppState.Viewing);
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

        /// <summary>
        /// Create a world-space TextMeshPro label above a debug sphere.
        /// The label faces the camera via the BillboardLabel component.
        /// </summary>
        static GameObject CreateDebugLabel(string text, Vector3 localPos, Transform parent, Color color, float fontSize = 3f)
        {
            var labelObj = new GameObject($"Label_{text}");
            labelObj.transform.SetParent(parent);
            labelObj.transform.localPosition = localPos + Vector3.up * 0.25f;

            var tmp = labelObj.AddComponent<TextMeshPro>();
            tmp.text = text;
            tmp.fontSize = fontSize;
            tmp.color = color;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.enableWordWrapping = false;

            var rt = labelObj.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(2f, 1f);

            labelObj.AddComponent<BillboardLabel>();

            return labelObj;
        }
    }
}
