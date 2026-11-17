using System.Collections.Generic;
using UnityEngine;

namespace ForestSoldier.NPC
{
    /// <summary>
    /// Turns a walker's skeleton into a physics ragdoll on demand (blast deaths)
    /// and puts it back afterwards (reset).
    ///
    /// The soldier rig hangs its hands, feet, IK poles and gun off the root bone,
    /// not off the limbs. For the ragdoll those are re-parented onto the bones
    /// that should carry them (hands onto forearms, feet onto shins, the gun
    /// onto the right hand), so they flail with the body. Every change is
    /// recorded and undone by <see cref="Restore"/>.
    /// </summary>
    public class WalkerRagdoll : MonoBehaviour
    {
        [SerializeField] private float totalMass = 70f;
        [Tooltip("Seconds after which a settled body is frozen to stop costing physics time.")]
        [SerializeField] private float freezeAfter = 8f;
        [SerializeField] private int layer = 10; // NPC: collides with the world, not the player or other walkers

        private struct Part
        {
            public string Bone, Parent, Child;
            public float Radius, MassShare;
            public bool Sphere;
        }

        // Bone, joint parent, the bone that marks its far end, radius (m), mass share.
        private static readonly Part[] Parts =
        {
            new Part { Bone = "Spinelower", Parent = null, Child = "Spineupper", Radius = 0.15f, MassShare = 0.22f },
            new Part { Bone = "Spineupper", Parent = "Spinelower", Child = "Neck", Radius = 0.17f, MassShare = 0.24f },
            new Part { Bone = "head", Parent = "Spineupper", Child = null, Radius = 0.12f, MassShare = 0.08f, Sphere = true },
            new Part { Bone = "ArmUpper.L", Parent = "Spineupper", Child = "ArmLower.L", Radius = 0.055f, MassShare = 0.035f },
            new Part { Bone = "ArmLower.L", Parent = "ArmUpper.L", Child = "HandWrist.L", Radius = 0.05f, MassShare = 0.03f },
            new Part { Bone = "ArmUpper.R", Parent = "Spineupper", Child = "ArmLower.R", Radius = 0.055f, MassShare = 0.035f },
            new Part { Bone = "ArmLower.R", Parent = "ArmUpper.R", Child = "HandWrist.R", Radius = 0.05f, MassShare = 0.03f },
            new Part { Bone = "LegThigh.L", Parent = "Spinelower", Child = "LegShin.L", Radius = 0.08f, MassShare = 0.1f },
            new Part { Bone = "LegShin.L", Parent = "LegThigh.L", Child = "Foot.L", Radius = 0.065f, MassShare = 0.05f },
            new Part { Bone = "LegThigh.R", Parent = "Spinelower", Child = "LegShin.R", Radius = 0.08f, MassShare = 0.1f },
            new Part { Bone = "LegShin.R", Parent = "LegThigh.R", Child = "Foot.R", Radius = 0.065f, MassShare = 0.05f },
        };

        // Bones hanging off the root that should ride on a limb while ragdolled.
        private static readonly (string bone, string carrier)[] Carry =
        {
            ("HandWrist.L", "ArmLower.L"), ("HandWrist.R", "ArmLower.R"),
            ("Footctrl.L", "LegShin.L"), ("Footctrl.R", "LegShin.R"),
            ("PoleArm.L", "Spineupper"), ("PoleArm.R", "Spineupper"),
            ("Poleleg.L", "Spinelower"), ("Poleleg.R", "Spinelower"),
            ("SMGBone", "HandWrist.R"), ("SHOTGUNbone", "Spinelower"),
        };

        private struct Saved
        {
            public Transform T, Parent;
            public Vector3 Pos;
            public Quaternion Rot;
        }

        private Animator animator;
        private SkinnedMeshRenderer[] renderers;
        private Dictionary<string, Transform> bones;
        private readonly List<Saved> saved = new List<Saved>();
        private readonly List<Rigidbody> bodies = new List<Rigidbody>();
        private readonly List<Component> added = new List<Component>();
        private float since;
        private bool frozen;

        public bool Active { get; private set; }
        public IReadOnlyList<Rigidbody> Bodies => bodies;
        public Transform Chest => bones != null && bones.TryGetValue("Spineupper", out var t) ? t : null;

        private void Awake()
        {
            animator = GetComponentInChildren<Animator>();
            renderers = GetComponentsInChildren<SkinnedMeshRenderer>();
        }

        private void IndexBones()
        {
            if (bones != null) return;
            bones = new Dictionary<string, Transform>();
            foreach (var t in animator.GetComponentsInChildren<Transform>(true)) bones[t.name] = t;
        }

        /// <summary>Go limp and fly: velocity away from the blast, scaled by distance, with spin.</summary>
        public void Activate(Vector3 blastCentre, float impulse)
        {
            if (Active || animator == null) return;
            IndexBones();
            Active = true;
            frozen = false;
            since = 0f;
            animator.enabled = false;
            foreach (var r in renderers) r.updateWhenOffscreen = true; // bones fly outside the animated bounds

            // Remember the whole skeleton's pose and parents, then re-seat the root-hung bones.
            saved.Clear();
            foreach (var t in bones.Values) saved.Add(new Saved { T = t, Parent = t.parent, Pos = t.localPosition, Rot = t.localRotation });
            foreach (var (bone, carrier) in Carry)
                if (bones.TryGetValue(bone, out var b) && bones.TryGetValue(carrier, out var c)) b.SetParent(c, true);

            bodies.Clear();
            added.Clear();
            var byBone = new Dictionary<string, Rigidbody>();
            foreach (var p in Parts)
            {
                if (!bones.TryGetValue(p.Bone, out var t)) continue;
                t.gameObject.layer = layer;
                float s = Mathf.Max(1e-4f, t.lossyScale.x);
                if (p.Sphere || p.Child == null || !bones.TryGetValue(p.Child, out var end))
                {
                    var sc = t.gameObject.AddComponent<SphereCollider>();
                    sc.radius = p.Radius / s;
                    sc.center = t.InverseTransformPoint(t.position + Vector3.up * 0.06f);
                    added.Add(sc);
                }
                else
                {
                    Vector3 local = t.InverseTransformPoint(end.position);
                    var cc = t.gameObject.AddComponent<CapsuleCollider>();
                    Vector3 a = new Vector3(Mathf.Abs(local.x), Mathf.Abs(local.y), Mathf.Abs(local.z));
                    cc.direction = a.x > a.y && a.x > a.z ? 0 : a.y > a.z ? 1 : 2;
                    cc.center = local * 0.5f;
                    cc.radius = p.Radius / s;
                    cc.height = local.magnitude + 2f * cc.radius;
                    added.Add(cc);
                }
                var rb = t.gameObject.AddComponent<Rigidbody>();
                rb.mass = totalMass * p.MassShare;
                rb.linearDamping = 0.05f;
                rb.angularDamping = 0.6f;
                rb.interpolation = RigidbodyInterpolation.Interpolate;
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
                bodies.Add(rb);
                added.Add(rb);
                byBone[p.Bone] = rb;
            }
            foreach (var p in Parts)
            {
                if (p.Parent == null || !byBone.TryGetValue(p.Bone, out var rb) || !byBone.TryGetValue(p.Parent, out var parent)) continue;
                var j = rb.gameObject.AddComponent<CharacterJoint>();
                j.connectedBody = parent;
                j.enablePreprocessing = false;
                j.lowTwistLimit = new SoftJointLimit { limit = -35f };
                j.highTwistLimit = new SoftJointLimit { limit = 35f };
                j.swing1Limit = new SoftJointLimit { limit = p.Bone == "head" ? 30f : 60f };
                j.swing2Limit = new SoftJointLimit { limit = p.Bone == "head" ? 30f : 45f };
                added.Add(j);
            }

            // Thrown away from the blast, mostly up, harder for parts nearer it, tumbling.
            foreach (var rb in bodies)
            {
                Vector3 away = rb.position - blastCentre;
                away.y = Mathf.Max(away.y, 0.2f);
                float near = 1f / (1f + away.magnitude * 0.15f);
                Vector3 v = away.normalized * impulse * near + Vector3.up * impulse * 0.4f;
                rb.linearVelocity = v;
                rb.angularVelocity = Random.insideUnitSphere * impulse * 0.8f;
            }
        }

        private void FixedUpdate()
        {
            if (!Active || frozen) return;
            since += Time.fixedDeltaTime;
            if (since < freezeAfter) return;
            foreach (var rb in bodies)
                if (rb != null && rb.linearVelocity.sqrMagnitude > 0.04f && since < freezeAfter * 2f) return;
            frozen = true; // settled: stop simulating, keep the pose
            foreach (var rb in bodies) if (rb != null) rb.isKinematic = true;
        }

        /// <summary>Back to an animated walker: components removed, bones re-parented and posed as before.</summary>
        public void Restore()
        {
            if (!Active) return;
            Active = false;
            // Joints first: they need their rigidbodies to exist while they go.
            for (int i = added.Count - 1; i >= 0; i--)
                if (added[i] is Joint) DestroyImmediate(added[i]);
            for (int i = added.Count - 1; i >= 0; i--)
                if (added[i] != null && !(added[i] is Joint)) DestroyImmediate(added[i]);
            added.Clear();
            bodies.Clear();
            foreach (var s in saved)
            {
                s.T.SetParent(s.Parent, false);
                s.T.SetLocalPositionAndRotation(s.Pos, s.Rot);
            }
            foreach (var r in renderers) r.updateWhenOffscreen = false;
            animator.enabled = true;
        }
    }
}
