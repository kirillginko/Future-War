using System.Collections.Generic;
using System.Linq;
using ForestSoldier.Player;
using ForestSoldier.Vehicles;
using UnityEditor;
using UnityEngine;

namespace ForestSoldier.EditorTools
{
    /// <summary>
    /// The MH-6 Little Bird, flying on the Base Helicopter Controller asset
    /// (Assets/Helicopter): model import, prefab, helipad, camera and HUD.
    /// </summary>
    public static partial class ForestSoldierSetup
    {
        private const string HeliFbxPath = Root + "/Art/Vehicles/LittleBird/LittleBird.fbx";
        private const string HeliTexPath = Root + "/Art/Vehicles/LittleBird/LittleBird_Texture.png";
        private const string HeliPrefabPath = Root + "/Prefabs/Vehicles/LittleBird.prefab";
        private const string AssetRotorSound = "Assets/Helicopter/Resources/Audio/Helicopter.wav";
        private const int VehicleLayerIndex = 11;
        private static readonly Vector3 HelipadPosition = new Vector3(16f, 0f, 7.5f);
        private const float HelipadYaw = 90f; // nose down range

        private static GameObject heliPrefab;

        private static void BuildHelicopterAssets()
        {
            if (!System.IO.File.Exists(HeliFbxPath))
            {
                Debug.LogWarning($"[ForestSoldierSetup] {HeliFbxPath} missing: run Tools/Blender/import_helicopter.py. No helicopter this build.");
                return;
            }
            EnsureNamedLayer(VehicleLayerIndex, "Vehicle");
            var mats = HelicopterMaterials();
            var mi = (ModelImporter)AssetImporter.GetAtPath(HeliFbxPath);
            mi.globalScale = 1f;
            mi.importAnimation = false;
            mi.animationType = ModelImporterAnimationType.None;
            mi.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
            foreach (var name in new[] { "base", "cargador", "armas", "acces", "computadora", "controles", "helice_1", "helice_2" })
                mi.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), name), mats.body);
            mi.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), "vidrio"), mats.glass);
            mi.SaveAndReimport();
            heliPrefab = BuildLittleBird(AssetDatabase.LoadAssetAtPath<GameObject>(HeliFbxPath), mats.flash);
        }

        private static void EnsureNamedLayer(int index, string layerName)
        {
            var tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            var slot = tagManager.FindProperty("layers").GetArrayElementAtIndex(index);
            if (slot.stringValue == layerName) return;
            if (!string.IsNullOrEmpty(slot.stringValue))
                throw new System.InvalidOperationException($"Layer {index} is already '{slot.stringValue}'.");
            slot.stringValue = layerName;
            tagManager.ApplyModifiedPropertiesWithoutUndo();
        }

        private static (Material body, Material glass, Material flash) HelicopterMaterials()
        {
            var ti = (TextureImporter)AssetImporter.GetAtPath(HeliTexPath);
            if (ti != null)
            {
                ti.filterMode = FilterMode.Point; // a small colour atlas
                ti.mipmapEnabled = false;
                ti.textureCompression = TextureImporterCompression.Uncompressed;
                ti.SaveAndReimport();
            }
            var body = ColorMat("Vehicle_LittleBird", Color.white);
            body.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(HeliTexPath));
            body.SetFloat("_Smoothness", 0.35f);
            body.SetFloat("_Metallic", 0.25f);
            EditorUtility.SetDirty(body);
            var glass = ColorMat("Vehicle_LittleBird_Glass", new Color(0.55f, 0.65f, 0.7f, 0.32f));
            MakeTransparent(glass, 0.92f);
            return (body, glass, flashMat);
        }

        // ---------------------------------------------------------------- sounds

        private static (AudioClip gun, AudioClip rocket) HelicopterSounds()
        {
            const int rate = 44100;
            // Minigun: 20 cracks in exactly 0.5 s (2400 rpm), so the loop is seamless.
            float lp = 0f;
            var gun = WriteClip("MinigunLoop", Normalize(Synth(0.5f, rate, (t, noise) =>
            {
                float n = noise();
                lp += 0.3f * (n - lp);
                float local = t % 0.025f;
                float crack = n * Mathf.Exp(-local / 0.0025f) + lp * 1.8f * Mathf.Exp(-local / 0.008f);
                float thump = Mathf.Sin(2f * Mathf.PI * 95f * local) * Mathf.Exp(-local / 0.01f) * 0.7f;
                float whine = 0.06f * Mathf.Sin(2f * Mathf.PI * 1180f * t);
                return crack + thump + whine;
            }, 61), 0.9f), rate);
            // Rocket: a rushing hiss that rises then tails off.
            float lp2 = 0f;
            var rocket = WriteClip("RocketLaunch", Normalize(Synth(0.9f, rate, (t, noise) =>
            {
                float n = noise();
                lp2 += Mathf.Lerp(0.5f, 0.08f, Mathf.Clamp01(t / 0.6f)) * (n - lp2);
                return lp2 * Mathf.Clamp01(t / 0.02f) * Mathf.Exp(-t / 0.35f);
            }, 62), 0.85f), rate);
            return (gun, rocket);
        }

        // ---------------------------------------------------------------- prefab

        private static Vector3[] VerticesIn(Transform space, MeshFilter mf)
        {
            var m = mf.sharedMesh;
            var v = m.vertices;
            var o = new Vector3[v.Length];
            for (int i = 0; i < v.Length; i++) o[i] = space.InverseTransformPoint(mf.transform.TransformPoint(v[i]));
            return o;
        }

        private static GameObject BuildLittleBird(GameObject model, Material flash)
        {
            var (gunClip, rocketClip) = HelicopterSounds();
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(HeliPrefabPath));
            var root = new GameObject("LittleBird");
            try
            {
                var m = (GameObject)PrefabUtility.InstantiatePrefab(model, root.transform);
                m.name = "Model";
                var main = FindDeep(m.transform, "MainRotor");
                var tail = FindDeep(m.transform, "TailRotor");
                if (main == null || tail == null) throw new System.InvalidOperationException("LittleBird.fbx lacks MainRotor / TailRotor");

                // Nose to +Z: the tail rotor is behind the main rotor.
                Vector3 fwd = Vector3.ProjectOnPlane(main.position - tail.position, Vector3.up);
                m.transform.rotation = Quaternion.FromToRotation(fwd.normalized, Vector3.forward) * m.transform.rotation;

                // Skids on the ground (rotors don't count).
                var meshes = m.GetComponentsInChildren<MeshFilter>()
                    .Where(f => !f.transform.IsChildOf(main) && !f.transform.IsChildOf(tail)).ToArray();
                Bounds b = default;
                bool first = true;
                foreach (var f in meshes)
                foreach (var p in VerticesIn(root.transform, f))
                {
                    if (first) { b = new Bounds(p, Vector3.zero); first = false; }
                    else b.Encapsulate(p);
                }
                m.transform.position -= Vector3.up * b.min.y;
                b.center -= Vector3.up * b.min.y;
                Debug.Log($"[ForestSoldierSetup] Little Bird: {b.size.z:F1} m long, {b.size.x:F1} m wide, {b.size.y:F1} m tall (without rotors)");

                // ---- the asset's controller and rotors
                var rb = root.AddComponent<Rigidbody>();
                rb.mass = 650f;
                rb.linearDamping = 0.13f;     // light drag: it carries speed and coasts a long way (lean back to brake)
                rb.angularDamping = 0.5f;     // the pilot builds and runs down the turn rate itself
                rb.interpolation = RigidbodyInterpolation.Interpolate;
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

                var rotorAudio = root.AddComponent<AudioSource>();
                rotorAudio.clip = AssetDatabase.LoadAssetAtPath<AudioClip>(AssetRotorSound);
                rotorAudio.loop = true;
                rotorAudio.playOnAwake = true;
                rotorAudio.spatialBlend = 1f;
                rotorAudio.rolloffMode = AudioRolloffMode.Linear;
                rotorAudio.minDistance = 8f;
                rotorAudio.maxDistance = 260f;
                rotorAudio.volume = 0.8f;

                HeliRotorController Spinner(Transform rotor, Vector3 worldAxis)
                {
                    var c = rotor.gameObject.AddComponent<HeliRotorController>();
                    // The asset spins about one of the parent's axes: pick the one that matches.
                    Vector3 local = rotor.parent.InverseTransformDirection(worldAxis);
                    Vector3 a = new Vector3(Mathf.Abs(local.x), Mathf.Abs(local.y), Mathf.Abs(local.z));
                    c.RotateAxis = a.x >= a.y && a.x >= a.z ? HeliRotorController.Axis.X : a.y >= a.z ? HeliRotorController.Axis.Y : HeliRotorController.Axis.Z;
                    return c;
                }
                var mainSpin = Spinner(main, Vector3.up);
                var tailSpin = Spinner(tail, root.transform.right);

                var panelGo = new GameObject("ControlPanel");
                panelGo.transform.SetParent(root.transform, false);
                var panel = panelGo.AddComponent<ControlPanel>();
                panel.enabled = false; // reads the legacy Input class; HelicopterPilot raises its event instead

                var hc = root.AddComponent<HelicopterController>();
                hc.HelicopterModel = rb;
                hc.HelicopterSound = rotorAudio;
                hc.ControlPanel = panel;
                hc.MainRotorController = mainSpin;
                hc.SubRotorController = tailSpin;
                // Heavy, with momentum (the asset's defaults are 3 / 10 / 20 / 30 / 100 / 1.5 / 1.3).
                // Top speed is about 125 km/h: the push along the nose plus the tilted lift, against the drag.
                // The asset works out the lean; HelicopterPilot turns the airframe towards it with inertia.
                hc.TurnForce = 0f;            // its bank-turn torque: HelicopterPilot turns the nose instead
                hc.ForwardForce = 0f;      // all the push comes from leaning the lift (HelicopterPilot weighs it down)
                hc.ForwardTiltForce = 40f;    // pitches well nose-down to put the guns on targets below
                hc.TurnTiltForce = 30f;
                hc.TiltResponse = 2f;       // the lean the stick asks for builds over about a second
                hc.EffectiveHeight = 180f;
                hc.turnTiltForcePercent = 1.6f;
                hc.turnForcePercent = 0f;     // its pedal torque: likewise

                // ---- colliders: cabin, tail boom, skids (rotors pass through things)
                float nose = b.max.z, back = b.min.z;
                void Box(string n, Vector3 c, Vector3 s)
                {
                    var g = new GameObject(n);
                    g.transform.SetParent(root.transform, false);
                    g.transform.localPosition = c;
                    g.AddComponent<BoxCollider>().size = s;
                }
                Box("Cabin", new Vector3(0f, 1.45f, nose - 1.7f), new Vector3(2.0f, 1.9f, 3.2f));
                Box("TailBoom", new Vector3(0f, 1.7f, (nose - 3.3f + back) * 0.5f), new Vector3(0.45f, 0.5f, nose - 3.3f - back));
                Box("Skid_L", new Vector3(-1.0f, 0.1f, nose - 1.8f), new Vector3(0.15f, 0.2f, 3.0f));
                Box("Skid_R", new Vector3(1.0f, 0.1f, nose - 1.8f), new Vector3(0.15f, 0.2f, 3.0f));
                Box("Pods", new Vector3(0f, 0.55f, nose - 1.1f), new Vector3(3.6f, 0.5f, 1.8f));

                // ---- muzzles and tubes, measured from the weapons mesh ("armas")
                var arms = m.GetComponentsInChildren<MeshRenderer>().First(r => r.sharedMaterials.Any(x => x != null) &&
                    r.GetComponent<MeshFilter>().sharedMesh.vertexCount > 7000 && r.bounds.size.x > 3f);
                var pts = VerticesIn(root.transform, arms.GetComponent<MeshFilter>());
                Transform Mount(string n, float sign, float lo, float hi)
                {
                    var cl = pts.Where(p => p.x * sign > 0 && Mathf.Abs(p.x) >= lo && Mathf.Abs(p.x) < hi).ToArray();
                    float front = cl.Max(p => p.z);
                    var tip = cl.Where(p => p.z > front - 0.03f).ToArray();
                    Vector3 c = tip.Aggregate(Vector3.zero, (a, p) => a + p) / tip.Length + Vector3.forward * 0.05f;
                    var t = new GameObject(n).transform;
                    t.SetParent(root.transform, false);
                    t.localPosition = c;
                    return t;
                }
                var guns = new[] { Mount("Minigun_L", -1f, 0.8f, 1.35f), Mount("Minigun_R", 1f, 0.8f, 1.35f) };
                var tubes = new[] { Mount("Rockets_L", -1f, 1.35f, 2.1f), Mount("Rockets_R", 1f, 1.35f, 2.1f) };
                var flashes = new List<Renderer>();
                foreach (var g in guns)
                {
                    var q = Part(PrimitiveType.Quad, "Flash", g, new Vector3(0f, 0f, 0.15f), new Vector3(0.45f, 0.45f, 1f), flash, false);
                    q.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    flashes.Add(q.GetComponent<Renderer>());
                }
                Debug.Log($"[ForestSoldierSetup] Little Bird guns at {guns[0].localPosition} / {guns[1].localPosition}, rockets at {tubes[0].localPosition} / {tubes[1].localPosition}");

                // ---- seat and door
                var glassR = m.GetComponentsInChildren<MeshRenderer>().FirstOrDefault(r => r.sharedMaterial != null && r.sharedMaterial.name.Contains("Glass"));
                Vector3 cockpit = glassR != null ? root.transform.InverseTransformPoint(glassR.bounds.center) : new Vector3(0f, 1.3f, nose - 1.5f);
                var seat = new GameObject("Seat").transform;
                seat.SetParent(root.transform, false);
                seat.localPosition = new Vector3(cockpit.x, 0.7f, cockpit.z);
                var exit = new GameObject("Door").transform;
                exit.SetParent(root.transform, false);
                exit.localPosition = new Vector3(2.8f, 0.1f, cockpit.z);

                // ---- rocket round: a slim body, a hot glow, a smoke trail
                var rocket = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                rocket.name = "Rocket70mm";
                Object.DestroyImmediate(rocket.GetComponent<Collider>());
                rocket.transform.localScale = new Vector3(0.07f, 0.4f, 0.07f);
                var holder = new GameObject("Rocket70mm");
                rocket.transform.SetParent(holder.transform, false);
                rocket.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                rocket.GetComponent<Renderer>().sharedMaterial = gunMetal;
                var glow = Part(PrimitiveType.Quad, "Glow", holder.transform, new Vector3(0f, 0f, -0.42f), new Vector3(0.35f, 0.35f, 1f), flash, false);
                glow.AddComponent<FaceCamera>();
                var trail = holder.AddComponent<TrailRenderer>();
                trail.time = 0.9f;
                trail.widthMultiplier = 0.32f;
                trail.widthCurve = new AnimationCurve(new Keyframe(0f, 0.25f), new Keyframe(0.25f, 1f), new Keyframe(1f, 1.6f));
                trail.sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>(Root + "/Materials/FX_Smoke.mat");
                trail.startColor = new Color(0.85f, 0.83f, 0.8f, 0.7f);
                trail.endColor = new Color(0.7f, 0.7f, 0.7f, 0f);
                trail.minVertexDistance = 0.4f;
                trail.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                var rocketPrefab = PrefabUtility.SaveAsPrefabAsset(holder, Root + "/Prefabs/Vehicles/Rocket70mm.prefab");
                Object.DestroyImmediate(holder);

                // ---- our parts: pilot, weapons
                var gunAudio = root.AddComponent<AudioSource>();
                gunAudio.clip = gunClip;
                gunAudio.loop = true;
                gunAudio.playOnAwake = false;
                gunAudio.spatialBlend = 0.6f;
                gunAudio.volume = 0.75f;
                var rocketAudio = root.AddComponent<AudioSource>();
                rocketAudio.playOnAwake = false;
                rocketAudio.spatialBlend = 0.6f;

                var pilot = root.AddComponent<HelicopterPilot>();
                var so = new SerializedObject(pilot);
                so.FindProperty("controller").objectReferenceValue = hc;
                so.FindProperty("controlPanel").objectReferenceValue = panel;
                so.FindProperty("seat").objectReferenceValue = seat;
                so.FindProperty("exitPoint").objectReferenceValue = exit;
                so.ApplyModifiedPropertiesWithoutUndo();

                var weapons = root.AddComponent<HelicopterWeapons>();
                so = new SerializedObject(weapons);
                so.FindProperty("pilot").objectReferenceValue = pilot;
                SetArray(so.FindProperty("gunMuzzles"), guns, (p, v) => p.objectReferenceValue = v);
                SetArray(so.FindProperty("rocketTubes"), tubes, (p, v) => p.objectReferenceValue = v);
                SetArray(so.FindProperty("gunFlashes"), flashes.ToArray(), (p, v) => p.objectReferenceValue = v);
                so.FindProperty("hitMask").intValue = HitMask;
                so.FindProperty("gunAudio").objectReferenceValue = gunAudio;
                so.FindProperty("rocketPrefab").objectReferenceValue = rocketPrefab;
                so.FindProperty("explosionPrefab").objectReferenceValue = explosionPrefab;
                so.FindProperty("rocketAudio").objectReferenceValue = rocketAudio;
                so.FindProperty("rocketLaunch").objectReferenceValue = rocketClip;
                // Chunks the rockets tear off what they hit (walls and rocks in their own material, dirt off the ground).
                var debris = root.AddComponent<ForestSoldier.Combat.DebrisBurst>();
                var dso = new SerializedObject(debris);
                dso.FindProperty("fallback").objectReferenceValue = ColorMat("Debris_Dirt", new Color(0.33f, 0.26f, 0.18f));
                dso.ApplyModifiedPropertiesWithoutUndo();
                so.FindProperty("debris").objectReferenceValue = debris;
                so.ApplyModifiedPropertiesWithoutUndo();

                foreach (var t in root.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = VehicleLayerIndex;
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(HeliPrefabPath));
                return PrefabUtility.SaveAsPrefabAsset(root, HeliPrefabPath);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        // ---------------------------------------------------------------- scene

        private static void PlaceHelicopter(Camera cam)
        {
            if (heliPrefab == null) return;
            var pad = new GameObject("Helipad").transform;
            pad.position = HelipadPosition;
            var mark = ColorMat("Proto_TargetMark", new Color(0.95f, 0.93f, 0.85f));
            var disc = Part(PrimitiveType.Cylinder, "Pad", pad, new Vector3(0f, 0.01f, 0f), new Vector3(8.5f, 0.01f, 8.5f),
                ColorMat("Proto_Pad", new Color(0.28f, 0.29f, 0.3f)), false);
            disc.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            Part(PrimitiveType.Cube, "H_Left", pad, new Vector3(-1.1f, 0.025f, 0f), new Vector3(0.45f, 0.01f, 3.4f), mark, false);
            Part(PrimitiveType.Cube, "H_Right", pad, new Vector3(1.1f, 0.025f, 0f), new Vector3(0.45f, 0.01f, 3.4f), mark, false);
            Part(PrimitiveType.Cube, "H_Bar", pad, new Vector3(0f, 0.025f, 0f), new Vector3(2.2f, 0.01f, 0.45f), mark, false);
            pad.rotation = Quaternion.Euler(0f, HelipadYaw, 0f);

            var heli = (GameObject)PrefabUtility.InstantiatePrefab(heliPrefab);
            heli.transform.SetPositionAndRotation(HelipadPosition + Vector3.up * 0.05f, Quaternion.Euler(0f, HelipadYaw, 0f));

            var chase = cam.GetComponent<HelicopterCamera>() ?? cam.gameObject.AddComponent<HelicopterCamera>();
            var so = new SerializedObject(chase);
            so.FindProperty("collisionMask").intValue = ~(1 << PlayerLayerIndex) & ~(1 << 2) & ~(1 << NpcLayerIndex) & ~(1 << VehicleLayerIndex);
            so.ApplyModifiedPropertiesWithoutUndo();
            so = new SerializedObject(heli.GetComponent<HelicopterPilot>());
            so.FindProperty("chaseCamera").objectReferenceValue = chase;
            so.ApplyModifiedPropertiesWithoutUndo();
            so = new SerializedObject(heli.GetComponent<HelicopterWeapons>());
            so.FindProperty("chaseCamera").objectReferenceValue = chase;
            so.ApplyModifiedPropertiesWithoutUndo();

            var hudGo = new GameObject("Helicopter HUD");
            var hud = hudGo.AddComponent<HelicopterHUD>();
            so = new SerializedObject(hud);
            so.FindProperty("pilot").objectReferenceValue = heli.GetComponent<HelicopterPilot>();
            so.FindProperty("weapons").objectReferenceValue = heli.GetComponent<HelicopterWeapons>();
            so.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
