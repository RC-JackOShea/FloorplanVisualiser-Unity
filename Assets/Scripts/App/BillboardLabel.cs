using TMPro;
using UnityEngine;

namespace FloorplanVectoriser.App
{
    /// <summary>
    /// Rotates a transform every frame to face the main camera.
    /// Attach to a TextMeshPro world-space object for billboard labels.
    /// </summary>
    public class BillboardLabel : MonoBehaviour
    {
        void Start()
        {
            var tmp = GetComponent<TextMeshPro>();
            if (tmp != null)
            {
                tmp.color = Color.white;
                tmp.fontMaterial.EnableKeyword("OUTLINE_ON");
                tmp.fontMaterial.SetFloat(ShaderUtilities.ID_OutlineWidth, 0.2f);
                tmp.fontMaterial.SetColor(ShaderUtilities.ID_OutlineColor, Color.black);
            }
        }

        void LateUpdate()
        {
            var cam = Camera.main;
            if (cam == null) return;
            transform.forward = cam.transform.forward;
        }
    }
}
