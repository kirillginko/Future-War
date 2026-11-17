using UnityEngine;

namespace ForestSoldier.Combat
{
    /// <summary>Slides a target back and forth along its local X. Stops while the target is down.</summary>
    public class TargetMover : MonoBehaviour
    {
        [SerializeField] private float halfTravel = 3f;
        [SerializeField] private float speed = 1.5f;
        [SerializeField] private DamageableTarget target;

        private Vector3 origin;
        private float phase;

        private void Awake()
        {
            origin = transform.localPosition;
            if (target == null) target = GetComponent<DamageableTarget>();
        }

        private void Update()
        {
            if (target != null && !target.IsAlive) return;
            if (halfTravel <= 0f) return;
            phase += speed * Time.deltaTime / halfTravel;
            // smooth turnarounds: position follows a sine
            transform.localPosition = origin + transform.localRotation * Vector3.right * (Mathf.Sin(phase) * halfTravel);
        }
    }
}
