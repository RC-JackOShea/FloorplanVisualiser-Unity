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
        
        [Tooltip("Use CPU backend for larger models (1024+) to avoid GPU thread group limits")]
        [SerializeField] private bool forceCPU = false;

        /// <summary>True while inference is running.</summary>
        public bool IsRunning { get; private set; }

        void Awake()
        {
            if (modelAsset == null)
            {
                Debug.LogError("FloorplanInference: No model asset assigned.");
                return;
            }
            _model = ModelLoader.Load(modelAsset);
            
            // For larger models (1024+), GPU compute can hit thread group limits
            // Use CPU backend in those cases, or when explicitly requested
            bool useCPU = forceCPU || inputSize > 512;
            
            if (useCPU)
            {
                Debug.Log($"Using CPU backend for inference (inputSize: {inputSize})");
                _worker = new Worker(_model, BackendType.CPU);
            }
            else
            {
                // Prefer GPU for 512 and smaller; fall back to CPU if unavailable
                try
                {
                    _worker = new Worker(_model, BackendType.GPUCompute);
                }
                catch
                {
                    Debug.LogWarning("GPU compute not available, falling back to CPU.");
                    _worker = new Worker(_model, BackendType.CPU);
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

            // 2. Run inference
            _worker.Schedule(inputTensor);
            
            // Allow time for inference to complete
            // CPU backend needs more time for larger models
            if (inputSize > 512)
            {
                // For larger models on CPU, yield multiple frames
                for (int i = 0; i < 10; i++)
                    yield return null;
            }
            else
            {
                yield return null; // Single frame for GPU work
            }

            // 3. Read output
            var outputTensor = _worker.PeekOutput() as Tensor<float>;
            float[] outputData = outputTensor.DownloadToArray();

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
