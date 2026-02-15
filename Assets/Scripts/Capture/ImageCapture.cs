using System;
using System.Collections;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace FloorplanVectoriser.Capture
{
    /// <summary>
    /// Handles camera preview and image capture for the floorplan scanner.
    /// Supports both live camera feed and gallery image selection on Android.
    /// </summary>
    public class ImageCapture : MonoBehaviour
    {
        [SerializeField] private RawImage previewImage;
        [SerializeField] private int targetSize = 1024;

        [Header("Camera Selection")]
        [SerializeField] private Button cameraSwitchButton;
        [SerializeField] private TMP_Text cameraNameText;
        
        [Header("3D Preview Plane")]
        [SerializeField] private MeshRenderer previewPlaneRenderer;
        [SerializeField] private Material previewPlaneMaterial;

        [Header("Debugging")]
        [SerializeField] private Texture2D debugFloorPlanTexture;

        GameObject _previewPlane;
        Material _previewPlaneMaterialInstance;
        WebCamTexture _webCamTexture;
        Texture2D _capturedImage;
        bool _isPreviewing;
        float _currentWorldScale;
        float _currentImageAspectRatio = 1f; // Aspect ratio of current image (camera or loaded)
        int _selectedCameraIndex = -1;
        WebCamDevice[] _availableCameras;
        
        /// <summary>Event fired when the camera resolution is known (after camera starts).</summary>
        public event Action<int, int> OnCameraResolutionChanged;

        void Awake()
        {
            InitializeCameraList();
        }

        /// <summary>
        /// Initialize the list of available cameras and set up the switch button.
        /// </summary>
        public void InitializeCameraList()
        {
            _availableCameras = WebCamTexture.devices;
            Debug.Log($"ImageCapture: Found {_availableCameras.Length} webcam devices");

            for (int i = 0; i < _availableCameras.Length; i++)
            {
                var device = _availableCameras[i];
                Debug.Log($"ImageCapture: Camera {i}: '{device.name}' (front facing: {device.isFrontFacing})");
            }

            // Default to first rear-facing camera, or first available
            _selectedCameraIndex = 0;
            for (int i = 0; i < _availableCameras.Length; i++)
            {
                if (!_availableCameras[i].isFrontFacing)
                {
                    _selectedCameraIndex = i;
                    break;
                }
            }

            // Set up the switch button
            if (cameraSwitchButton != null)
            {
                cameraSwitchButton.onClick.AddListener(OnCameraSwitchPressed);
                
                // Disable button if there's 0 or 1 camera (nothing to switch to)
                cameraSwitchButton.interactable = _availableCameras.Length > 1;
            }

            // Update the camera name text
            UpdateCameraNameText();
        }

        void OnCameraSwitchPressed()
        {
            if (_availableCameras == null || _availableCameras.Length <= 1) return;

            // Cycle to the next camera
            int nextIndex = (_selectedCameraIndex + 1) % _availableCameras.Length;
            _selectedCameraIndex = nextIndex;
            
            Debug.Log($"ImageCapture: Switched to camera {nextIndex}: {_availableCameras[nextIndex].name}");
            
            UpdateCameraNameText();

            // If currently previewing, restart with new camera
            if (_isPreviewing)
            {
                StopPreview();
                StartPreview();
            }
        }

        /// <summary>
        /// Update the camera name text to show the current camera.
        /// </summary>
        void UpdateCameraNameText()
        {
            if (cameraNameText == null) return;

            if (_availableCameras == null || _availableCameras.Length == 0)
            {
                cameraNameText.text = "No Camera";
                return;
            }

            if (_selectedCameraIndex >= 0 && _selectedCameraIndex < _availableCameras.Length)
            {
                var device = _availableCameras[_selectedCameraIndex];
                string suffix = device.isFrontFacing ? "(Front)" : "(Back)";
                cameraNameText.text = $"{device.name} {suffix}";
            }
        }

        /// <summary>
        /// Get the number of available cameras.
        /// </summary>
        public int GetCameraCount() => _availableCameras?.Length ?? 0;

        /// <summary>
        /// Get information about a specific camera.
        /// </summary>
        public WebCamDevice? GetCameraInfo(int index)
        {
            if (_availableCameras == null || index < 0 || index >= _availableCameras.Length)
                return null;
            return _availableCameras[index];
        }

        /// <summary>
        /// Get the currently selected camera index.
        /// </summary>
        public int GetSelectedCameraIndex() => _selectedCameraIndex;

        /// <summary>
        /// Set the active camera by index.
        /// </summary>
        public void SetSelectedCamera(int index)
        {
            if (_availableCameras == null || index < 0 || index >= _availableCameras.Length)
            {
                Debug.LogWarning($"ImageCapture: Invalid camera index {index}");
                return;
            }

            _selectedCameraIndex = index;
            UpdateCameraNameText();
            
            if (_isPreviewing)
            {
                StopPreview();
                StartPreview();
            }
        }

        /// <summary>
        /// Get the current camera resolution. Returns (0,0) if camera is not active.
        /// </summary>
        public Vector2Int GetCurrentResolution()
        {
            if (_webCamTexture != null && _webCamTexture.isPlaying)
            {
                return new Vector2Int(_webCamTexture.width, _webCamTexture.height);
            }
            return Vector2Int.zero;
        }

        /// <summary>
        /// Get the current image aspect ratio (width/height). 
        /// Returns the aspect ratio of the active camera or loaded image.
        /// </summary>
        public float GetCurrentAspectRatio()
        {
            return _currentImageAspectRatio;
        }

        /// <summary>Clear the captured image so the preview plane won't hold a stale texture.</summary>
        public void ClearCapturedImage()
        {
            if (_capturedImage != null)
            {
                Destroy(_capturedImage);
                _capturedImage = null;
            }
        }

        /// <summary>Start the camera feed and display on the preview RawImage.</summary>
        public void StartPreview()
        {
            if (_isPreviewing) return;

            // Release any previous capture so the preview plane doesn't show a stale frame
            ClearCapturedImage();

            // If using debug texture, apply it to the preview plane immediately
            if (debugFloorPlanTexture != null)
            {
                Debug.Log("ImageCapture: Using debug floorplan texture for preview");
                // Preview will be square (center crop)
                _currentImageAspectRatio = 1f;
                UpdatePreviewPlaneTexture(debugFloorPlanTexture);
                // Apply UV crop to show center square of debug texture
                ApplySquareCropUV(debugFloorPlanTexture.width, debugFloorPlanTexture.height);
                if (_currentWorldScale > 0)
                {
                    UpdatePreviewPlaneAspectRatio();
                }
                _isPreviewing = true;
                return;
            }
            
            // Reset aspect ratio until camera resolution is detected
            _currentImageAspectRatio = 1f;

            // Use selected camera or find default
            string deviceName = null;
            if (_availableCameras != null && _availableCameras.Length > 0)
            {
                if (_selectedCameraIndex >= 0 && _selectedCameraIndex < _availableCameras.Length)
                {
                    deviceName = _availableCameras[_selectedCameraIndex].name;
                }
                else
                {
                    // Fallback: prefer rear camera
                    foreach (var device in _availableCameras)
                    {
                        if (!device.isFrontFacing)
                        {
                            deviceName = device.name;
                            break;
                        }
                    }
                }
            }

            // Reuse existing webcam texture if it matches the selected device,
            // otherwise destroy and create a new one. Reusing avoids Android issues
            // where the camera hardware hasn't fully released yet.
            if (_webCamTexture != null && _webCamTexture.deviceName == deviceName)
            {
                Debug.Log($"ImageCapture: Reusing existing webcam texture '{_webCamTexture.deviceName}'");
            }
            else
            {
                if (_webCamTexture != null)
                {
                    _webCamTexture.Stop();
                    Destroy(_webCamTexture);
                }

                _webCamTexture = deviceName != null
                    ? new WebCamTexture(deviceName, 1920, 1080)
                    : new WebCamTexture(1920, 1080);
            }

            Debug.Log($"ImageCapture: Starting webcam '{_webCamTexture.deviceName}'");
            _webCamTexture.Play();
            
            if (previewImage != null)
            {
                previewImage.texture = _webCamTexture;
                previewImage.gameObject.SetActive(true);
            }
            UpdatePreviewPlaneTexture(_webCamTexture);
            _isPreviewing = true;

            // Start coroutine to detect actual resolution once camera is ready
            StartCoroutine(WaitForCameraResolution());
        }

        IEnumerator WaitForCameraResolution()
        {
            // Wait for the camera to actually start and report its resolution
            float timeout = 5f;
            float elapsed = 0f;
            
            while (_webCamTexture != null && _webCamTexture.width < 100 && elapsed < timeout)
            {
                elapsed += Time.deltaTime;
                yield return null;
            }

            if (_webCamTexture != null && _webCamTexture.width >= 100)
            {
                Debug.Log($"ImageCapture: Camera resolution detected: {_webCamTexture.width}x{_webCamTexture.height}");
                
                // Preview will show the center-cropped square, so aspect ratio is 1:1
                _currentImageAspectRatio = 1f;
                
                // Apply UV cropping to show only the center square portion of the webcam feed
                ApplySquareCropUV(_webCamTexture.width, _webCamTexture.height);
                
                // Update preview plane to be square
                if (_currentWorldScale > 0)
                {
                    UpdatePreviewPlaneAspectRatio();
                }

                // Fire event for external listeners
                OnCameraResolutionChanged?.Invoke(_webCamTexture.width, _webCamTexture.height);
            }
            else
            {
                Debug.LogWarning("ImageCapture: Timed out waiting for camera resolution");
            }
        }

        /// <summary>Stop the camera feed.</summary>
        public void StopPreview()
        {
            if (!_isPreviewing) return;
            if (_webCamTexture != null && _webCamTexture.isPlaying)
                _webCamTexture.Stop();
            _isPreviewing = false;
        }

        /// <summary>
        /// Capture the current camera frame as a Texture2D, cropped to square and resized to targetSize x targetSize.
        /// </summary>
        public Texture2D Capture()
        {
            if (debugFloorPlanTexture != null)
            {
                // Crop debug texture to square just like camera capture
                _capturedImage = CropToSquareAndResize(debugFloorPlanTexture, targetSize);
                _currentImageAspectRatio = 1f;
                ResetUV();
                UpdatePreviewPlaneTexture(_capturedImage);
                UpdatePreviewPlaneAspectRatio();
                return _capturedImage;
            }

            if (_webCamTexture == null || !_webCamTexture.isPlaying)
            {
                Debug.LogWarning("ImageCapture: No active camera feed to capture.");
                return null;
            }

            // Read pixels from webcam
            var tempTex = new Texture2D(_webCamTexture.width, _webCamTexture.height, TextureFormat.RGB24, false);
            tempTex.SetPixels(_webCamTexture.GetPixels());
            tempTex.Apply();

            // Center-crop to square using the smaller dimension, then resize
            _capturedImage = CropToSquareAndResize(tempTex, targetSize);
            Destroy(tempTex);

            // Update aspect ratio to 1:1 since we're now using a square crop
            _currentImageAspectRatio = 1f;

            // Reset UV since the captured texture is already cropped to square
            ResetUV();

            // Show the frozen capture in the preview
            if (previewImage != null)
                previewImage.texture = _capturedImage;
            UpdatePreviewPlaneTexture(_capturedImage);
            UpdatePreviewPlaneAspectRatio();

            StopPreview();
            return _capturedImage;
        }

        /// <summary>
        /// Load an image from a file path (for gallery picker or testing).
        /// The image is center-cropped to square to match camera capture behavior.
        /// </summary>
        public Texture2D LoadFromFile(string path)
        {
            byte[] fileData = System.IO.File.ReadAllBytes(path);
            var tex = new Texture2D(2, 2);
            if (!tex.LoadImage(fileData))
            {
                Debug.LogError($"Failed to load image from: {path}");
                Destroy(tex);
                return null;
            }

            Debug.Log($"ImageCapture: Loaded image {tex.width}x{tex.height}, will center-crop to square");

            // Center-crop to square and resize (same as camera capture)
            _capturedImage = CropToSquareAndResize(tex, targetSize);
            Destroy(tex);

            // Aspect ratio is now 1:1 since we cropped to square
            _currentImageAspectRatio = 1f;

            // Reset UV since the texture is already cropped to square
            ResetUV();

            if (previewImage != null)
                previewImage.texture = _capturedImage;
            UpdatePreviewPlaneTexture(_capturedImage);

            // Update the preview plane to square
            if (_currentWorldScale > 0)
            {
                UpdatePreviewPlaneAspectRatio();
            }

            return _capturedImage;
        }

        /// <summary>Returns the last captured/loaded image.</summary>
        public Texture2D GetCapturedImage() => _capturedImage;

        /// <summary>
        /// Set up the 3D preview plane centered in the orthographic camera view.
        /// Creates a quad dynamically if no previewPlaneRenderer is assigned.
        /// The plane will be scaled to match the image aspect ratio.
        /// </summary>
        /// <param name="worldScale">The world scale matching AppController's worldScale.</param>
        public void SetupPreviewPlane(float worldScale)
        {
            _currentWorldScale = worldScale;
            
            // Create the preview plane if it doesn't exist
            if (_previewPlane == null)
            {
                CreatePreviewPlane();
            }

            // Position at world origin (visualization is centered around zero)
            _previewPlane.transform.position = Vector3.zero;
            _previewPlane.transform.rotation = Quaternion.Euler(90f, 0f, 0f); // Flat on ground facing up
            
            // Initial scale - will be updated when camera/image aspect ratio is known
            _previewPlane.transform.localScale = new Vector3(worldScale, worldScale, 1f);
            _previewPlane.SetActive(true);

            // Apply any existing texture (webcam or captured image)
            if (_webCamTexture != null && _webCamTexture.isPlaying)
            {
                UpdatePreviewPlaneTexture(_webCamTexture);
                UpdatePreviewPlaneAspectRatio();
            }
            else if (_capturedImage != null)
            {
                UpdatePreviewPlaneTexture(_capturedImage);
                // Use the stored aspect ratio from the loaded image
                UpdatePreviewPlaneAspectRatio();
            }
        }

        /// <summary>
        /// Update the preview plane scale to match the current camera's aspect ratio.
        /// The plane will fit within the worldScale bounds while maintaining aspect ratio.
        /// </summary>
        void UpdatePreviewPlaneAspectRatio()
        {
            if (_previewPlane == null || _currentWorldScale <= 0) return;

            float aspectRatio = GetCurrentAspectRatio();
            if (aspectRatio <= 0) return;

            float width, height;
            
            if (aspectRatio >= 1f)
            {
                // Landscape: width is constrained by worldScale
                width = _currentWorldScale;
                height = _currentWorldScale / aspectRatio;
            }
            else
            {
                // Portrait: height is constrained by worldScale
                height = _currentWorldScale;
                width = _currentWorldScale * aspectRatio;
            }

            // Center at world origin
            _previewPlane.transform.position = Vector3.zero;
            _previewPlane.transform.localScale = new Vector3(width, height, 1f);

            Debug.Log($"ImageCapture: Preview plane adjusted to {width:F2}x{height:F2} (aspect ratio: {aspectRatio:F2})");
        }

        /// <summary>
        /// Update the world scale and re-adjust the preview plane to match.
        /// Called when the user applies a wall measurement to rescale the scene.
        /// </summary>
        public void UpdateWorldScale(float newWorldScale)
        {
            _currentWorldScale = newWorldScale;
            UpdatePreviewPlaneAspectRatio();
        }

        /// <summary>
        /// Hide the 3D preview plane (e.g., when transitioning to mesh viewing).
        /// </summary>
        /// <summary>Returns the preview plane GameObject (for material access).</summary>
        public GameObject GetPreviewPlane() => _previewPlane;

        public void HidePreviewPlane()
        {
            if (_previewPlane != null)
            {
                _previewPlane.SetActive(false);
            }
        }

        void CreatePreviewPlane()
        {
            // Use assigned renderer if available
            if (previewPlaneRenderer != null)
            {
                _previewPlane = previewPlaneRenderer.gameObject;
                _previewPlaneMaterialInstance = previewPlaneRenderer.material;
                Debug.Log("ImageCapture: Using assigned preview plane renderer");
                return;
            }

            // Create a quad dynamically
            _previewPlane = GameObject.CreatePrimitive(PrimitiveType.Quad);
            _previewPlane.name = "FloorplanPreviewPlane";
            Debug.Log("ImageCapture: Created dynamic preview plane");

            // Remove collider - we don't need it
            var collider = _previewPlane.GetComponent<Collider>();
            if (collider != null)
                Destroy(collider);

            // Set up material with UnlitFade shader for opacity control
            var renderer = _previewPlane.GetComponent<MeshRenderer>();
            var unlitFadeShader = Shader.Find("Custom/UnlitFade");
            if (unlitFadeShader != null)
            {
                _previewPlaneMaterialInstance = new Material(unlitFadeShader);
                _previewPlaneMaterialInstance.SetFloat("_Opacity", 1f);
                _previewPlaneMaterialInstance.SetFloat("_BorderWidth", 0.01f);
                _previewPlaneMaterialInstance.SetColor("_BorderColor", new Color(0f, 1f, 1f, 1f));
                renderer.material = _previewPlaneMaterialInstance;
                Debug.Log("ImageCapture: Using Custom/UnlitFade shader for preview plane");
            }
            else if (previewPlaneMaterial != null)
            {
                _previewPlaneMaterialInstance = new Material(previewPlaneMaterial);
                renderer.material = _previewPlaneMaterialInstance;
                Debug.Log("ImageCapture: Using assigned preview plane material");
            }
            else
            {
                _previewPlaneMaterialInstance = renderer.material;
                Debug.Log($"ImageCapture: Using default primitive material with shader: {_previewPlaneMaterialInstance.shader.name}");
            }
        }

        void UpdatePreviewPlaneTexture(Texture texture)
        {
            if (_previewPlaneMaterialInstance == null)
            {
                Debug.LogWarning("ImageCapture: Cannot update preview plane texture - material instance is null");
                return;
            }
            if (texture == null)
            {
                Debug.LogWarning("ImageCapture: Cannot update preview plane texture - texture is null");
                return;
            }
            
            // Set texture on common property names to support different render pipelines
            // Built-in RP uses _MainTex, URP uses _BaseMap
            _previewPlaneMaterialInstance.mainTexture = texture;
            if (_previewPlaneMaterialInstance.HasProperty("_BaseMap"))
            {
                _previewPlaneMaterialInstance.SetTexture("_BaseMap", texture);
            }
            Debug.Log($"ImageCapture: Applied texture '{texture.name}' ({texture.width}x{texture.height}) to preview plane");
        }

        /// <summary>
        /// Apply UV tiling/offset to show only the center square crop of a non-square texture.
        /// This makes the live preview show exactly what will be captured.
        /// </summary>
        void ApplySquareCropUV(int textureWidth, int textureHeight)
        {
            if (_previewPlaneMaterialInstance == null) return;

            float aspectRatio = (float)textureWidth / textureHeight;
            Vector2 tiling;
            Vector2 offset;

            if (aspectRatio >= 1f)
            {
                // Landscape: crop sides (width > height)
                float cropRatio = (float)textureHeight / textureWidth;
                tiling = new Vector2(cropRatio, 1f);
                offset = new Vector2((1f - cropRatio) / 2f, 0f);
            }
            else
            {
                // Portrait: crop top/bottom (height > width)
                float cropRatio = (float)textureWidth / textureHeight;
                tiling = new Vector2(1f, cropRatio);
                offset = new Vector2(0f, (1f - cropRatio) / 2f);
            }

            // Apply to material (works for both Built-in RP and URP)
            _previewPlaneMaterialInstance.mainTextureScale = tiling;
            _previewPlaneMaterialInstance.mainTextureOffset = offset;
            
            if (_previewPlaneMaterialInstance.HasProperty("_BaseMap"))
            {
                _previewPlaneMaterialInstance.SetTextureScale("_BaseMap", tiling);
                _previewPlaneMaterialInstance.SetTextureOffset("_BaseMap", offset);
            }

            Debug.Log($"ImageCapture: Applied square crop UV - tiling: {tiling}, offset: {offset} (aspect: {aspectRatio:F2})");
        }

        /// <summary>
        /// Reset UV tiling/offset to show the full texture (used after capture when texture is already square).
        /// </summary>
        void ResetUV()
        {
            if (_previewPlaneMaterialInstance == null) return;

            _previewPlaneMaterialInstance.mainTextureScale = Vector2.one;
            _previewPlaneMaterialInstance.mainTextureOffset = Vector2.zero;
            
            if (_previewPlaneMaterialInstance.HasProperty("_BaseMap"))
            {
                _previewPlaneMaterialInstance.SetTextureScale("_BaseMap", Vector2.one);
                _previewPlaneMaterialInstance.SetTextureOffset("_BaseMap", Vector2.zero);
            }
        }

        /// <summary>
        /// Open a file picker to select an image. Works on Windows, Android, and in the Editor.
        /// </summary>
        public void OpenGallery(System.Action<Texture2D> onImageSelected)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            StartCoroutine(AndroidGalleryCoroutine(onImageSelected));
#elif UNITY_EDITOR
            OpenGalleryEditor(onImageSelected);
#elif UNITY_STANDALONE_WIN
            OpenGalleryWindows(onImageSelected);
#else
            Debug.Log("Gallery picker not available on this platform.");
            onImageSelected?.Invoke(null);
#endif
        }

#if UNITY_EDITOR
        void OpenGalleryEditor(System.Action<Texture2D> onImageSelected)
        {
            string path = EditorUtility.OpenFilePanel("Select Floorplan Image", "", "png,jpg,jpeg");
            if (!string.IsNullOrEmpty(path))
            {
                var tex = LoadFromFile(path);
                onImageSelected?.Invoke(tex);
            }
            else
            {
                onImageSelected?.Invoke(null);
            }
        }
#endif

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        [DllImport("comdlg32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        static extern bool GetOpenFileName(ref OpenFileName ofn);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        struct OpenFileName
        {
            public int lStructSize;
            public IntPtr hwndOwner;
            public IntPtr hInstance;
            public string lpstrFilter;
            public string lpstrCustomFilter;
            public int nMaxCustFilter;
            public int nFilterIndex;
            public string lpstrFile;
            public int nMaxFile;
            public string lpstrFileTitle;
            public int nMaxFileTitle;
            public string lpstrInitialDir;
            public string lpstrTitle;
            public int Flags;
            public short nFileOffset;
            public short nFileExtension;
            public string lpstrDefExt;
            public IntPtr lCustData;
            public IntPtr lpfnHook;
            public string lpTemplateName;
            public IntPtr pvReserved;
            public int dwReserved;
            public int flagsEx;
        }

        void OpenGalleryWindows(System.Action<Texture2D> onImageSelected)
        {
            var ofn = new OpenFileName();
            ofn.lStructSize = Marshal.SizeOf(ofn);
            ofn.lpstrFilter = "Image Files\0*.png;*.jpg;*.jpeg;*.bmp\0All Files\0*.*\0";
            ofn.lpstrFile = new string(new char[256]);
            ofn.nMaxFile = ofn.lpstrFile.Length;
            ofn.lpstrFileTitle = new string(new char[64]);
            ofn.nMaxFileTitle = ofn.lpstrFileTitle.Length;
            ofn.lpstrTitle = "Select Floorplan Image";
            ofn.Flags = 0x00080000 | 0x00001000; // OFN_EXPLORER | OFN_FILEMUSTEXIST

            if (GetOpenFileName(ref ofn))
            {
                string path = ofn.lpstrFile;
                Debug.Log($"ImageCapture: Selected file: {path}");
                var tex = LoadFromFile(path);
                onImageSelected?.Invoke(tex);
            }
            else
            {
                Debug.Log("ImageCapture: File selection cancelled");
                onImageSelected?.Invoke(null);
            }
        }
#endif

#if UNITY_ANDROID && !UNITY_EDITOR
        IEnumerator AndroidGalleryCoroutine(System.Action<Texture2D> onImageSelected)
        {
            // Use NativeFilePicker or a simple Android intent
            // This is a minimal implementation; consider using a plugin like
            // NativeGallery (https://github.com/yasirkula/UnityNativeGallery) for production
            NativeGallery.GetImageFromGallery((path) =>
            {
                if (!string.IsNullOrEmpty(path))
                {
                    var tex = LoadFromFile(path);
                    onImageSelected?.Invoke(tex);
                }
                else
                {
                    onImageSelected?.Invoke(null);
                }
            }, "Select Floorplan Image");
            yield break;
        }
#endif

        /// <summary>Resize a texture using bilinear sampling.</summary>
        static Texture2D ResizeTexture(Texture2D source, int newWidth, int newHeight)
        {
            RenderTexture rt = RenderTexture.GetTemporary(newWidth, newHeight, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(source, rt);

            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;

            var result = new Texture2D(newWidth, newHeight, TextureFormat.RGB24, false);
            result.ReadPixels(new Rect(0, 0, newWidth, newHeight), 0, 0);
            result.Apply();

            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            return result;
        }

        /// <summary>
        /// Center-crop the source texture to a square (using the smaller dimension), then resize to targetSize.
        /// This avoids distortion by cropping rather than squishing.
        /// </summary>
        static Texture2D CropToSquareAndResize(Texture2D source, int targetSize)
        {
            int srcWidth = source.width;
            int srcHeight = source.height;
            int cropSize = Mathf.Min(srcWidth, srcHeight);
            
            // Calculate center crop offset
            int offsetX = (srcWidth - cropSize) / 2;
            int offsetY = (srcHeight - cropSize) / 2;

            Debug.Log($"ImageCapture: Cropping {srcWidth}x{srcHeight} to {cropSize}x{cropSize} square (offset: {offsetX}, {offsetY}), then resizing to {targetSize}x{targetSize}");

            // Use GPU blit with source rect to crop and resize in one pass
            RenderTexture rt = RenderTexture.GetTemporary(targetSize, targetSize, 0, RenderTextureFormat.ARGB32);
            
            // Calculate UV rect for the center crop
            float uvX = (float)offsetX / srcWidth;
            float uvY = (float)offsetY / srcHeight;
            float uvWidth = (float)cropSize / srcWidth;
            float uvHeight = (float)cropSize / srcHeight;

            // Blit with source rect to crop and scale
            Graphics.Blit(source, rt, new Vector2(uvWidth, uvHeight), new Vector2(uvX, uvY));

            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;

            var result = new Texture2D(targetSize, targetSize, TextureFormat.RGB24, false);
            result.ReadPixels(new Rect(0, 0, targetSize, targetSize), 0, 0);
            result.Apply();

            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            return result;
        }

        void OnDestroy()
        {
            StopPreview();
            
            if (cameraSwitchButton != null)
                cameraSwitchButton.onClick.RemoveListener(OnCameraSwitchPressed);
            
            if (_webCamTexture != null)
                Destroy(_webCamTexture);
            if (_previewPlaneMaterialInstance != null)
                Destroy(_previewPlaneMaterialInstance);
            // Only destroy the plane if we created it dynamically
            if (_previewPlane != null && previewPlaneRenderer == null)
                Destroy(_previewPlane);
        }
    }
}
