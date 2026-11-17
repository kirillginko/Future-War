using System.Collections.Generic;
using System.Linq;
using ForestSoldier.Combat;
using ForestSoldier.Environment;
using ForestSoldier.NPC;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace ForestSoldier.EditorTools
{
    /// <summary>Walking soldiers you can shoot: animator, prefab, routes.</summary>
    public static partial class ForestSoldierSetup
    {
        private const string WalkerControllerPath = Root + "/Animations/Walker.controller";
        private const string WalkerPrefabPath = Root + "/Prefabs/NPC/Walker.prefab";
        private const int NpcLayerIndex = 10;
        private const string NpcLayerName = "NPC";

        private static GameObject walkerPrefab;
        private static AudioClip fleshHitClip, fleshKillClip;
        private static Material splatMat;
        private static ParticleSystem bloodPrefab;
        private const string BloodPrefabPath = Root + "/Prefabs/Player/BloodBurst.prefab";

        private static void BuildWalkerAssets(Dictionary<string, AnimationClip> clips, GameObject model)
        {
            EnsureNpcLayer();
            BuildBloodAssets();
            var controller = BuildWalkerController(clips);
            walkerPrefab = BuildWalkerPrefab(model, controller, clips["Rifle Aiming Idle"]);
        }

        private static void EnsureNpcLayer()
        {
            var tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            var slot = tagManager.FindProperty("layers").GetArrayElementAtIndex(NpcLayerIndex);
            if (slot.stringValue != NpcLayerName)
            {
                if (!string.IsNullOrEmpty(slot.stringValue))
                    throw new System.InvalidOperationException($"Layer {NpcLayerIndex} is already '{slot.stringValue}'.");
                slot.stringValue = NpcLayerName;
                tagManager.ApplyModifiedPropertiesWithoutUndo();
            }
            // Walkers stop bullets but not the player (they also step aside for you).
            Physics.IgnoreLayerCollision(PlayerLayerIndex, NpcLayerIndex, true);
            Physics.IgnoreLayerCollision(NpcLayerIndex, NpcLayerIndex, true);
        }

        private static AnimatorController BuildWalkerController(Dictionary<string, AnimationClip> clips)
        {
            AssetDatabase.DeleteAsset(WalkerControllerPath);
            var ac = AnimatorController.CreateAnimatorControllerAtPath(WalkerControllerPath);
            ac.AddParameter("Speed", AnimatorControllerParameterType.Float);
            ac.AddParameter("DieFront", AnimatorControllerParameterType.Trigger);
            ac.AddParameter("DieBack", AnimatorControllerParameterType.Trigger);

            ac.CreateBlendTreeInController("Locomotion", out BlendTree tree, 0);
            tree.blendType = BlendTreeType.Simple1D;
            tree.blendParameter = "Speed";
            tree.useAutomaticThresholds = false;
            tree.AddChild(clips["Rifle Aiming Idle"], 0f);
            tree.AddChild(clips["Walking"], 0.99f); // the Walking clip's own pace

            var sm = ac.layers[0].stateMachine;
            void Death(string state, string trigger, AnimationClip clip)
            {
                var s = sm.AddState(state);
                s.motion = clip;
                var t = sm.AddAnyStateTransition(s);
                t.AddCondition(AnimatorConditionMode.If, 0f, trigger);
                t.hasExitTime = false;
                t.hasFixedDuration = true;
                t.duration = 0.12f;
                t.canTransitionToSelf = false;
            }
            Death("DieFront", "DieFront", clips["Dying"]);
            Death("DieBack", "DieBack", clips["Death From Back Headshot"]);
            EditorUtility.SetDirty(ac);
            return ac;
        }

        private static GameObject BuildWalkerPrefab(GameObject model, AnimatorController controller, AnimationClip idle)
        {
            var (_, height, minY) = MeasureModel(model, idle);
            float scaleFix = height > 0.01f ? TargetHeight / height : 1f;

            // Flat khaki uniform so walkers don't read as the (near-black) player.
            // A tint can't do it: the body texture is almost black, so it's dropped.
            var body = AssetDatabase.LoadAssetAtPath<Material>($"{Root}/Materials/Soldier_Body.mat");
            var walkerBody = ColorMat("Soldier_Body_Walker", new Color(0.52f, 0.46f, 0.32f));
            walkerBody.SetTexture("_BaseMap", null);
            walkerBody.SetFloat("_Smoothness", 0.15f);
            EditorUtility.SetDirty(walkerBody);

            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(WalkerPrefabPath));
            var root = new GameObject("Walker");
            try
            {
                var npc = root.AddComponent<WalkerNPC>();
                var capsule = root.AddComponent<CapsuleCollider>();
                // Body ends below the neck so the head hitbox gets head shots.
                capsule.center = new Vector3(0f, 0.74f, 0f);
                capsule.height = 1.48f;
                capsule.radius = 0.28f;

                var m = (GameObject)PrefabUtility.InstantiatePrefab(model, root.transform);
                m.name = "Model";
                m.transform.localScale = Vector3.one * scaleFix;
                m.transform.localPosition = new Vector3(0f, -minY * scaleFix, 0f);
                var animator = m.GetComponent<Animator>();
                animator.runtimeAnimatorController = controller;
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;

                // The Walking clip carries Footstep events: give them a receiver (3D, quiet).
                var stepSource = m.AddComponent<AudioSource>();
                stepSource.playOnAwake = false;
                stepSource.spatialBlend = 1f;
                stepSource.rolloffMode = AudioRolloffMode.Linear;
                stepSource.minDistance = 2f;
                stepSource.maxDistance = 22f;
                var steps = m.AddComponent<WalkerFootsteps>();
                var stepClips = AssetDatabase.FindAssets("GrassWalk_ t:AudioClip", new[] { Root + "/Audio/Footsteps" })
                    .Select(AssetDatabase.GUIDToAssetPath).OrderBy(p => p, System.StringComparer.Ordinal)
                    .Select(AssetDatabase.LoadAssetAtPath<AudioClip>).ToArray();
                var stepSo = new SerializedObject(steps);
                var bank = stepSo.FindProperty("steps");
                bank.arraySize = stepClips.Length;
                for (int i = 0; i < stepClips.Length; i++) bank.GetArrayElementAtIndex(i).objectReferenceValue = stepClips[i];
                stepSo.ApplyModifiedPropertiesWithoutUndo();

                var renderers = m.GetComponentsInChildren<SkinnedMeshRenderer>();
                foreach (var r in renderers)
                    if (r.sharedMaterial == body) r.sharedMaterial = walkerBody;

                // Head hitbox rides the head bone: double damage, critical.
                var headBone = FindDeep(m.transform, "head");
                var head = new GameObject("HeadHitbox");
                head.transform.SetParent(headBone, false);
                head.transform.position = headBone.position + Vector3.up * 0.05f; // centre of the head in the walking pose
                Debug.Log($"[ForestSoldierSetup] walker head hitbox centre at y {head.transform.position.y:F2} (model {TargetHeight} m tall)");
                var sphere = head.AddComponent<SphereCollider>();
                float s = Mathf.Max(headBone.lossyScale.x, headBone.lossyScale.y, headBone.lossyScale.z);
                sphere.radius = 0.14f / Mathf.Max(1e-4f, s);
                var hb = head.AddComponent<Hitbox>();

                var so = new SerializedObject(hb);
                so.FindProperty("owner").objectReferenceValue = npc;
                so.FindProperty("multiplier").floatValue = 2f;
                so.FindProperty("critical").boolValue = true;
                so.ApplyModifiedPropertiesWithoutUndo();

                so = new SerializedObject(npc);
                so.FindProperty("animator").objectReferenceValue = animator;
                var flash = so.FindProperty("flashRenderers");
                flash.arraySize = renderers.Length;
                for (int i = 0; i < renderers.Length; i++) flash.GetArrayElementAtIndex(i).objectReferenceValue = renderers[i];
                so.ApplyModifiedPropertiesWithoutUndo();

                foreach (var t in root.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = NpcLayerIndex;
                return PrefabUtility.SaveAsPrefabAsset(root, WalkerPrefabPath);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        // ---------------------------------------------------------------- blood and body hits

        private static void BuildBloodAssets()
        {
            const int rate = 44100;
            var rng = new System.Random(31);
            float Noise() => (float)(rng.NextDouble() * 2.0 - 1.0);

            // A wet thud for a body hit: low thump, short muffled noise, a tiny smack on top.
            float[] Thud(float length, float f0, float f1, float bodyDecay, float noiseDecay, float second)
            {
                int n = (int)(length * rate);
                var s = new float[n];
                float lp = 0f, phase = 0f;
                for (int i = 0; i < n; i++)
                {
                    float t = i / (float)rate;
                    lp += 0.12f * (Noise() - lp);
                    phase += 2f * Mathf.PI * Mathf.Lerp(f0, f1, Mathf.Clamp01(t / 0.08f)) / rate;
                    float v = Mathf.Sin(phase) * Mathf.Exp(-t / bodyDecay) + 1.8f * lp * Mathf.Exp(-t / noiseDecay);
                    if (t < 0.0015f) v += Noise() * 0.6f;
                    float t2 = t - second;
                    if (second > 0f && t2 > 0f) v += 0.7f * Mathf.Sin(2f * Mathf.PI * 55f * t2) * Mathf.Exp(-t2 / 0.07f);
                    s[i] = v * Mathf.Clamp01(t / 0.0005f);
                }
                return Normalize(s, 0.9f);
            }
            fleshHitClip = WriteClip("FleshHit", Thud(0.2f, 170f, 75f, 0.045f, 0.025f, 0f), rate);
            fleshKillClip = WriteClip("FleshKill", Thud(0.5f, 120f, 45f, 0.09f, 0.05f, 0.11f), rate);

            // Splat: an irregular blob with a few droplets, white with alpha; the material makes it red.
            var blobPhase = new float[5];
            for (int i = 0; i < blobPhase.Length; i++) blobPhase[i] = (float)rng.NextDouble() * Mathf.PI * 2f;
            var drops = new Vector3[9];
            for (int i = 0; i < drops.Length; i++)
            {
                float a = (float)rng.NextDouble() * Mathf.PI * 2f, d = 0.55f + (float)rng.NextDouble() * 0.35f;
                drops[i] = new Vector3(Mathf.Cos(a) * d, Mathf.Sin(a) * d, 0.04f + (float)rng.NextDouble() * 0.07f);
            }
            var tex = WriteTexture("BloodSplat", 128, (u, v) =>
            {
                float r = Mathf.Sqrt(u * u + v * v), ang = Mathf.Atan2(v, u);
                float edge = 0.45f;
                for (int k = 0; k < blobPhase.Length; k++) edge += 0.06f * Mathf.Sin(ang * (k + 2) + blobPhase[k]) / (1f + k * 0.4f);
                float a = Mathf.Clamp01((edge - r) / 0.04f);
                foreach (var d in drops)
                {
                    float dd = Vector2.Distance(new Vector2(u, v), new Vector2(d.x, d.y));
                    a = Mathf.Max(a, Mathf.Clamp01((d.z - dd) / 0.02f));
                }
                float shade = 0.75f + 0.25f * Mathf.Clamp01(r / 0.5f); // darker, thicker middle
                return new Color(shade, shade, shade, a * 0.95f);
            });
            splatMat = ParticleMat("FX_BloodSplat", tex, new Color(0.32f, 0.02f, 0.02f, 0.92f), additive: false);
            var dropMat = ParticleMat("FX_BloodDrop", AssetDatabase.LoadAssetAtPath<Texture2D>(WeaponArtDir + "/SoftDot.png"),
                Color.white, additive: false);

            var go = new GameObject("BloodBurst");
            try
            {
                var ps = go.AddComponent<ParticleSystem>();
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                var main = ps.main;
                main.duration = 0.8f;
                main.loop = false;
                main.playOnAwake = false;
                main.startLifetime = new ParticleSystem.MinMaxCurve(0.25f, 0.7f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(1.2f, 4.2f);
                main.startSize = new ParticleSystem.MinMaxCurve(0.025f, 0.075f);
                main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.55f, 0.03f, 0.03f), new Color(0.28f, 0.01f, 0.01f));
                main.gravityModifier = 1.6f;
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                main.maxParticles = 150;
                var emission = ps.emission;
                emission.rateOverTime = 0f;
                var shape = ps.shape;
                shape.shapeType = ParticleSystemShapeType.Cone;
                shape.angle = 28f;
                shape.radius = 0.03f;
                var sol = ps.sizeOverLifetime;
                sol.enabled = true;
                sol.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0.3f));
                var r = go.GetComponent<ParticleSystemRenderer>();
                r.sharedMaterial = dropMat;
                r.renderMode = ParticleSystemRenderMode.Billboard;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                bloodPrefab = PrefabUtility.SaveAsPrefabAsset(go, BloodPrefabPath).GetComponent<ParticleSystem>();
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        /// <summary>Called from AddCombat with the player's WeaponEffects.</summary>
        private static void WireBlood(SerializedObject effects)
        {
            effects.FindProperty("fleshHit").objectReferenceValue = fleshHitClip;
            effects.FindProperty("fleshKill").objectReferenceValue = fleshKillClip;
            effects.FindProperty("bloodPrefab").objectReferenceValue = bloodPrefab;
            effects.FindProperty("splatMaterial").objectReferenceValue = splatMat;
            // Land on the world only: not the player, walkers, triggers or foliage volumes.
            effects.FindProperty("splatMask").intValue = ~(1 << PlayerLayerIndex) & ~(1 << 2) & ~(1 << 9) & ~(1 << NpcLayerIndex);
        }

        private static Vector2[] TrailRoute(float from, float to)
        {
            var pts = new List<Vector2>();
            for (float t = from; t <= to + 1e-4f; t += 0.01f) pts.Add(ForestLayout.TrailPoint(t));
            return pts.ToArray();
        }

        private static void PlaceWalkers()
        {
            var parent = new GameObject("Walkers").transform;
            var routes = new (string name, Vector2[] route, bool loop, float startAt)[]
            {
                ("Walker_RangeSouth", new[] { new Vector2(24f, -9.5f), new Vector2(92f, -9.5f) }, false, 0.3f),
                ("Walker_RangeNorth", new[] { new Vector2(92f, 9.5f), new Vector2(24f, 9.5f) }, false, 0.55f),
                ("Walker_RangeFar", new[] { new Vector2(100f, -10f), new Vector2(100f, 10f) }, false, 0.5f),
                ("Walker_Yard", new[] { new Vector2(-24f, -30f), new Vector2(24f, -30f), new Vector2(24f, 26f), new Vector2(-24f, 26f) }, true, 0.1f),
                ("Walker_TrailNorth", TrailRoute(0.08f, 0.33f), false, 0.4f),
                ("Walker_TrailWest", TrailRoute(0.45f, 0.72f), false, 0.6f),
                ("Walker_TrailSouth", TrailRoute(0.76f, 0.96f), false, 0.3f),
            };
            foreach (var (name, route, loop, startAt) in routes)
            {
                var w = (GameObject)PrefabUtility.InstantiatePrefab(walkerPrefab, parent);
                w.name = name;
                w.transform.position = new Vector3(route[0].x, 0f, route[0].y);
                var so = new SerializedObject(w.GetComponent<WalkerNPC>());
                var r = so.FindProperty("route");
                r.arraySize = route.Length;
                for (int i = 0; i < route.Length; i++) r.GetArrayElementAtIndex(i).vector2Value = route[i];
                so.FindProperty("loop").boolValue = loop;
                so.FindProperty("startAt").floatValue = startAt;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
        }
    }
}
