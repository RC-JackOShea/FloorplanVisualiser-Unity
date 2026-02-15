using UnityEngine;

namespace FloorplanVectoriser.App
{
    /// <summary>
    /// Attached to wall mesh GameObjects to store endpoint positions
    /// for wall length calculation during measurement calibration.
    /// </summary>
    public class WallInfo : MonoBehaviour
    {
        public Vector3 endpointA;
        public Vector3 endpointB;

        /// <summary>Length of the wall in local (pre-scale) space.</summary>
        public float LocalLength => Vector3.Distance(endpointA, endpointB);
    }
}
