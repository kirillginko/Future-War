using UnityEngine;

namespace ForestSoldier.Vehicles
{
    /// <summary>Keeps a quad facing the main camera (the glow on a rocket motor).</summary>
    public class FaceCamera : MonoBehaviour
    {
        private Transform cam;

        private void LateUpdate()
        {
            if (cam == null)
            {
                var c = Camera.main;
                if (c == null) return;
                cam = c.transform;
            }
            transform.rotation = Quaternion.LookRotation(transform.position - cam.position);
        }
    }
}
