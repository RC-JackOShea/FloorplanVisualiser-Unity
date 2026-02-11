using System;
using UnityEngine;

namespace FloorplanVectoriser.Inference
{
    /// <summary>
    /// Tensor manipulation helpers for post-inference processing.
    /// Handles channel splitting, softmax, and argmax operations.
    /// </summary>
    public static class TensorUtils
    {
        /// <summary>
        /// Split a flat float array from model output (shape 1x44xHxW) into three 3D arrays.
        /// The model outputs 44 channels: [21 heatmaps, 12 rooms, 11 icons].
        /// </summary>
        public static void SplitChannels(float[] flatOutput, int height, int width,
            out float[,,] heatmaps, out float[,,] rooms, out float[,,] icons)
        {
            int heatmapChannels = 21;
            int roomChannels = 12;
            int iconChannels = 11;

            heatmaps = new float[heatmapChannels, height, width];
            rooms = new float[roomChannels, height, width];
            icons = new float[iconChannels, height, width];

            // Layout: [batch=1][channel][height][width] → flat index = c*H*W + h*W + w
            int hw = height * width;
            for (int c = 0; c < heatmapChannels; c++)
            {
                int srcOffset = c * hw;
                for (int h = 0; h < height; h++)
                    for (int w = 0; w < width; w++)
                        heatmaps[c, h, w] = flatOutput[srcOffset + h * width + w];
            }

            for (int c = 0; c < roomChannels; c++)
            {
                int srcOffset = (heatmapChannels + c) * hw;
                for (int h = 0; h < height; h++)
                    for (int w = 0; w < width; w++)
                        rooms[c, h, w] = flatOutput[srcOffset + h * width + w];
            }

            for (int c = 0; c < iconChannels; c++)
            {
                int srcOffset = (heatmapChannels + roomChannels + c) * hw;
                for (int h = 0; h < height; h++)
                    for (int w = 0; w < width; w++)
                        icons[c, h, w] = flatOutput[srcOffset + h * width + w];
            }
        }

        /// <summary>
        /// Apply softmax along the channel axis (axis 0) of a [C,H,W] array in-place.
        /// </summary>
        public static void SoftmaxChannelAxis(float[,,] data)
        {
            int C = data.GetLength(0);
            int H = data.GetLength(1);
            int W = data.GetLength(2);

            for (int h = 0; h < H; h++)
            {
                for (int w = 0; w < W; w++)
                {
                    // Numerically stable softmax: subtract max first
                    float max = float.NegativeInfinity;
                    for (int c = 0; c < C; c++)
                        if (data[c, h, w] > max) max = data[c, h, w];

                    float sum = 0f;
                    for (int c = 0; c < C; c++)
                    {
                        data[c, h, w] = Mathf.Exp(data[c, h, w] - max);
                        sum += data[c, h, w];
                    }

                    if (sum > 0)
                        for (int c = 0; c < C; c++)
                            data[c, h, w] /= sum;
                }
            }
        }

        /// <summary>
        /// Argmax along the channel axis, returning an int[H,W] array of class indices.
        /// </summary>
        public static int[,] ArgmaxChannelAxis(float[,,] data)
        {
            int C = data.GetLength(0);
            int H = data.GetLength(1);
            int W = data.GetLength(2);
            int[,] result = new int[H, W];

            for (int h = 0; h < H; h++)
            {
                for (int w = 0; w < W; w++)
                {
                    int best = 0;
                    float bestVal = data[0, h, w];
                    for (int c = 1; c < C; c++)
                    {
                        if (data[c, h, w] > bestVal)
                        {
                            bestVal = data[c, h, w];
                            best = c;
                        }
                    }
                    result[h, w] = best;
                }
            }
            return result;
        }
    }
}
