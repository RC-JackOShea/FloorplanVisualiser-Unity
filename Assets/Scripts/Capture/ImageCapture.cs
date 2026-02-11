using System.Collections;
using UnityEngine;
using UnityEngine.UI;

namespace FloorplanVectoriser.Capture
{
    /// <summary>
    /// Handles camera preview and image capture for the floorplan scanner.
    /// Supports both live camera feed and gallery image selection on Android.
    /// </summary>
    public class ImageCapture : MonoBehaviour
    {
        [SerializeField] private RawImage previewImage;
        [SerializeField] private int targetSize = 512;

        [Header("Debugging")]
        [SerializeField] private Texture2D debugFloorPlanTexture;

        WebCamTexture _webCamTexture;
        Texture2D _capturedImage;
        bool _isPreviewing;

        /// <summary>Start the rear camera feed and display on the preview RawImage.</summary>
        public void StartPreview()
        {
            if (_isPreviewing) return;

            // Prefer rear camera
            string deviceName = null;
            foreach (var device in WebCamTexture.devices)
            {
                if (!device.isFrontFacing)
                {
                    deviceName = device.name;
                    break;
                }
            }

            _webCamTexture = deviceName != null
                ? new WebCamTexture(deviceName, 1280, 720)
                : new WebCamTexture(1280, 720);

            _webCamTexture.Play();
            if (previewImage != null)
            {
                previewImage.texture = _webCamTexture;
                previewImage.gameObject.SetActive(true);
            }
            _isPreviewing = true;
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
        /// Capture the current camera frame as a Texture2D, resized to targetSize x targetSize.
        /// </summary>
        public Texture2D Capture()
        {
            if (debugFloorPlanTexture != null)
            {
                _capturedImage = debugFloorPlanTexture;
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

            // Resize to target dimensions
            _capturedImage = ResizeTexture(tempTex, targetSize, targetSize);
            Destroy(tempTex);

            // Show the frozen capture in the preview
            if (previewImage != null)
                previewImage.texture = _capturedImage;

            StopPreview();
            return _capturedImage;
        }

        /// <summary>
        /// Load an image from a file path (for gallery picker or testing).
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

            _capturedImage = ResizeTexture(tex, targetSize, targetSize);
            Destroy(tex);

            if (previewImage != null)
                previewImage.texture = _capturedImage;

            return _capturedImage;
        }

        /// <summary>Returns the last captured/loaded image.</summary>
        public Texture2D GetCapturedImage() => _capturedImage;

        /// <summary>
        /// Open Android gallery to pick an image. Uses a native intent on Android,
        /// falls back to a file path prompt in editor.
        /// </summary>
        public void OpenGallery(System.Action<Texture2D> onImageSelected)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            StartCoroutine(AndroidGalleryCoroutine(onImageSelected));
#else
            // In editor: use a hardcoded test path or skip
            Debug.Log("Gallery picker not available in editor. Use LoadFromFile() directly.");
            onImageSelected?.Invoke(null);
#endif
        }

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

        void OnDestroy()
        {
            StopPreview();
            if (_webCamTexture != null)
                Destroy(_webCamTexture);
        }
    }
}
