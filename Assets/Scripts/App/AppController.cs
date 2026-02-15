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
                        // Compute center of the 4-vertex polygon in world space
                        Vector3 center = Vector3.zero;
                        for (int i = 0; i < poly.Vertices.Length; i++)
                        {
                            center += new Vector3(
                                poly.Vertices[i].x * photoCaptureSize.x,
                                0f,
                                (1f - poly.Vertices[i].y) * photoCaptureSize.y);
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

                // Build a quick lookup from junction ID to position (used by debug vis + mesh gen)
                var junctionPos = new System.Collections.Generic.Dictionary<int, Vector3>();
                foreach (var j in extraction.Junctions)
                    junctionPos[j.Id] = j.Position;

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

                // Door positions (yellow spheres)
                if (doorPositions.Count > 0)
                {
                    var doorMat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                    doorMat.color = Color.yellow;

                    for (int i = 0; i < doorPositions.Count; i++)
                    {
                        var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                        sphere.name = $"Door[{i}]";
                        sphere.transform.SetParent(debugRoot.transform);
                        sphere.transform.localPosition = doorPositions[i];
                        sphere.transform.localScale = Vector3.one * 0.2f;
                        sphere.GetComponent<Renderer>().sharedMaterial = doorMat;
                        Destroy(sphere.GetComponent<Collider>());

                        CreateDebugLabel($"D{i}", doorPositions[i], debugRoot.transform, Color.yellow);
                    }
                }

                // Window positions (purple spheres)
                if (windowPositions.Count > 0)
                {
                    var winMat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                    winMat.color = new Color(0.6f, 0f, 0.8f); // purple

                    for (int i = 0; i < windowPositions.Count; i++)
                    {
                        var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                        sphere.name = $"Window[{i}]";
                        sphere.transform.SetParent(debugRoot.transform);
                        sphere.transform.localPosition = windowPositions[i];
                        sphere.transform.localScale = Vector3.one * 0.2f;
                        sphere.GetComponent<Renderer>().sharedMaterial = winMat;
                        Destroy(sphere.GetComponent<Collider>());

                        CreateDebugLabel($"W{i}", windowPositions[i], debugRoot.transform, Color.magenta);
                    }
                }

                Debug.Log($"[Debug] {doorPositions.Count} doors (yellow), {windowPositions.Count} windows (purple)");

                debugRoot.transform.localScale = new Vector3(scaleX, 1f, scaleZ);

                Debug.Log($"[Debug] {extraction.Junctions.Count} junctions, " +
                          $"{extraction.Connections.Count} connections, " +
                          $"{extraction.Intersections.Count} intersections " +
                          $"[weldTolerance={weldTolerance:F3}m]");

                // Generate outer wall mesh from boundary connections
                var meshRoot = new GameObject("OuterWallMesh");
                _generatedMeshRoot = meshRoot;

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
                    wallObj.transform.SetParent(meshRoot.transform);
                    wallObj.AddComponent<MeshFilter>().mesh = BuildBoxMesh(bottom, top);
                    wallObj.AddComponent<MeshRenderer>().material = wallMaterial;
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
                        wallObj.transform.SetParent(meshRoot.transform);
                        wallObj.AddComponent<MeshFilter>().mesh = BuildBoxMesh(bottom, top);
                        wallObj.AddComponent<MeshRenderer>().material = wallMaterial;
                        interiorCount++;
                    }

                    Debug.Log($"[Mesh] Generated {interiorCount} interior wall panels " +
                              $"(from {interiorConns.Count} connections, merged into {chains.Count} chains)");
                }

                // Apply scale AFTER all children are added, so SetParent doesn't
                // compensate child local scale to counteract the parent transform
                meshRoot.transform.localScale = new Vector3(scaleX, 1f, scaleZ);
            }

            // Transition to viewing
            TransitionTo(AppState.CameraTransition);
            var viewBounds = _debugRoot != null
                ? new Bounds(_debugRoot.transform.position, Vector3.one * worldScale)
                : new Bounds(Vector3.zero, Vector3.one * worldScale);
            cameraController.LerpToPerspective(viewBounds.center, viewBounds, () =>
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
