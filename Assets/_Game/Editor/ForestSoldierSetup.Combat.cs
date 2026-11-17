using System.Collections.Generic;
using System.IO;
using System.Linq;
using ForestSoldier.Combat;
using ForestSoldier.Player;
using ForestSoldier.UI;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace ForestSoldier.EditorTools
{
    /// <summary>Milestone 2: weapon, effects, sounds, targets, range layout and HUD.</summary>
    public static partial class ForestSoldierSetup
    {
        private const string AudioDir = Root + "/Audio";
        private const string WeaponArtDir = Root + "/Art/Weapons";
        private const string OverlayControllerPath = Root + "/Animations/SoldierOverlay.controller";
        private const string TargetPrefabPath = Root + "/Prefabs/Targets/TargetDummy.prefab";
        private const string ImpactPrefabPath = Root + "/Prefabs/Player/ImpactBurst.prefab";
        private const float FiringLineX = 16f;

        private static readonly string[] AimRigRoots = { "Spineupper", "HandWrist.L", "HandWrist.R", "PoleArm.L", "PoleArm.R", "SMGBone" };

        private static AudioClip shotClip, dryClip, clackClip, pingClip;
        private static Material flashMat, tracerMat, impactMat, targetMat, targetMarkMat;
        private static ParticleSystem impactPrefab;
        private static AnimatorController overlayController;
        private static AnimationClip reloadClip;
        private static GameObject targetPrefab;

        private static int HitMask => ~(1 << PlayerLayerIndex) & ~(1 << 2);

        private static void BuildCombatAssets(Dictionary<string, AnimationClip> clips)
        {
            BuildSounds();
            BuildEffectMaterials();
            BuildImpactPrefab();
            reloadClip = clips["Reload(1)"];
            BuildOverlayController(clips["Rifle Aiming Idle"], reloadClip);
            BuildTargetPrefab();
        }

        // ---------------------------------------------------------------- sounds

        private static void BuildSounds()
        {
            const int rate = 44100;
            var rng = new System.Random(7);
            float Noise() => (float)(rng.NextDouble() * 2.0 - 1.0);

            float[] Gunshot()
            {
                int n = (int)(0.5f * rate);
                var s = new float[n];
                float lp = 0f, phase = 0f;
                for (int i = 0; i < n; i++)
                {
                    float t = i / (float)rate;
                    float bright = Mathf.Lerp(0.85f, 0.12f, Mathf.Clamp01(t / 0.12f));
                    lp += bright * (Noise() - lp);
                    float env = Mathf.Exp(-t / 0.045f) + 0.25f * Mathf.Exp(-t / 0.22f);
                    float f = Mathf.Lerp(130f, 42f, Mathf.Clamp01(t / 0.15f));
                    phase += 2f * Mathf.PI * f / rate;
                    float thump = Mathf.Sin(phase) * Mathf.Exp(-t / 0.08f) * 0.9f;
                    float attack = Mathf.Clamp01(t / 0.0006f);
                    s[i] = attack * (lp * env * 1.6f + thump);
                }
                return Normalize(s, 0.95f);
            }

            float[] Click(float length, float tone, float decay)
            {
                int n = (int)(length * rate);
                var s = new float[n];
                float lp = 0f;
                for (int i = 0; i < n; i++)
                {
                    float t = i / (float)rate;
                    lp += 0.45f * (Noise() - lp);
                    s[i] = lp * Mathf.Exp(-t / decay) + 0.5f * Mathf.Sin(2f * Mathf.PI * tone * t) * Mathf.Exp(-t / (decay * 2f));
                    float t2 = t - length * 0.3f; // second, softer transient
                    if (t2 > 0f) s[i] += 0.5f * lp * Mathf.Exp(-t2 / decay);
                }
                return Normalize(s, 0.8f);
            }

            float[] Ping()
            {
                int n = (int)(0.4f * rate);
                var s = new float[n];
                for (int i = 0; i < n; i++)
                {
                    float t = i / (float)rate;
                    float attack = Mathf.Clamp01(t / 0.002f);
                    s[i] = attack * (Mathf.Sin(2f * Mathf.PI * 1850f * t) * Mathf.Exp(-t / 0.12f)
                                     + 0.5f * Mathf.Sin(2f * Mathf.PI * 2960f * t) * Mathf.Exp(-t / 0.08f)
                                     + 0.3f * Mathf.Sin(2f * Mathf.PI * 4370f * t) * Mathf.Exp(-t / 0.05f));
                }
                return Normalize(s, 0.7f);
            }

            shotClip = WriteClip("Gunshot", Gunshot(), rate);
            dryClip = WriteClip("DryFire", Click(0.05f, 2600f, 0.004f), rate);
            clackClip = WriteClip("ReloadClack", Click(0.12f, 900f, 0.012f), rate);
            pingClip = WriteClip("HitConfirm", Ping(), rate);
        }

        private static float[] Normalize(float[] s, float peak)
        {
            float max = s.Max(Mathf.Abs);
            if (max > 0f) for (int i = 0; i < s.Length; i++) s[i] *= peak / max;
            return s;
        }

        private static AudioClip WriteClip(string name, float[] samples, int rate)
        {
            Directory.CreateDirectory(AudioDir);
            string path = $"{AudioDir}/{name}.wav";
            using (var w = new BinaryWriter(File.Create(path)))
            {
                int bytes = samples.Length * 2;
                w.Write(new[] { 'R', 'I', 'F', 'F' });
                w.Write(36 + bytes);
                w.Write(new[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' });
                w.Write(16);
                w.Write((short)1); // PCM
                w.Write((short)1); // mono
                w.Write(rate);
                w.Write(rate * 2);
                w.Write((short)2);
                w.Write((short)16);
                w.Write(new[] { 'd', 'a', 't', 'a' });
                w.Write(bytes);
                foreach (var v in samples) w.Write((short)(Mathf.Clamp(v, -1f, 1f) * 32767f));
            }
            AssetDatabase.ImportAsset(path);
            return AssetDatabase.LoadAssetAtPath<AudioClip>(path);
        }

        // ---------------------------------------------------------------- effects

        private static Texture2D WriteTexture(string name, int size, System.Func<float, float, Color> pixel)
        {
            Directory.CreateDirectory(WeaponArtDir);
            string path = $"{WeaponArtDir}/{name}.png";
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float u = (x + 0.5f) / size * 2f - 1f, v = (y + 0.5f) / size * 2f - 1f;
                tex.SetPixel(x, y, pixel(u, v));
            }
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path);
            var ti = (TextureImporter)AssetImporter.GetAtPath(path);
            ti.alphaIsTransparency = true;
            ti.wrapMode = TextureWrapMode.Clamp;
            ti.mipmapEnabled = false;
            ti.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private static Material ParticleMat(string name, Texture2D tex, Color color, bool additive)
        {
            string path = $"{Root}/Materials/{name}.mat";
            var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.shader = shader;
            mat.SetTexture("_BaseMap", tex);
            mat.SetColor("_BaseColor", color);
            mat.SetFloat("_Surface", 1f); // transparent
            mat.SetFloat("_Blend", additive ? 2f : 0f);
            mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetFloat("_DstBlend", (float)(additive ? UnityEngine.Rendering.BlendMode.One : UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha));
            mat.SetFloat("_ZWrite", 0f);
            mat.SetFloat("_Cull", 0f);
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            EditorUtility.SetDirty(mat);
            return mat;
        }

        private static void BuildEffectMaterials()
        {
            var flashTex = WriteTexture("MuzzleFlash", 64, (u, v) =>
            {
                float r = Mathf.Sqrt(u * u + v * v);
                float a = Mathf.Atan2(v, u);
                float core = Mathf.Exp(-r * r * 9f);
                float spikes = Mathf.Pow(Mathf.Max(0f, Mathf.Cos(a * 3f)), 6f) * Mathf.Clamp01(1f - r);
                float k = Mathf.Clamp01(core + spikes * 0.8f);
                return new Color(1f, Mathf.Lerp(0.55f, 0.95f, core), Mathf.Lerp(0.2f, 0.75f, core), k);
            });
            var dotTex = WriteTexture("SoftDot", 32, (u, v) =>
            {
                float r = Mathf.Sqrt(u * u + v * v);
                return new Color(1f, 1f, 1f, Mathf.Clamp01(1f - r) * Mathf.Clamp01(1f - r));
            });
            flashMat = ParticleMat("FX_MuzzleFlash", flashTex, new Color(1f, 0.9f, 0.7f, 1f), additive: true);
            tracerMat = ParticleMat("FX_Tracer", dotTex, Color.white, additive: true);
            impactMat = ParticleMat("FX_Impact", dotTex, Color.white, additive: false);
        }

        private static void BuildImpactPrefab()
        {
            var go = new GameObject("ImpactBurst");
            try
            {
                var ps = go.AddComponent<ParticleSystem>();
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                var main = ps.main;
                main.duration = 0.6f;
                main.loop = false;
                main.playOnAwake = false;
                main.startLifetime = new ParticleSystem.MinMaxCurve(0.2f, 0.55f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(1.2f, 4.5f);
                main.startSize = new ParticleSystem.MinMaxCurve(0.025f, 0.07f);
                main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0.82f, 0.45f), new Color(0.55f, 0.5f, 0.42f));
                main.gravityModifier = 1.2f;
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                main.maxParticles = 120;
                var emission = ps.emission;
                emission.rateOverTime = 0f;
                var shape = ps.shape;
                shape.shapeType = ParticleSystemShapeType.Cone;
                shape.angle = 35f;
                shape.radius = 0.01f;
                var sol = ps.sizeOverLifetime;
                sol.enabled = true;
                sol.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0f));
                var r = go.GetComponent<ParticleSystemRenderer>();
                r.sharedMaterial = impactMat;
                r.renderMode = ParticleSystemRenderMode.Billboard;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                impactPrefab = PrefabUtility.SaveAsPrefabAsset(go, ImpactPrefabPath).GetComponent<ParticleSystem>();
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        private static void BuildOverlayController(AnimationClip hold, AnimationClip reload)
        {
            AssetDatabase.DeleteAsset(OverlayControllerPath);
            overlayController = AnimatorController.CreateAnimatorControllerAtPath(OverlayControllerPath);
            var sm = overlayController.layers[0].stateMachine;
            var idle = sm.AddState("AimHold");
            idle.motion = hold;
            sm.defaultState = idle;
            sm.AddState("Reload").motion = reload;
            EditorUtility.SetDirty(overlayController);
        }

        // ---------------------------------------------------------------- targets

        private static GameObject Part(PrimitiveType type, string name, Transform parent, Vector3 localPos, Vector3 scale, Material mat, bool keepCollider = true)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = scale;
            go.GetComponent<Renderer>().sharedMaterial = mat;
            if (!keepCollider) Object.DestroyImmediate(go.GetComponent<Collider>());
            return go;
        }

        private static void BuildTargetPrefab()
        {
            targetMat = ColorMat("Proto_Target", new Color(0.86f, 0.32f, 0.14f));
            targetMarkMat = ColorMat("Proto_TargetMark", new Color(0.95f, 0.93f, 0.85f));
            var dark = ColorMat("Proto_Dark", new Color(0.18f, 0.18f, 0.2f));

            var root = new GameObject("TargetDummy");
            try
            {
                Part(PrimitiveType.Cube, "Base", root.transform, new Vector3(0f, 0.06f, 0f), new Vector3(0.7f, 0.12f, 0.45f), dark);
                var hinge = new GameObject("Hinge").transform;
                hinge.SetParent(root.transform, false);
                hinge.localPosition = new Vector3(0f, 0.12f, 0f);
                Part(PrimitiveType.Cube, "Post", hinge, new Vector3(0f, 0.28f, 0f), new Vector3(0.07f, 0.56f, 0.07f), dark);
                var torso = Part(PrimitiveType.Cube, "Torso", hinge, new Vector3(0f, 0.92f, 0f), new Vector3(0.5f, 0.72f, 0.08f), targetMat);
                var head = Part(PrimitiveType.Sphere, "Head", hinge, new Vector3(0f, 1.46f, 0f), new Vector3(0.28f, 0.3f, 0.2f), targetMat);
                Part(PrimitiveType.Cube, "Mark", hinge, new Vector3(0f, 1.0f, 0.042f), new Vector3(0.16f, 0.16f, 0.005f), targetMarkMat, keepCollider: false);

                var target = root.AddComponent<DamageableTarget>();
                var so = new SerializedObject(target);
                so.FindProperty("hinge").objectReferenceValue = hinge;
                var flash = so.FindProperty("flashRenderers");
                flash.arraySize = 2;
                flash.GetArrayElementAtIndex(0).objectReferenceValue = torso.GetComponent<Renderer>();
                flash.GetArrayElementAtIndex(1).objectReferenceValue = head.GetComponent<Renderer>();
                so.ApplyModifiedPropertiesWithoutUndo();

                void Hit(GameObject g, float mult, bool crit)
                {
                    var hb = g.AddComponent<Hitbox>();
                    var hso = new SerializedObject(hb);
                    hso.FindProperty("owner").objectReferenceValue = target;
                    hso.FindProperty("multiplier").floatValue = mult;
                    hso.FindProperty("critical").boolValue = crit;
                    hso.ApplyModifiedPropertiesWithoutUndo();
                }
                Hit(torso, 1f, false);
                Hit(head, 2f, true);

                targetPrefab = PrefabUtility.SaveAsPrefabAsset(root, TargetPrefabPath);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        // ---------------------------------------------------------------- player

        /// <summary>Finds the barrel tip in the aim-idle pose, in SMGBone's local space.</summary>
        private static (Vector3 pos, Quaternion rot) MeasureMuzzle(GameObject model, AnimationClip idle)
        {
            var probe = (GameObject)PrefabUtility.InstantiatePrefab(model);
            try
            {
                probe.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                idle.SampleAnimation(probe, 0f);
                var smg = FindDeep(probe.transform, "SMGBone");

                List<Vector3> best = null;
                var mesh = new Mesh();
                foreach (var smr in probe.GetComponentsInChildren<SkinnedMeshRenderer>())
                {
                    if (smr.sharedMaterial == null || !smr.sharedMaterial.name.Contains("Weapon")) continue;
                    smr.BakeMesh(mesh, true);
                    var pts = mesh.vertices.Select(v => smr.transform.position + smr.transform.rotation * v).ToList();
                    if (best == null || Extent(pts) > Extent(best)) best = pts;
                }
                Object.DestroyImmediate(mesh);
                if (best == null) throw new System.InvalidOperationException("No weapon mesh found on the soldier.");

                Vector3 c = best.Aggregate(Vector3.zero, (a, b) => a + b) / best.Count;
                // long axis by power iteration on the covariance
                float xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
                foreach (var p in best)
                {
                    var d = p - c;
                    xx += d.x * d.x; xy += d.x * d.y; xz += d.x * d.z; yy += d.y * d.y; yz += d.y * d.z; zz += d.z * d.z;
                }
                Vector3 axis = new Vector3(1f, 0.3f, 0.2f);
                for (int i = 0; i < 60; i++)
                    axis = new Vector3(xx * axis.x + xy * axis.y + xz * axis.z,
                                       xy * axis.x + yy * axis.y + yz * axis.z,
                                       xz * axis.x + yz * axis.y + zz * axis.z).normalized;
                if (Vector3.Dot(axis, Vector3.forward) < 0f) axis = -axis; // the barrel points where the soldier faces
                float max = best.Max(p => Vector3.Dot(p - c, axis));
                var tip = best.Where(p => Vector3.Dot(p - c, axis) > max - 0.03f).ToList();
                Vector3 tipCentre = tip.Aggregate(Vector3.zero, (a, b) => a + b) / tip.Count + axis * 0.02f;

                Quaternion rot = Quaternion.LookRotation(axis, Vector3.up);
                Debug.Log($"[ForestSoldierSetup] muzzle at {tipCentre} (gun length {(max - best.Min(p => Vector3.Dot(p - c, axis))):F2} m), barrel {axis}");
                return (smg.InverseTransformPoint(tipCentre), Quaternion.Inverse(smg.rotation) * rot);
            }
            finally
            {
                Object.DestroyImmediate(probe);
            }
        }

        private static float Extent(List<Vector3> pts)
        {
            var b = new Bounds(pts[0], Vector3.zero);
            foreach (var p in pts) b.Encapsulate(p);
            return b.size.magnitude;
        }

        private static void AddCombat(GameObject root, GameObject modelInstance, GameObject model, AnimationClip idle)
        {
            var smg = FindDeep(modelInstance.transform, "SMGBone");
            var (mPos, mRot) = MeasureMuzzle(model, idle);
            var muzzle = new GameObject("Muzzle").transform;
            muzzle.SetParent(smg, false);
            muzzle.SetLocalPositionAndRotation(mPos, mRot);
            Vector3 ls = smg.lossyScale;
            muzzle.localScale = new Vector3(1f / ls.x, 1f / ls.y, 1f / ls.z);

            // Flash: two quads along the barrel (seen from the side) and one facing forward.
            var flash = new GameObject("Flash").transform;
            flash.SetParent(muzzle, false);
            Part(PrimitiveType.Quad, "Side_A", flash, new Vector3(0f, 0f, 0.14f), new Vector3(0.16f, 0.32f, 1f), flashMat, false)
                .transform.localRotation = Quaternion.AngleAxis(90f, Vector3.forward) * Quaternion.Euler(90f, 0f, 0f); // vertical, along the barrel
            Part(PrimitiveType.Quad, "Side_B", flash, new Vector3(0f, 0f, 0.14f), new Vector3(0.16f, 0.32f, 1f), flashMat, false)
                .transform.localRotation = Quaternion.Euler(90f, 0f, 0f); // horizontal, along the barrel
            Part(PrimitiveType.Quad, "Front", flash, new Vector3(0f, 0f, 0.03f), new Vector3(0.22f, 0.22f, 1f), flashMat, false);
            foreach (var r in flash.GetComponentsInChildren<Renderer>()) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            var lightGo = new GameObject("FlashLight");
            lightGo.transform.SetParent(muzzle, false);
            lightGo.transform.localPosition = new Vector3(0f, 0f, 0.1f);
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Point;
            light.range = 5f;
            light.intensity = 4f;
            light.color = new Color(1f, 0.75f, 0.4f);
            light.shadows = LightShadows.None;

            var shooter = new GameObject("ShooterOrigin").transform;
            shooter.SetParent(root.transform, false);
            shooter.localPosition = new Vector3(0f, 1.45f, 0f);

            var audio = root.AddComponent<AudioSource>();
            audio.playOnAwake = false;
            audio.spatialBlend = 0f;

            var overlay = root.AddComponent<UpperBodyOverlay>();
            var aimRig = root.AddComponent<SoldierAimRig>();
            var effects = root.AddComponent<WeaponEffects>();
            var weapon = root.AddComponent<HitscanWeapon>();

            var so = new SerializedObject(overlay);
            so.FindProperty("proxyModel").objectReferenceValue = model;
            so.FindProperty("overlayController").objectReferenceValue = overlayController;
            so.FindProperty("model").objectReferenceValue = modelInstance.transform;
            so.FindProperty("reloadClip").objectReferenceValue = reloadClip;
            so.ApplyModifiedPropertiesWithoutUndo();

            so = new SerializedObject(aimRig);
            so.FindProperty("motor").objectReferenceValue = root.GetComponent<SoldierMotor>();
            so.FindProperty("weapon").objectReferenceValue = weapon;
            so.FindProperty("overlay").objectReferenceValue = overlay;
            so.FindProperty("chest").objectReferenceValue = FindDeep(modelInstance.transform, "Spineupper");
            so.FindProperty("muzzle").objectReferenceValue = muzzle;
            var roots = so.FindProperty("upperRoots");
            roots.arraySize = AimRigRoots.Length;
            for (int i = 0; i < AimRigRoots.Length; i++)
                roots.GetArrayElementAtIndex(i).objectReferenceValue = FindDeep(modelInstance.transform, AimRigRoots[i]);
            so.ApplyModifiedPropertiesWithoutUndo();

            so = new SerializedObject(effects);
            so.FindProperty("flash").objectReferenceValue = flash;
            so.FindProperty("flashLight").objectReferenceValue = light;
            so.FindProperty("tracerMaterial").objectReferenceValue = tracerMat;
            so.FindProperty("impactPrefab").objectReferenceValue = impactPrefab;
            so.FindProperty("source").objectReferenceValue = audio;
            so.FindProperty("shot").objectReferenceValue = shotClip;
            so.FindProperty("dryFire").objectReferenceValue = dryClip;
            so.FindProperty("reloadClack").objectReferenceValue = clackClip;
            so.FindProperty("hitConfirm").objectReferenceValue = pingClip;
            so.ApplyModifiedPropertiesWithoutUndo();

            so = new SerializedObject(weapon);
            so.FindProperty("input").objectReferenceValue = root.GetComponent<SoldierInput>();
            so.FindProperty("motor").objectReferenceValue = root.GetComponent<SoldierMotor>();
            so.FindProperty("muzzle").objectReferenceValue = muzzle;
            so.FindProperty("shooterOrigin").objectReferenceValue = shooter;
            so.FindProperty("aimRig").objectReferenceValue = aimRig;
            so.FindProperty("overlay").objectReferenceValue = overlay;
            so.FindProperty("effects").objectReferenceValue = effects;
            so.FindProperty("hitMask").intValue = HitMask;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // ---------------------------------------------------------------- scene

        private static readonly (float distance, float z, float travel)[] RangeTargets =
        {
            (10f, -3f, 0f), (15f, 2f, 0f), (20f, -1f, 0f), (25f, 4f, 2.5f), (30f, -4f, 0f),
            (40f, 1f, 0f), (50f, -2f, 0f), (60f, 3f, 0f), (75f, 0f, 4f),
        };

        private static void BuildRange(Transform env, Material grey)
        {
            var range = new GameObject("ShootingRange").transform;
            var dark = ColorMat("Proto_Dark", new Color(0.18f, 0.18f, 0.2f));
            var sand = ColorMat("Proto_Sand", new Color(0.62f, 0.55f, 0.4f));

            Block("FiringLine_Bench", range, new Vector3(FiringLineX + 1.2f, 0.45f, -2.2f), new Vector3(0.5f, 0.9f, 2.2f), Vector3.zero, sand);
            Block("FiringLine_Strip", range, new Vector3(FiringLineX, 0.005f, 0f), new Vector3(0.15f, 0.01f, 12f), Vector3.zero, targetMarkMat);
            for (int d = 10; d <= 80; d += 10)
                Block($"Marker_{d}m", range, new Vector3(FiringLineX + d, 0.25f, -7f), new Vector3(0.25f, 0.5f, 0.25f), Vector3.zero, d % 50 == 0 ? targetMat : grey);
            // Cover that hides the lower half of the 50 m target, and a berm behind the range.
            Block("Cover_Crate_50m", range, new Vector3(FiringLineX + 47f, 0.55f, -2f), new Vector3(1.2f, 1.1f, 1.2f), Vector3.zero, sand);
            Block("Cover_Wall_25m", range, new Vector3(FiringLineX + 22f, 0.6f, -6f), new Vector3(0.4f, 1.2f, 3f), Vector3.zero, grey);
            Block("Backstop", range, new Vector3(FiringLineX + 88f, 2.5f, 0f), new Vector3(2f, 5f, 26f), Vector3.zero, sand);

            foreach (var (distance, z, travel) in RangeTargets)
            {
                var t = (GameObject)PrefabUtility.InstantiatePrefab(targetPrefab, range);
                t.name = $"Target_{distance:0}m";
                t.transform.SetPositionAndRotation(new Vector3(FiringLineX + distance, 0f, z), Quaternion.Euler(0f, -90f, 0f));
                if (travel > 0f)
                {
                    var mover = t.AddComponent<TargetMover>();
                    var mso = new SerializedObject(mover);
                    mso.FindProperty("halfTravel").floatValue = travel;
                    mso.FindProperty("speed").floatValue = 1.6f;
                    mso.ApplyModifiedPropertiesWithoutUndo();
                }
            }
        }

        private static void AddHud(GameObject player, ThirdPersonCamera rig)
        {
            var hud = new GameObject("HUD");
            var reset = hud.AddComponent<TargetResetController>();
            var so = new SerializedObject(reset);
            so.FindProperty("input").objectReferenceValue = player.GetComponent<SoldierInput>();
            so.ApplyModifiedPropertiesWithoutUndo();

            var ui = hud.AddComponent<PrototypeHUD>();
            so = new SerializedObject(ui);
            so.FindProperty("weapon").objectReferenceValue = player.GetComponent<HitscanWeapon>();
            so.FindProperty("cameraRig").objectReferenceValue = rig;
            so.FindProperty("targets").objectReferenceValue = reset;
            so.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
