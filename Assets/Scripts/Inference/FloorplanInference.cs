using System;
using System.Collections;
using UnityEngine;
using Unity.InferenceEngine;

namespace FloorplanVectoriser.Inference
{
    /// <summary>
    /// Loads the ONNX floorplan model and runs inference via Unity Inference Engine.
    /// Attach to a GameObject in the scene and assign the ONNX ModelAsset in the inspector.
    /// </summary>
    public class FloorplanInference : MonoBehaviour
    {
        [SerializeField] private ModelAsset modelAsset;
        private Model _model;
        private Worker _worker;

        [SerializeField] private int inputSize = 512;
        
        [Tooltip("Use CPU backend to avoid GPU contention (recommended for mobile)")]
        [SerializeField] private bool forceCPU = false;
        
        [Tooltip("Layers to execute per frame when using iterative scheduling (lower = less GPU pressure)")]
        [SerializeField] private int layersPerFrame = 20;

        /// <summary>True while inference is running.</summary>
        public bool IsRunning { get; private set; }
        
        private bool _useCPU;

        void Awake()
        {
            if (modelAsset == null)
            {
                Debug.LogError("FloorplanInference: No model asset assigned.");
                return;
            }
            _model = ModelLoader.Load(modelAsset);
            
            // Force CPU on Android to avoid GPU timeout (QUEUE_BUFFER_TIMEOUT)
            // Mobile GPUs can't handle heavy ML inference without blocking the rendering pipeline
            bool isMobile = Application.platform == RuntimePlatform.Android || 
                           Application.platform == RuntimePlatform.IPhonePlayer;
            
            // Use CPU for: mobile devices, large models (1024+), or when explicitly requested
            _useCPU = forceCPU || isMobile || inputSize > 512;
            
            if (_useCPU)
            {
                Debug.Log($"Using CPU backend for inference (inputSize: {inputSize}, mobile: {isMobile})");
                _worker = new Worker(_model, BackendType.CPU);
            }
            else
            {
                // Prefer GPU for desktop with smaller models; fall back to CPU if unavailable
                try
                {
                    _worker = new Worker(_model, BackendType.GPUCompute);
                    Debug.Log("Using GPU backend for inference");
                }
                catch
                {
                    Debug.LogWarning("GPU compute not available, falling back to CPU.");
                    _worker = new Worker(_model, BackendType.CPU);
                    _useCPU = true;
                }
            }
        }

        /// <summary>
        /// Run inference on a captured floorplan image.
        /// Results are delivered via the onComplete callback as (heatmaps, rooms, icons).
        /// </summary>
        public void RunInference(Texture2D image, Action<float[,,], float[,,], float[,,]> onComplete)
        {
            if (IsRunning)
            {
                Debug.LogWarning("Inference already in progress.");
                return;
            }
            StartCoroutine(InferenceCoroutine(image, onComplete));
        }

        IEnumerator InferenceCoroutine(Texture2D image, Action<float[,,], float[,,], float[,,]> onComplete)
        {
            IsRunning = true;

            // 1. Prepare input tensor: (1, 3, inputSize, inputSize), normalized [0,1]
            //    Texture2D pixels are bottom-to-top in Unity, so we flip Y.
            var inputTensor = new Tensor<float>(new TensorShape(1, 3, inputSize, inputSize));
            Color[] pixels = image.GetPixels();
            int w = image.width;
            int h = image.height;

            for (int y = 0; y < inputSize; y++)
            {
                for (int x = 0; x < inputSize; x++)
                {
                    // Map to source pixel (image may already be 512x512)
                    int srcX = x * w / inputSize;
                    int srcY = (inputSize - 1 - y) * h / inputSize; // flip Y
                    Color pixel = pixels[srcY * w + srcX];

                    inputTensor[0, 0, y, x] = pixel.r;
                    inputTensor[0, 1, y, x] = pixel.g;
                    inputTensor[0, 2, y, x] = pixel.b;
                }
            }

            // 2. Run inference using iterative scheduling to spread work across frames
            // This prevents blocking the main thread and avoids GPU timeout on mobile
            var enumerator = _worker.ScheduleIterable(inputTensor);
            int layersThisFrame = 0;
            int totalLayers = 0;
            
            while (enumerator.MoveNext())
            {
                totalLayers++;
                layersThisFrame++;
                if (layersThisFrame >= layersPerFrame)
                {
                    layersThisFrame = 0;
                    yield return null; // Yield to let rendering happen
                }
            }
            
            Debug.Log($"Inference complete: executed {totalLayers} layers");
            
            // Ensure we yield at least once after scheduling completes
            yield return null;

            // 3. Read output
            var outputTensor = _worker.PeekOutput() as Tensor<float>;
            float[] outputData;
            
            if (_useCPU)
            {
                // CPU backend: data is already on CPU, direct download is fast
                outputData = outputTensor.DownloadToArray();
            }
            else
            {
                // GPU backend: use async readback to avoid blocking
                outputTensor.ReadbackRequest();
                while (!outputTensor.IsReadbackRequestDone())
                    yield return null;
                outputData = outputTensor.DownloadToArray();
            }

            inputTensor.Dispose();

            // 4. Split channels and apply softmax
            TensorUtils.SplitChannels(outputData, inputSize, inputSize,
                out float[,,] heatmaps, out float[,,] rooms, out float[,,] icons);

            // Sigmoid is already applied in the ONNX model for heatmaps (channels 0-20).
            // Apply softmax to rooms and icons.
            TensorUtils.SoftmaxChannelAxis(rooms);
            TensorUtils.SoftmaxChannelAxis(icons);

            IsRunning = false;
            onComplete?.Invoke(heatmaps, rooms, icons);
        }

        void OnDestroy()
        {
            _worker?.Dispose();
        }
    }
}
