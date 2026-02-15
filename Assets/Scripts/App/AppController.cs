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
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
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
        [Tooltip("Base unlit material cloned for debug visuals. Create a URP Unlit material and assign it here.")]
        [SerializeField] private Material unlitBaseMaterial;

        [Header("Mesh Settings")]
        [SerializeField] private float worldScale = 10f;
        [SerializeField] private float extrudeHeight = 2f;
        [Tooltip("Number of meshes to create per frame during generation (lower = less GPU pressure on mobile)")]
        [SerializeField] private int meshesPerFrame = 4;

        [Header("Mesh Animation")]
        [SerializeField] private float meshScaleUpDuration = 0.6f;

        [Header("Reveal Animation")]
        [Tooltip("How long each element takes to scale from 0 to full size")]
        [SerializeField] private float revealPopDuration = 0.15f;
        [Tooltip("Delay between each element starting its pop-in (lower = more overlap)")]
        [SerializeField] private float revealStaggerDelay = 0.05f;

        [Header("Post-Processing")]
        [Tooltip("Confidence threshold for wall/door/window detection (lower = more detections, may include false positives)")]
        [SerializeField] private float detectionThreshold = 0.5f;
        [Tooltip("Use lower threshold on mobile due to CPU inference differences")]
        [SerializeField] private float mobileThresholdMultiplier = 0.7f;

        [Header("Sketch Export")]
        [Tooltip("Photo capture size in metres — maps normalized [0,1] coordinates to world scale")]
        [SerializeField] private Vector2 photoCaptureSize = new Vector2(7f, 7f);
        [Tooltip("Scale factor applied to all sketch positions/thicknesses before export (e.g. 2.0 = double size)")]
        [SerializeField] private float sketchScale = 1f;

        [Header("Debug Visualisation")]
        [SerializeField] private bool showDebugPoints = true;
        [SerializeField] private bool showWeldedPoints = true;
        [SerializeField] private bool showSplinePoints = true;
        [SerializeField] private bool showIntersectionPoints = true;
        [Tooltip("When false, text labels are not created for debug spheres and connection lines are not revealed before walls")]
        [SerializeField] private bool showDebugLabels = true;
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

        [Header("UI - Viewing Toggles")]
        [SerializeField] private GameObject viewingTogglesUI;
        [SerializeField] private Button outerWallsButton;
        [SerializeField] private Button innerWallsButton;
        [SerializeField] private Button splinePointsButton;
        [SerializeField] private Button doorsWindowsButton;
        [SerializeField] private Button previewPlaneButton;

        [Header("UI - Wall Measurement")]
        [SerializeField] private GameObject measurementUI;
        [SerializeField] private TMP_InputField wallLengthInput;
        [SerializeField] private Button applyMeasurementButton;

        AppState _currentState;
        GameObject _generatedMeshRoot;
        GameObject _debugRoot;
        SketchFile _lastSketch;
        static Mesh _sharedSphereMesh;
        string _lastSketchJson;

        // Category containers for toggle visibility
        GameObject _outerWallsContainer;
        GameObject _innerWallsContainer;
        GameObject _junctionsContainer;
        GameObject _doorsWindowsContainer;

        // Toggle state tracking
        bool _outerWallsVisible = true;
        bool _innerWallsVisible = true;
        bool _splinePointsVisible = true;
        bool _doorsWindowsVisible = true;
        bool _previewPlaneVisible = true;

        static readonly Color ToggleOnColor = new Color(0.2f, 0.8f, 0.2f);
        static readonly Color ToggleOffColor = new Color(0.8f, 0.2f, 0.2f);

        // Reveal animation lists (populated during creation, animated on Viewing enter)
        readonly System.Collections.Generic.List<(Transform obj, float targetScale)> _junctionReveals = new();
        readonly System.Collections.Generic.List<(Transform obj, float targetScale)> _doorReveals = new();
        readonly System.Collections.Generic.List<(Transform obj, float targetScale)> _windowReveals = new();
        readonly System.Collections.Generic.List<(LineRenderer lr, Vector3 start, Vector3 end)> _connectionLineReveals = new();

        // Wall selection state
        GameObject _selectedWall;
        Material _selectedWallOriginalMaterial;
        Material _highlightMaterial;
        Vector2 _pointerDownPos;
        const float TapThreshold = 10f; // pixels

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

            // Viewing toggle buttons
            if (outerWallsButton != null) outerWallsButton.onClick.AddListener(() => ToggleCategory(ref _outerWallsVisible, _outerWallsContainer, outerWallsButton));
            if (innerWallsButton != null) innerWallsButton.onClick.AddListener(() => ToggleCategory(ref _innerWallsVisible, _innerWallsContainer, innerWallsButton));
            if (splinePointsButton != null) splinePointsButton.onClick.AddListener(() => ToggleCategory(ref _splinePointsVisible, _junctionsContainer, splinePointsButton));
            if (doorsWindowsButton != null) doorsWindowsButton.onClick.AddListener(() => ToggleCategory(ref _doorsWindowsVisible, _doorsWindowsContainer, doorsWindowsButton));
            if (previewPlaneButton != null) previewPlaneButton.onClick.AddListener(TogglePreviewPlane);

            // Wall measurement
            if (applyMeasurementButton != null) applyMeasurementButton.onClick.AddListener(OnApplyMeasurement);
            SetActive(measurementUI, false);

            // Highlight material (created once)
            _highlightMaterial = new Material(unlitBaseMaterial);
            _highlightMaterial.SetColor("_BaseColor", new Color(0f, 1f, 1f)); // cyan

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
            SetActive(measurementUI, false);
            DeselectWall();

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
                    _outerWallsContainer = null;
                    _innerWallsContainer = null;
                    _junctionsContainer = null;
                    _doorsWindowsContainer = null;
                    _outerWallsVisible = true;
                    _innerWallsVisible = true;
                    _splinePointsVisible = true;
                    _doorsWindowsVisible = true;
                    _previewPlaneVisible = true;
                    _junctionReveals.Clear();
                    _doorReveals.Clear();
                    _windowReveals.Clear();
                    _connectionLineReveals.Clear();
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
                    // Hide walls (Y=0) until camera transition completes
                    if (_generatedMeshRoot != null)
                    {
                        var s = _generatedMeshRoot.transform.localScale;
                        _generatedMeshRoot.transform.localScale = new Vector3(s.x, 0f, s.z);
                    }
                    break;

                case AppState.Viewing:
                    // Orbit is enabled by CameraController after transition
                    // Preview plane was faded to 10% during camera transition
                    _previewPlaneVisible = false;
                    SetActive(viewingUI, true);
                    UpdateToggleButtonColor(outerWallsButton, _outerWallsVisible);
                    UpdateToggleButtonColor(innerWallsButton, _innerWallsVisible);
                    UpdateToggleButtonColor(splinePointsButton, _splinePointsVisible);
                    UpdateToggleButtonColor(doorsWindowsButton, _doorsWindowsVisible);
                    UpdateToggleButtonColor(previewPlaneButton, _previewPlaneVisible);
                    // Sequenced reveal: points → lines → doors → windows → walls
                    StartCoroutine(RevealSequence());
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
            SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
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

        // --- Wall Selection & Measurement ---

        void Update()
        {
            if (_currentState != AppState.Viewing) return;

            // Detect tap (not drag) for wall selection using new Input System
            // Touch input
            if (Touchscreen.current != null)
            {
                if (Touchscreen.current.primaryTouch.press.wasPressedThisFrame)
                    _pointerDownPos = Touchscreen.current.primaryTouch.position.ReadValue();

                if (Touchscreen.current.primaryTouch.press.wasReleasedThisFrame)
                {
                    Vector2 pointerUpPos = Touchscreen.current.primaryTouch.position.ReadValue();
                    float dragDist = Vector2.Distance(_pointerDownPos, pointerUpPos);
                    if (dragDist < TapThreshold)
                        TrySelectWall(pointerUpPos);
                }
            }
            // Mouse input
            else if (Mouse.current != null)
            {
                if (Mouse.current.leftButton.wasPressedThisFrame)
                    _pointerDownPos = Mouse.current.position.ReadValue();

                if (Mouse.current.leftButton.wasReleasedThisFrame)
                {
                    Vector2 pointerUpPos = Mouse.current.position.ReadValue();
                    float dragDist = Vector2.Distance(_pointerDownPos, pointerUpPos);
                    if (dragDist < TapThreshold)
                        TrySelectWall(pointerUpPos);
                }
            }
        }

        void TrySelectWall(Vector2 screenPos)
        {
            var cam = cameraController.GetComponent<Camera>();
            if (cam == null) return;

            Ray ray = cam.ScreenPointToRay(screenPos);
            if (Physics.Raycast(ray, out RaycastHit hit))
            {
                var info = hit.collider.GetComponent<WallInfo>();
                if (info != null)
                {
                    SelectWall(hit.collider.gameObject);
                    return;
                }
            }

            DeselectWall();
        }

        void SelectWall(GameObject wall)
        {
            // Deselect previous if different
            if (_selectedWall != null && _selectedWall != wall)
                DeselectWall();

            if (_selectedWall == wall) return;

            _selectedWall = wall;
            var renderer = wall.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                _selectedWallOriginalMaterial = renderer.material;
                renderer.material = _highlightMaterial;
            }

            // Hide viewing toggles, show measurement UI
            SetActive(viewingTogglesUI, false);
            SetActive(measurementUI, true);
            if (wallLengthInput != null)
            {
                // Pre-fill with current wall length based on existing scale
                var info = wall.GetComponent<WallInfo>();
                if (info != null)
                {
                    float scaleX = _generatedMeshRoot != null ? _generatedMeshRoot.transform.localScale.x : 1f;
                    float currentLength = info.LocalLength * scaleX;
                    wallLengthInput.text = currentLength.ToString("F2");
                }
                wallLengthInput.ActivateInputField();
            }
        }

        void DeselectWall()
        {
            if (_selectedWall != null)
            {
                var renderer = _selectedWall.GetComponent<MeshRenderer>();
                if (renderer != null && _selectedWallOriginalMaterial != null)
                    renderer.material = _selectedWallOriginalMaterial;
                _selectedWall = null;
                _selectedWallOriginalMaterial = null;
            }
            SetActive(measurementUI, false);
            SetActive(viewingTogglesUI, true);
        }

        void OnApplyMeasurement()
        {
            if (_selectedWall == null) return;
            if (wallLengthInput == null) return;
            if (!float.TryParse(wallLengthInput.text, out float userLength) || userLength <= 0f) return;

            var info = _selectedWall.GetComponent<WallInfo>();
            if (info == null) return;

            float localLength = info.LocalLength;
            if (localLength < 0.001f) return;

            // Compute new worldScale so that this wall's world length equals userLength
            worldScale = (userLength / localLength) * photoCaptureSize.x;

            float scaleX = worldScale / photoCaptureSize.x;
            float scaleZ = worldScale / photoCaptureSize.y;

            if (_generatedMeshRoot != null)
                _generatedMeshRoot.transform.localScale = new Vector3(scaleX, 1f, scaleZ);
            if (_debugRoot != null)
                _debugRoot.transform.localScale = new Vector3(scaleX, 1f, scaleZ);

            imageCapture.UpdateWorldScale(worldScale);

            Debug.Log($"[Measurement] Wall local length: {localLength:F3}m, " +
                      $"user length: {userLength:F2}m, new worldScale: {worldScale:F2}");

            DeselectWall();
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
            _lastSketch = SketchConverter.Convert(result, photoCaptureSize, sketchScale);
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
                var doorPositions = new System.Collections.Generic.List<Vector3>();
                var windowPositions = new System.Collections.Generic.List<Vector3>();

                foreach (var poly in result.Polygons)
                {
                    if (poly.Category == StructureCategory.Wall)
                    {
                        walls.Add(poly);
                    }
                    else
                    {
                        // Compute center of the 4-vertex polygon in world space (centered around origin)
                        float halfW = photoCaptureSize.x * 0.5f;
                        float halfH = photoCaptureSize.y * 0.5f;
                        Vector3 center = Vector3.zero;
                        for (int i = 0; i < poly.Vertices.Length; i++)
                        {
                            center += new Vector3(
                                poly.Vertices[i].x * photoCaptureSize.x - halfW,
                                0f,
                                (1f - poly.Vertices[i].y) * photoCaptureSize.y - halfH);
                        }
                        center /= poly.Vertices.Length;

                        if (poly.Category == StructureCategory.Door)
                            doorPositions.Add(center);
                        else if (poly.Category == StructureCategory.Window)
                            windowPositions.Add(center);
                    }
                }

                var wallSegments = new System.Collections.Generic.List<WallChainBuilder.WallSegment>(walls.Count);
                for (int i = 0; i < walls.Count; i++)
                    wallSegments.Add(WallChainBuilder.ExtractCenterline(walls[i], photoCaptureSize));

                // Run extraction — this is now the single source of truth for junctions & connections
                var extraction = RoomOutlineExtractor.Extract(wallSegments, weldTolerance);

                float scaleX = worldScale / photoCaptureSize.x;
                float scaleZ = worldScale / photoCaptureSize.y;

                var debugRoot = new GameObject("DebugWallEndpoints");
                _debugRoot = debugRoot;

                // Category containers for toggle visibility
                var junctionsContainer = new GameObject("Junctions");
                junctionsContainer.transform.SetParent(debugRoot.transform);
                _junctionsContainer = junctionsContainer;

                var doorsWindowsContainer = new GameObject("DoorsWindows");
                doorsWindowsContainer.transform.SetParent(debugRoot.transform);
                _doorsWindowsContainer = doorsWindowsContainer;

                // Build a quick lookup from junction ID to position (used by debug vis + mesh gen)
                var junctionPos = new System.Collections.Generic.Dictionary<int, Vector3>();
                foreach (var j in extraction.Junctions)
                    junctionPos[j.Id] = j.Position;

                // Junction points (blue spheres with J[n] labels — IDs match extraction data)
                if (showSplinePoints)
                {
                    var blueMat = new Material(unlitBaseMaterial);
                    blueMat.SetColor("_BaseColor", Color.blue);

                    foreach (var junction in extraction.Junctions)
                    {
                        var sphere = CreateDebugSphere($"J[{junction.Id}]", blueMat);
                        sphere.transform.SetParent(junctionsContainer.transform);
                        sphere.transform.localPosition = junction.Position;
                        sphere.transform.localScale = Vector3.zero;
                        _junctionReveals.Add((sphere.transform, 0.25f));

                        if (showDebugLabels)
                        {
                            var label = CreateDebugLabel($"J{junction.Id}", junction.Position, junctionsContainer.transform, Color.blue);
                            label.transform.localScale = Vector3.zero;
                            _junctionReveals.Add((label.transform, 1f));
                        }
                    }

                    Debug.Log($"[Debug] {extraction.Junctions.Count} junction points (blue)");
                }

                // Connection lines (green = interior, red = outer boundary)
                {
                    var connMat = new Material(unlitBaseMaterial);
                    connMat.SetColor("_BaseColor", Color.green);
                    var outerMat = new Material(unlitBaseMaterial);
                    outerMat.SetColor("_BaseColor", Color.red);

                    const float buffer = 0.15f; // pull line ends away from junction centres

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
                        lineObj.transform.SetParent(junctionsContainer.transform);

                        var lr = lineObj.AddComponent<LineRenderer>();
                        lr.useWorldSpace = false;
                        lr.positionCount = 2;
                        lr.SetPosition(0, lineStart);
                        lr.SetPosition(1, lineStart); // start collapsed — will be drawn during reveal
                        lr.startWidth = 0.12f;
                        lr.endWidth = 0.12f;
                        lr.material = isOuter ? outerMat : connMat;
                        _connectionLineReveals.Add((lr, lineStart, lineEnd));

                        // Label at midpoint
                        if (showDebugLabels)
                        {
                            Vector3 mid = (posA + posB) * 0.5f;
                            Color labelColor = isOuter ? Color.red : Color.green;
                            var connLabel = CreateDebugLabel($"C{conn.Id}", mid, junctionsContainer.transform, labelColor, 2f);
                            connLabel.transform.localScale = Vector3.zero;
                            _junctionReveals.Add((connLabel.transform, 1f));
                        }
                    }

                    Debug.Log($"[Debug] {extraction.Connections.Count} connections ({outerCount} outer/red, {extraction.Connections.Count - outerCount} interior/green)");
                }

                // Intersection points (yellow spheres with IX[n] labels)
                if (showIntersectionPoints && extraction.Intersections.Count > 0)
                {
                    var yellowMat = new Material(unlitBaseMaterial);
                    yellowMat.SetColor("_BaseColor", Color.yellow);

                    for (int i = 0; i < extraction.Intersections.Count; i++)
                    {
                        var ix = extraction.Intersections[i];
                        var sphere = CreateDebugSphere($"IX[{i}] (Seg{ix.SegmentA} x Seg{ix.SegmentB})", yellowMat);
                        sphere.transform.SetParent(junctionsContainer.transform);
                        sphere.transform.localPosition = ix.Position;
                        sphere.transform.localScale = Vector3.zero;
                        _junctionReveals.Add((sphere.transform, 0.3f));

                        if (showDebugLabels)
                        {
                            var ixLabel = CreateDebugLabel($"IX{i}", ix.Position, junctionsContainer.transform, Color.yellow);
                            ixLabel.transform.localScale = Vector3.zero;
                            _junctionReveals.Add((ixLabel.transform, 1f));
                        }
                    }

                    Debug.Log($"[Debug] {extraction.Intersections.Count} intersection points (yellow)");
                }

                // Door positions (yellow spheres)
                if (doorPositions.Count > 0)
                {
                    var doorMat = new Material(unlitBaseMaterial);
                    doorMat.SetColor("_BaseColor", Color.yellow);

                    for (int i = 0; i < doorPositions.Count; i++)
                    {
                        var sphere = CreateDebugSphere($"Door[{i}]", doorMat);
                        sphere.transform.SetParent(doorsWindowsContainer.transform);
                        sphere.transform.localPosition = doorPositions[i];
                        sphere.transform.localScale = Vector3.zero;
                        _doorReveals.Add((sphere.transform, 0.2f));

                        if (showDebugLabels)
                        {
                            var doorLabel = CreateDebugLabel($"D{i}", doorPositions[i], doorsWindowsContainer.transform, Color.yellow);
                            doorLabel.transform.localScale = Vector3.zero;
                            _doorReveals.Add((doorLabel.transform, 1f));
                        }
                    }
                }

                // Window positions (purple spheres)
                if (windowPositions.Count > 0)
                {
                    var winMat = new Material(unlitBaseMaterial);
                    winMat.SetColor("_BaseColor", Color.green);

                    for (int i = 0; i < windowPositions.Count; i++)
                    {
                        var sphere = CreateDebugSphere($"Window[{i}]", winMat);
                        sphere.transform.SetParent(doorsWindowsContainer.transform);
                        sphere.transform.localPosition = windowPositions[i];
                        sphere.transform.localScale = Vector3.zero;
                        _windowReveals.Add((sphere.transform, 0.2f));

                        if (showDebugLabels)
                        {
                            var winLabel = CreateDebugLabel($"W{i}", windowPositions[i], doorsWindowsContainer.transform, Color.magenta);
                            winLabel.transform.localScale = Vector3.zero;
                            _windowReveals.Add((winLabel.transform, 1f));
                        }
                    }
                }

                Debug.Log($"[Debug] {doorPositions.Count} doors (yellow), {windowPositions.Count} windows (purple)");

                debugRoot.transform.localScale = new Vector3(scaleX, 1f, scaleZ);

                Debug.Log($"[Debug] {extraction.Junctions.Count} junctions, " +
                          $"{extraction.Connections.Count} connections, " +
                          $"{extraction.Intersections.Count} intersections " +
                          $"[weldTolerance={weldTolerance:F3}m]");

                // Generate wall meshes
                var meshRoot = new GameObject("WallMeshes");
                _generatedMeshRoot = meshRoot;

                var outerWallsContainer = new GameObject("OuterWalls");
                outerWallsContainer.transform.SetParent(meshRoot.transform);
                _outerWallsContainer = outerWallsContainer;

                var innerWallsContainer = new GameObject("InnerWalls");
                innerWallsContainer.transform.SetParent(meshRoot.transform);
                _innerWallsContainer = innerWallsContainer;

                var doorMeshContainer = new GameObject("DoorMeshes");
                doorMeshContainer.transform.SetParent(meshRoot.transform);

                var windowMeshContainer = new GameObject("WindowMeshes");
                windowMeshContainer.transform.SetParent(meshRoot.transform);

                foreach (var conn in extraction.Connections)
                {
                    if (!extraction.OuterBoundaryConnectionIds.Contains(conn.Id)) continue;
                    if (!junctionPos.TryGetValue(conn.JunctionA, out var posA2)) continue;
                    if (!junctionPos.TryGetValue(conn.JunctionB, out var posB2)) continue;

                    float halfThick = Mathf.Max(conn.Thickness * 0.5f, 0.05f);
                    Vector3 dir = (posB2 - posA2).normalized;
                    Vector3 perp = new Vector3(-dir.z, 0f, dir.x);

                    Vector3[] bottom = {
                        posA2 - perp * halfThick,
                        posA2 + perp * halfThick,
                        posB2 + perp * halfThick,
                        posB2 - perp * halfThick
                    };
                    Vector3[] top = new Vector3[4];
                    for (int i = 0; i < 4; i++)
                        top[i] = bottom[i] + Vector3.up * extrudeHeight;

                    var wallObj = new GameObject($"OuterWall_C{conn.Id}");
                    wallObj.transform.SetParent(outerWallsContainer.transform);
                    wallObj.AddComponent<MeshFilter>().mesh = BuildBoxMesh(bottom, top);
                    wallObj.AddComponent<MeshRenderer>().material = wallMaterial;
                    wallObj.AddComponent<MeshCollider>();
                    var outerInfo = wallObj.AddComponent<WallInfo>();
                    outerInfo.endpointA = posA2;
                    outerInfo.endpointB = posB2;
                }

                Debug.Log($"[Mesh] Generated outer wall mesh ({extraction.OuterBoundaryConnectionIds.Count} panels)");

                // Generate interior wall meshes from non-outer connections, chaining collinear runs
                {
                    // 1. Build interior-only adjacency: junction → list of (neighbor, connectionId)
                    var interiorAdj = new System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<(int neighbor, int connId)>>();
                    var interiorConns = new System.Collections.Generic.Dictionary<int, RoomOutlineExtractor.Connection>();

                    foreach (var conn in extraction.Connections)
                    {
                        if (extraction.OuterBoundaryConnectionIds.Contains(conn.Id)) continue;
                        interiorConns[conn.Id] = conn;

                        if (!interiorAdj.ContainsKey(conn.JunctionA))
                            interiorAdj[conn.JunctionA] = new System.Collections.Generic.List<(int, int)>();
                        if (!interiorAdj.ContainsKey(conn.JunctionB))
                            interiorAdj[conn.JunctionB] = new System.Collections.Generic.List<(int, int)>();

                        interiorAdj[conn.JunctionA].Add((conn.JunctionB, conn.Id));
                        interiorAdj[conn.JunctionB].Add((conn.JunctionA, conn.Id));
                    }

                    // 2. Walk chains: merge collinear connections through degree-2 interior junctions
                    var usedConns = new System.Collections.Generic.HashSet<int>();
                    var chains = new System.Collections.Generic.List<(System.Collections.Generic.List<int> junctions, float thickness)>();

                    foreach (var conn in interiorConns.Values)
                    {
                        if (usedConns.Contains(conn.Id)) continue;

                        // Start a chain from this connection
                        var chain = new System.Collections.Generic.List<int> { conn.JunctionA, conn.JunctionB };
                        float totalThickness = conn.Thickness;
                        int thicknessCount = 1;
                        usedConns.Add(conn.Id);

                        // Determine axis: vertical (same X) or horizontal (same Z)
                        Vector3 pA = junctionPos[conn.JunctionA];
                        Vector3 pB = junctionPos[conn.JunctionB];
                        bool isVertical = Mathf.Abs(pA.x - pB.x) < Mathf.Abs(pA.z - pB.z);

                        // Extend chain forward (from last junction)
                        ExtendChain(chain, isVertical, junctionPos, interiorAdj, usedConns,
                            interiorConns, ref totalThickness, ref thicknessCount, forward: true);

                        // Extend chain backward (from first junction)
                        ExtendChain(chain, isVertical, junctionPos, interiorAdj, usedConns,
                            interiorConns, ref totalThickness, ref thicknessCount, forward: false);

                        chains.Add((chain, totalThickness / thicknessCount));
                    }

                    // 3. Generate wall panel per chain
                    int interiorCount = 0;
                    foreach (var (chain, avgThickness) in chains)
                    {
                        Vector3 start = junctionPos[chain[0]];
                        Vector3 end = junctionPos[chain[chain.Count - 1]];

                        float halfThick = Mathf.Max(avgThickness * 0.5f, 0.05f);
                        Vector3 dir = (end - start).normalized;
                        Vector3 perp = new Vector3(-dir.z, 0f, dir.x);

                        Vector3[] bottom = {
                            start - perp * halfThick,
                            start + perp * halfThick,
                            end + perp * halfThick,
                            end - perp * halfThick
                        };
                        Vector3[] top = new Vector3[4];
                        for (int i = 0; i < 4; i++)
                            top[i] = bottom[i] + Vector3.up * extrudeHeight;

                        string label = chain.Count == 2
                            ? $"InnerWall_J{chain[0]}-J{chain[1]}"
                            : $"InnerWall_J{chain[0]}-...-J{chain[chain.Count - 1]}({chain.Count}pts)";
                        var wallObj = new GameObject(label);
                        wallObj.transform.SetParent(innerWallsContainer.transform);
                        wallObj.AddComponent<MeshFilter>().mesh = BuildBoxMesh(bottom, top);
                        wallObj.AddComponent<MeshRenderer>().material = wallMaterial;
                        wallObj.AddComponent<MeshCollider>();
                        var innerInfo = wallObj.AddComponent<WallInfo>();
                        innerInfo.endpointA = start;
                        innerInfo.endpointB = end;
                        interiorCount++;
                    }

                    Debug.Log($"[Mesh] Generated {interiorCount} interior wall panels " +
                              $"(from {interiorConns.Count} connections, merged into {chains.Count} chains)");
                }

                // Generate door and window meshes oriented along nearest wall
                {
                    // Build list of wall segments (start, end) from all connections for nearest-wall lookup
                    var wallLines = new System.Collections.Generic.List<(Vector3 a, Vector3 b, float thickness)>();
                    foreach (var conn in extraction.Connections)
                    {
                        if (!junctionPos.TryGetValue(conn.JunctionA, out var a)) continue;
                        if (!junctionPos.TryGetValue(conn.JunctionB, out var b)) continue;
                        wallLines.Add((a, b, conn.Thickness));
                    }

                    // Door meshes
                    float doorWidth = 0.4f;
                    float doorHeight = extrudeHeight * 0.85f;
                    for (int i = 0; i < doorPositions.Count; i++)
                    {
                        var pos = doorPositions[i];
                        FindNearestWall(pos, wallLines, out Vector3 wallDir, out float wallThick);
                        Vector3 perp = new Vector3(-wallDir.z, 0f, wallDir.x);
                        float halfW = doorWidth * 0.5f;
                        float halfT = Mathf.Max(wallThick * 0.5f, 0.05f) + 0.01f; // slightly thicker than wall

                        // Width along wallDir, thickness along perp (same pattern as wall meshes)
                        Vector3[] bottom = {
                            pos - wallDir * halfW - perp * halfT,
                            pos - wallDir * halfW + perp * halfT,
                            pos + wallDir * halfW + perp * halfT,
                            pos + wallDir * halfW - perp * halfT
                        };
                        Vector3[] top = new Vector3[4];
                        for (int j = 0; j < 4; j++)
                            top[j] = bottom[j] + Vector3.up * doorHeight;

                        var doorObj = new GameObject($"DoorMesh[{i}]");
                        doorObj.transform.SetParent(doorMeshContainer.transform);
                        doorObj.AddComponent<MeshFilter>().mesh = BuildBoxMesh(bottom, top);
                        doorObj.AddComponent<MeshRenderer>().material = doorMaterial;
                    }

                    // Window meshes
                    float windowWidth = 0.35f;
                    float windowBottom = extrudeHeight * 0.35f;
                    float windowTop = extrudeHeight * 0.75f;
                    for (int i = 0; i < windowPositions.Count; i++)
                    {
                        var pos = windowPositions[i];
                        FindNearestWall(pos, wallLines, out Vector3 wallDir, out float wallThick);
                        Vector3 perp = new Vector3(-wallDir.z, 0f, wallDir.x);
                        float halfW = windowWidth * 0.5f;
                        float halfT = Mathf.Max(wallThick * 0.5f, 0.05f) + 0.01f;

                        // Width along wallDir, thickness along perp
                        Vector3[] bottom = {
                            pos + Vector3.up * windowBottom - wallDir * halfW - perp * halfT,
                            pos + Vector3.up * windowBottom - wallDir * halfW + perp * halfT,
                            pos + Vector3.up * windowBottom + wallDir * halfW + perp * halfT,
                            pos + Vector3.up * windowBottom + wallDir * halfW - perp * halfT
                        };
                        Vector3[] top = new Vector3[4];
                        for (int j = 0; j < 4; j++)
                            top[j] = bottom[j] + Vector3.up * (windowTop - windowBottom);

                        var winObj = new GameObject($"WindowMesh[{i}]");
                        winObj.transform.SetParent(windowMeshContainer.transform);
                        winObj.AddComponent<MeshFilter>().mesh = BuildBoxMesh(bottom, top);
                        winObj.AddComponent<MeshRenderer>().material = windowMaterial;
                    }

                    Debug.Log($"[Mesh] Generated {doorPositions.Count} door meshes, {windowPositions.Count} window meshes");
                }

                // Apply scale AFTER all children are added, so SetParent doesn't
                // compensate child local scale to counteract the parent transform
                meshRoot.transform.localScale = new Vector3(scaleX, 1f, scaleZ);
            }

            // Transition to viewing — TransitionTo will set Y=0 and start the scale-up animation
            TransitionTo(AppState.CameraTransition);
            var viewBounds = _debugRoot != null
                ? new Bounds(_debugRoot.transform.position, Vector3.one * worldScale)
                : new Bounds(Vector3.zero, Vector3.one * worldScale);
            cameraController.LerpToPerspective(viewBounds.center, viewBounds, () =>
            {
                TransitionTo(AppState.Viewing);
            }, t =>
            {
                // Fade preview plane from 100% to 10% during camera transition
                SetPreviewPlaneOpacity(Mathf.Lerp(1f, 0.1f, t));
            });
        }

        // --- Helpers ---

        IEnumerator RevealSequence()
        {
            // Phase 1: Junction/spline points pop in one by one
            yield return StartCoroutine(PopInElements(_junctionReveals));

            // Phase 2: Connection lines draw in one by one
            yield return StartCoroutine(RevealConnectionLines());

            // Phase 3: Door dots pop in one by one
            yield return StartCoroutine(PopInElements(_doorReveals));

            // Phase 4: Window dots pop in one by one
            yield return StartCoroutine(PopInElements(_windowReveals));

            // Phase 5: Walls rise from the ground
            yield return StartCoroutine(AnimateMeshScaleUp(null));

            // Phase 6: Shrink all debug elements in unison, then disable their toggles
            yield return StartCoroutine(ShrinkAllDebugElements());

            // Reset elements to full size so toggles show them instantly
            RestoreDebugElementScales();

            // Hide containers and update toggle states
            _splinePointsVisible = false;
            _doorsWindowsVisible = false;
            SetActive(_junctionsContainer, false);
            SetActive(_doorsWindowsContainer, false);
            UpdateToggleButtonColor(splinePointsButton, false);
            UpdateToggleButtonColor(doorsWindowsButton, false);
        }

        IEnumerator ShrinkAllDebugElements()
        {
            // Combine all debug element lists for a unified shrink
            var allElements = new System.Collections.Generic.List<(Transform obj, float targetScale)>();
            allElements.AddRange(_junctionReveals);
            allElements.AddRange(_doorReveals);
            allElements.AddRange(_windowReveals);

            if (allElements.Count == 0) yield break;

            // Also shrink connection lines simultaneously
            float elapsed = 0f;
            while (elapsed < revealPopDuration)
            {
                float t = Mathf.SmoothStep(0f, 1f, elapsed / revealPopDuration);
                foreach (var (obj, scale) in allElements)
                {
                    if (obj != null)
                        obj.localScale = Vector3.Lerp(Vector3.one * scale, Vector3.zero, t);
                }
                foreach (var (lr, start, end) in _connectionLineReveals)
                {
                    if (lr != null)
                    {
                        float w = Mathf.Lerp(0.12f, 0f, t);
                        lr.startWidth = w;
                        lr.endWidth = w;
                    }
                }
                elapsed += Time.deltaTime;
                yield return null;
            }

            // Snap to zero
            foreach (var (obj, _) in allElements)
            {
                if (obj != null) obj.localScale = Vector3.zero;
            }
            foreach (var (lr, _, _) in _connectionLineReveals)
            {
                if (lr != null) { lr.startWidth = 0f; lr.endWidth = 0f; }
            }
        }

        /// <summary>
        /// Restore all debug element scales to their full target size so that
        /// toggling visibility back on shows them instantly without re-animating.
        /// </summary>
        void RestoreDebugElementScales()
        {
            foreach (var (obj, scale) in _junctionReveals)
            {
                if (obj != null) obj.localScale = Vector3.one * scale;
            }
            foreach (var (obj, scale) in _doorReveals)
            {
                if (obj != null) obj.localScale = Vector3.one * scale;
            }
            foreach (var (obj, scale) in _windowReveals)
            {
                if (obj != null) obj.localScale = Vector3.one * scale;
            }
            foreach (var (lr, start, end) in _connectionLineReveals)
            {
                if (lr != null)
                {
                    lr.SetPosition(1, end);
                    lr.startWidth = 0.12f;
                    lr.endWidth = 0.12f;
                }
            }
        }

        IEnumerator PopInElements(System.Collections.Generic.List<(Transform obj, float targetScale)> elements)
        {
            if (elements.Count == 0) yield break;

            for (int i = 0; i < elements.Count; i++)
            {
                var (obj, target) = elements[i];
                if (obj != null)
                    StartCoroutine(ScaleElement(obj, target));
                yield return new WaitForSeconds(revealStaggerDelay);
            }
            // Wait for the last element to finish its pop
            yield return new WaitForSeconds(revealPopDuration);
        }

        IEnumerator ScaleElement(Transform obj, float targetScale)
        {
            float elapsed = 0f;
            Vector3 target = Vector3.one * targetScale;
            while (elapsed < revealPopDuration)
            {
                if (obj == null) yield break;
                float t = Mathf.SmoothStep(0f, 1f, elapsed / revealPopDuration);
                obj.localScale = Vector3.Lerp(Vector3.zero, target, t);
                elapsed += Time.deltaTime;
                yield return null;
            }
            if (obj != null)
                obj.localScale = target;
        }

        IEnumerator RevealConnectionLines()
        {
            if (_connectionLineReveals.Count == 0) yield break;

            for (int i = 0; i < _connectionLineReveals.Count; i++)
            {
                var (lr, start, end) = _connectionLineReveals[i];
                if (lr != null)
                    StartCoroutine(DrawLine(lr, start, end));
                yield return new WaitForSeconds(revealStaggerDelay);
            }
            // Wait for the last line to finish drawing
            yield return new WaitForSeconds(revealPopDuration);
        }

        IEnumerator DrawLine(LineRenderer lr, Vector3 start, Vector3 end)
        {
            float elapsed = 0f;
            while (elapsed < revealPopDuration)
            {
                if (lr == null) yield break;
                float t = Mathf.SmoothStep(0f, 1f, elapsed / revealPopDuration);
                lr.SetPosition(1, Vector3.Lerp(start, end, t));
                elapsed += Time.deltaTime;
                yield return null;
            }
            if (lr != null)
                lr.SetPosition(1, end);
        }

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

        void ToggleCategory(ref bool visible, GameObject container, Button button)
        {
            visible = !visible;
            SetActive(container, visible);
            UpdateToggleButtonColor(button, visible);
        }

        Coroutine _previewFadeCoroutine;

        void TogglePreviewPlane()
        {
            _previewPlaneVisible = !_previewPlaneVisible;
            UpdateToggleButtonColor(previewPlaneButton, _previewPlaneVisible);
            float target = _previewPlaneVisible ? 1f : 0.05f;
            if (_previewFadeCoroutine != null)
                StopCoroutine(_previewFadeCoroutine);
            _previewFadeCoroutine = StartCoroutine(LerpPreviewPlaneOpacity(target));
        }

        void SetPreviewPlaneOpacity(float opacity)
        {
            var plane = imageCapture.GetPreviewPlane();
            if (plane == null) return;
            var renderer = plane.GetComponent<Renderer>();
            if (renderer != null && renderer.material != null)
                renderer.material.SetFloat("_Opacity", opacity);
        }

        IEnumerator LerpPreviewPlaneOpacity(float target)
        {
            var plane = imageCapture.GetPreviewPlane();
            if (plane == null) yield break;
            var renderer = plane.GetComponent<Renderer>();
            if (renderer == null || renderer.material == null) yield break;

            float start = renderer.material.GetFloat("_Opacity");
            float elapsed = 0f;
            float duration = 0.4f;
            while (elapsed < duration)
            {
                float t = Mathf.SmoothStep(0f, 1f, elapsed / duration);
                renderer.material.SetFloat("_Opacity", Mathf.Lerp(start, target, t));
                elapsed += Time.deltaTime;
                yield return null;
            }
            renderer.material.SetFloat("_Opacity", target);
            _previewFadeCoroutine = null;
        }

        static void UpdateToggleButtonColor(Button button, bool visible)
        {
            if (button == null) return;
            var colors = button.colors;
            colors.normalColor = visible ? ToggleOnColor : ToggleOffColor;
            colors.selectedColor = colors.normalColor;
            colors.highlightedColor = colors.normalColor;
            button.colors = colors;
        }

        /// <summary>
        /// Extend a junction chain in one direction by following collinear connections
        /// through degree-2 interior junctions.
        /// </summary>
        static void ExtendChain(
            System.Collections.Generic.List<int> chain,
            bool isVertical,
            System.Collections.Generic.Dictionary<int, Vector3> junctionPos,
            System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<(int neighbor, int connId)>> interiorAdj,
            System.Collections.Generic.HashSet<int> usedConns,
            System.Collections.Generic.Dictionary<int, RoomOutlineExtractor.Connection> interiorConns,
            ref float totalThickness, ref int thicknessCount,
            bool forward)
        {
            while (true)
            {
                int tip = forward ? chain[chain.Count - 1] : chain[0];
                if (!interiorAdj.TryGetValue(tip, out var neighbors)) break;

                // Only extend through junctions with exactly 2 interior neighbors
                if (neighbors.Count != 2) break;

                // Find the unused neighbor
                int nextConn = -1;
                int nextJunction = -1;
                foreach (var (neighbor, connId) in neighbors)
                {
                    if (!usedConns.Contains(connId))
                    {
                        nextConn = connId;
                        nextJunction = neighbor;
                        break;
                    }
                }
                if (nextConn < 0) break;

                // Check collinearity: the next segment must be on the same axis
                Vector3 tipPos = junctionPos[tip];
                Vector3 nextPos = junctionPos[nextJunction];
                bool nextIsVertical = Mathf.Abs(tipPos.x - nextPos.x) < Mathf.Abs(tipPos.z - nextPos.z);
                if (nextIsVertical != isVertical) break;

                usedConns.Add(nextConn);
                if (interiorConns.TryGetValue(nextConn, out var c))
                {
                    totalThickness += c.Thickness;
                    thicknessCount++;
                }

                if (forward)
                    chain.Add(nextJunction);
                else
                    chain.Insert(0, nextJunction);
            }
        }

        static Mesh BuildBoxMesh(Vector3[] bottom, Vector3[] top)
        {
            var vertices = new System.Collections.Generic.List<Vector3>(20);
            var normals = new System.Collections.Generic.List<Vector3>(20);
            var triangles = new System.Collections.Generic.List<int>(30);

            AddQuad(vertices, normals, triangles, top[0], top[1], top[2], top[3], Vector3.up);
            AddQuad(vertices, normals, triangles, bottom[0], bottom[1], top[1], top[0],
                Vector3.Cross(bottom[1] - bottom[0], top[1] - bottom[0]).normalized);
            AddQuad(vertices, normals, triangles, bottom[1], bottom[2], top[2], top[1],
                Vector3.Cross(bottom[2] - bottom[1], top[2] - bottom[1]).normalized);
            AddQuad(vertices, normals, triangles, bottom[2], bottom[3], top[3], top[2],
                Vector3.Cross(bottom[3] - bottom[2], top[3] - bottom[2]).normalized);
            AddQuad(vertices, normals, triangles, bottom[3], bottom[0], top[0], top[3],
                Vector3.Cross(bottom[0] - bottom[3], top[0] - bottom[3]).normalized);

            var mesh = new Mesh
            {
                vertices = vertices.ToArray(),
                normals = normals.ToArray(),
                triangles = triangles.ToArray()
            };
            mesh.RecalculateBounds();
            return mesh;
        }

        static void AddQuad(
            System.Collections.Generic.List<Vector3> verts,
            System.Collections.Generic.List<Vector3> norms,
            System.Collections.Generic.List<int> tris,
            Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal)
        {
            int start = verts.Count;
            verts.Add(a); verts.Add(b); verts.Add(c); verts.Add(d);
            norms.Add(normal); norms.Add(normal); norms.Add(normal); norms.Add(normal);
            tris.Add(start); tris.Add(start + 1); tris.Add(start + 2);
            tris.Add(start); tris.Add(start + 2); tris.Add(start + 3);
        }

        /// <summary>
        /// Find the nearest wall segment to a point and return its direction and thickness.
        /// </summary>
        static void FindNearestWall(Vector3 point, System.Collections.Generic.List<(Vector3 a, Vector3 b, float thickness)> walls,
            out Vector3 wallDir, out float wallThickness)
        {
            wallDir = Vector3.right;
            wallThickness = 0.1f;
            float bestDist = float.MaxValue;

            foreach (var (a, b, thick) in walls)
            {
                // Project point onto line segment a→b, find closest point
                Vector3 ab = b - a;
                float len = ab.magnitude;
                if (len < 0.001f) continue;
                Vector3 abNorm = ab / len;
                float t = Mathf.Clamp(Vector3.Dot(point - a, abNorm), 0f, len);
                Vector3 closest = a + abNorm * t;
                float dist = Vector3.Distance(new Vector3(point.x, 0f, point.z),
                                              new Vector3(closest.x, 0f, closest.z));
                if (dist < bestDist)
                {
                    bestDist = dist;
                    wallDir = abNorm;
                    wallThickness = thick;
                }
            }
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
        static GameObject CreateDebugSphere(string name, Material mat)
        {
            if (_sharedSphereMesh == null)
            {
                var tmp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                _sharedSphereMesh = tmp.GetComponent<MeshFilter>().sharedMesh;
                Destroy(tmp);
            }

            var go = new GameObject(name);
            go.AddComponent<MeshFilter>().sharedMesh = _sharedSphereMesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            return go;
        }

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
