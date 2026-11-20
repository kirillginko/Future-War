using System.Collections.Generic;
using System.Linq;
using ForestSoldier.Player;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ForestSoldier.EditorTools
{
    /// <summary>
    /// Builds the prototype's assets from the exported soldier FBX:
    /// import settings, materials, animator controller,
    /// Player prefab and the ForestRange test scene.
    ///
    /// Menu: Forest Soldier / Rebuild Milestone 1 Assets
    /// Batch: Unity -batchmode -quit -projectPath . -executeMethod ForestSoldier.EditorTools.ForestSoldierSetup.BuildAll
    ///
    /// Rebuilding overwrites the controller, prefab and scene it creates.
    /// </summary>
    public static partial class ForestSoldierSetup
    {
        private const string Root = "Assets/_Game";
        private const string FbxPath = Root + "/Art/Characters/Soldier/Soldier.fbx";
        private const string BodyTexPath = Root + "/Art/Characters/Soldier/Textures/Soldier_Body.png";
        private const string WeaponTexPath = Root + "/Art/Characters/Soldier/Textures/Soldier_Weapons.png";
        private const string InputPath = Root + "/Input/SoldierControls.inputactions";
        private const string ControllerPath = Root + "/Animations/Soldier.controller";
        private const string MaskPath = Root + "/Animations/UpperBodyAim.mask";
        private const string PrefabPath = Root + "/Prefabs/Player/Player.prefab";
        private const string ScenePath = Root + "/Scenes/ForestRange.unity";
        private const string PlayerLayerName = "Player";
        private const int PlayerLayerIndex = 8;

        /// <summary>Blender units -> metres. The soldier is 4.44 units tall in the .blend.</summary>
        private const float ImportScale = 0.405f;
        private const float TargetHeight = 1.8f;

        // Clip speeds measured by Tools/Blender/export_soldier.py (m/s after ImportScale).
        private const float RunForwardClipSpeed = 1.66f;
        private const float WalkBackClipSpeed = 1.08f;
        private const float StrafeClipSpeed = 2.57f;
        private const float DiagonalClipSpeed = 1.54f; // 1.09 forward + 1.09 sideways

        private static readonly HashSet<string> LoopClips = new HashSet<string>
        {
            "Rifle Aiming Idle", "Sprint Forward", "Firing Rifle",
            "Loop Run Forward", "Loop Walk Back", "Loop Strafe Right", "Loop Strafe Left",
            "Loop Walk Forward Left", "Aim Walk Back", "Aim Walk Forward Left", "Aim Walk Forward Right",
            "Crouched Strafe Run", "Walk Crouching Forward", "Walk Crouching Right",
            "Walking", "Walking Backwards", "Crouch Walking",
            "Rifle Aim Idle", "Idle Crouching Aiming",
        };

        [MenuItem("Forest Soldier/Rebuild Prototype Assets")]
        public static void BuildFromMenu()
        {
            if (!EditorUtility.DisplayDialog("Rebuild prototype assets",
                    "This overwrites Soldier.controller, Player.prefab, TargetDummy.prefab, ImpactBurst.prefab, the generated sounds and ForestRange.unity. Continue?",
                    "Rebuild", "Cancel")) return;
            BuildAll();
        }

        public static void BuildAll()
        {
            EnsureLayer();
            ConfigureTextures();
            var materials = CreateSoldierMaterials();
            ConfigureModelImport(materials);
            var clips = LoadClips();
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(FbxPath);
            AssetDatabase.DeleteAsset(MaskPath); // earlier builds made an aim layer mask; aim is baked into the clips now
            var controller = CreateController(clips);
            BuildCombatAssets(clips);
            ConfigureWeaponModels();
            BuildWalkerAssets(clips, model);
            BuildDestructionAssets();
            BuildHelicopterAssets();
            var prefab = CreatePlayerPrefab(model, controller);
            CreateScene(prefab);
            AssetDatabase.SaveAssets();
            Debug.Log("[ForestSoldierSetup] Prototype assets built.");
        }

        // ---------------------------------------------------------------- project

        private static void EnsureLayer()
        {
            var tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            var layers = tagManager.FindProperty("layers");
            var slot = layers.GetArrayElementAtIndex(PlayerLayerIndex);
            if (slot.stringValue != PlayerLayerName)
            {
                if (!string.IsNullOrEmpty(slot.stringValue))
                    throw new System.InvalidOperationException($"Layer {PlayerLayerIndex} is already '{slot.stringValue}'.");
                slot.stringValue = PlayerLayerName;
                tagManager.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        private static void ConfigureTextures()
        {
            foreach (var path in new[] { BodyTexPath, WeaponTexPath })
            {
                var ti = (TextureImporter)AssetImporter.GetAtPath(path);
                // Low-poly palette atlases: keep colours crisp.
                ti.filterMode = FilterMode.Point;
                ti.mipmapEnabled = false;
                ti.textureCompression = TextureImporterCompression.Uncompressed;
                ti.SaveAndReimport();
            }
        }

        private static Dictionary<string, Material> CreateSoldierMaterials()
        {
            var unlit = Shader.Find("Universal Render Pipeline/Unlit");
            Material Make(string name, string texPath)
            {
                string path = $"{Root}/Materials/{name}.mat";
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null)
                {
                    mat = new Material(unlit) { name = name };
                    AssetDatabase.CreateAsset(mat, path);
                }
                mat.shader = unlit;
                mat.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(texPath));
                mat.SetColor("_BaseColor", Color.white);
                mat.SetFloat("_Cull", 0f);
                EditorUtility.SetDirty(mat);
                return mat;
            }
            return new Dictionary<string, Material>
            {
                { "Grid", Make("Soldier_Body", BodyTexPath) },
                { "weapons", Make("Soldier_Weapons", WeaponTexPath) },
            };
        }

        private static void ConfigureModelImport(Dictionary<string, Material> materials)
        {
            var mi = (ModelImporter)AssetImporter.GetAtPath(FbxPath);
            mi.globalScale = ImportScale;
            mi.useFileScale = true;
            mi.animationType = ModelImporterAnimationType.Generic;
            mi.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            mi.importAnimation = true;
            mi.importCameras = false;
            mi.importLights = false;
            mi.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
            foreach (var kv in materials)
                mi.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), kv.Key), kv.Value);
            mi.SaveAndReimport();

            // Clip list depends on the first import, so configure clips afterwards.
            var clips = mi.defaultClipAnimations;
            foreach (var c in clips)
            {
                string clean = c.takeName.Contains("|") ? c.takeName.Substring(c.takeName.IndexOf('|') + 1) : c.takeName;
                c.name = clean;
                c.loopTime = LoopClips.Contains(clean);
                c.loopPose = false;
                ConfigureFootstepEvents(c);
            }
            mi.clipAnimations = clips;
            mi.SaveAndReimport();
        }

        private static Dictionary<string, AnimationClip> LoadClips()
        {
            var clips = AssetDatabase.LoadAllAssetsAtPath(FbxPath)
                .OfType<AnimationClip>()
                .Where(c => !c.name.StartsWith("__preview__"))
                .ToDictionary(c => c.name);
            Debug.Log($"[ForestSoldierSetup] {clips.Count} clips: {string.Join(", ", clips.Keys.OrderBy(k => k))}");
            return clips;
        }

        // ---------------------------------------------------------------- animation

        private static AnimatorController CreateController(Dictionary<string, AnimationClip> clips)
        {
            AssetDatabase.DeleteAsset(ControllerPath);
            var ac = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
            ac.AddParameter("MoveX", AnimatorControllerParameterType.Float);
            ac.AddParameter("MoveZ", AnimatorControllerParameterType.Float);
            ac.AddParameter("Speed", AnimatorControllerParameterType.Float);
            ac.AddParameter("Aiming", AnimatorControllerParameterType.Bool);
            ac.AddParameter("Grounded", AnimatorControllerParameterType.Bool);

            // Base layer: one 2D freeform blend tree, positions in m/s (local X right, Z forward).
            ac.CreateBlendTreeInController("Locomotion", out BlendTree tree, 0);
            ConfigureStandingLocomotion(tree, clips);
            AddCrouchLocomotion(ac, clips);

            EditorUtility.SetDirty(ac);
            return ac;
        }

        private static void ConfigureStandingLocomotion(BlendTree tree, Dictionary<string, AnimationClip> clips)
        {
            tree.blendType = BlendTreeType.FreeformDirectional2D;
            tree.blendParameter = "MoveX";
            tree.blendParameterY = "MoveZ";

            const float run = 1.8f, sprint = 5.5f, back = 1.8f * 0.7f;
            float diag = run * 0.7071f;
            var children = new List<(AnimationClip clip, Vector2 pos, float timeScale)>
            {
                (clips["Rifle Aiming Idle"], Vector2.zero, 1f),
                (clips["Walking"], new Vector2(0f, SoldierMotor.WalkSpeed), 1f),
                (clips["Loop Run Forward"], new Vector2(0f, run), run / RunForwardClipSpeed),
                (clips["Sprint Forward"], new Vector2(0f, sprint), 1f),
                (clips["Walking Backwards"], new Vector2(0f, -back), back / WalkBackClipSpeed),
                (clips["Loop Strafe Right"], new Vector2(run, 0f), run / StrafeClipSpeed),
                (clips["Loop Strafe Left"], new Vector2(-run, 0f), run / StrafeClipSpeed),
                (clips["Aim Walk Forward Right"], new Vector2(diag, diag), run / DiagonalClipSpeed),
                (clips["Aim Walk Forward Left"], new Vector2(-diag, diag), run / DiagonalClipSpeed),
            };
            tree.children = System.Array.Empty<ChildMotion>();
            foreach (var c in children) tree.AddChild(c.clip, c.pos);
            var motions = tree.children;
            for (int i = 0; i < motions.Length; i++) motions[i].timeScale = children[i].timeScale;
            tree.children = motions;
        }

        private static void AddCrouchLocomotion(AnimatorController controller, Dictionary<string, AnimationClip> clips)
        {
            controller.AddParameter("Crouching", AnimatorControllerParameterType.Bool);
            var standing = controller.layers[0].stateMachine.defaultState;
            var crouching = controller.CreateBlendTreeInController("Crouch Locomotion", out BlendTree tree, 0);
            tree.blendType = BlendTreeType.Simple1D;
            tree.blendParameter = "Speed";
            tree.useAutomaticThresholds = false;
            tree.AddChild(clips["Idle Crouching Aiming"], 0f);
            tree.AddChild(clips["Crouch Walking"], SoldierMotor.CrouchMoveSpeed);

            var enter = standing.AddTransition(crouching);
            enter.hasExitTime = false;
            enter.hasFixedDuration = true;
            enter.duration = 0.10f;
            enter.AddCondition(AnimatorConditionMode.If, 0f, "Crouching");
            var exit = crouching.AddTransition(standing);
            exit.hasExitTime = false;
            exit.hasFixedDuration = true;
            exit.duration = 0.10f;
            exit.AddCondition(AnimatorConditionMode.IfNot, 0f, "Crouching");
        }

        // ---------------------------------------------------------------- prefab

        private static Transform FindDeep(Transform t, string name)
        {
            if (t.name == name) return t;
            foreach (Transform c in t)
            {
                var r = FindDeep(c, name);
                if (r != null) return r;
            }
            return null;
        }

        /// <summary>Measures the idle pose: which way the toes point and where the soles are.</summary>
        private static (float yawFix, float height, float minY) MeasureModel(GameObject model, AnimationClip idle)
        {
            var probe = (GameObject)PrefabUtility.InstantiatePrefab(model);
            try
            {
                probe.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                idle.SampleAnimation(probe, 0f);

                Vector3 toes = Vector3.zero;
                foreach (var side in new[] { "L", "R" })
                {
                    var foot = FindDeep(probe.transform, $"Foot.{side}");
                    var toe = FindDeep(probe.transform, $"FootToe.{side}");
                    if (foot != null && toe != null) toes += toe.position - foot.position;
                }
                toes.y = 0f;
                float yaw = Mathf.Atan2(toes.x, toes.z) * Mathf.Rad2Deg;
                float yawFix = -Mathf.Round(yaw / 90f) * 90f;

                var bounds = new Bounds();
                bool first = true;
                var mesh = new Mesh();
                foreach (var smr in probe.GetComponentsInChildren<SkinnedMeshRenderer>())
                {
                    smr.BakeMesh(mesh, true);
                    foreach (var v in mesh.vertices)
                    {
                        Vector3 w = smr.transform.position + smr.transform.rotation * v;
                        if (first) { bounds = new Bounds(w, Vector3.zero); first = false; }
                        else bounds.Encapsulate(w);
                    }
                }
                Object.DestroyImmediate(mesh);
                Debug.Log($"[ForestSoldierSetup] toe yaw {yaw:F1} deg -> model yaw fix {yawFix}; idle bounds {bounds.min} .. {bounds.max}");
                return (yawFix, bounds.size.y, bounds.min.y);
            }
            finally
            {
                Object.DestroyImmediate(probe);
            }
        }

        private static GameObject CreatePlayerPrefab(GameObject model, AnimatorController controller)
        {
            var idle = AssetDatabase.LoadAllAssetsAtPath(FbxPath).OfType<AnimationClip>().First(c => c.name == "Rifle Aiming Idle");
            var (yawFix, height, minY) = MeasureModel(model, idle);
            float scaleFix = height > 0.01f ? TargetHeight / height : 1f;

            var root = new GameObject("Player");
            try
            {
                var cc = root.AddComponent<CharacterController>();
                cc.height = 1.8f;
                cc.radius = 0.3f;
                cc.center = new Vector3(0f, 0.9f, 0f);
                cc.stepOffset = 0.35f;
                cc.slopeLimit = 50f;
                cc.skinWidth = 0.03f;
                cc.minMoveDistance = 0f;

                var input = root.AddComponent<SoldierInput>();
                var motor = root.AddComponent<SoldierMotor>();
                var anim = root.AddComponent<SoldierAnimator>();

                var modelInstance = (GameObject)PrefabUtility.InstantiatePrefab(model, root.transform);
                modelInstance.name = "Model";
                modelInstance.transform.localRotation = Quaternion.Euler(0f, yawFix, 0f);
                modelInstance.transform.localScale = Vector3.one * scaleFix;
                modelInstance.transform.localPosition = new Vector3(0f, -minY * scaleFix, 0f);
                var animator = modelInstance.GetComponent<Animator>() ?? modelInstance.AddComponent<Animator>();
                animator.runtimeAnimatorController = controller;
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

                var so = new SerializedObject(input);
                so.FindProperty("actions").objectReferenceValue = AssetDatabase.LoadAssetAtPath<InputActionAsset>(InputPath);
                so.ApplyModifiedPropertiesWithoutUndo();

                so = new SerializedObject(motor);
                so.FindProperty("input").objectReferenceValue = input;
                so.FindProperty("groundMask").intValue = ~(1 << PlayerLayerIndex) & ~(1 << 2);
                so.ApplyModifiedPropertiesWithoutUndo();

                so = new SerializedObject(anim);
                so.FindProperty("animator").objectReferenceValue = animator;
                so.FindProperty("motor").objectReferenceValue = motor;
                so.ApplyModifiedPropertiesWithoutUndo();

                AddCombat(root, modelInstance, model);
                AddWeaponLoadout(root, modelInstance);
                AddFootsteps(modelInstance);

                foreach (var t in root.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = PlayerLayerIndex;

                Debug.Log($"[ForestSoldierSetup] model height {height:F2} -> scale fix {scaleFix:F3}");
                return PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        // ---------------------------------------------------------------- scene

        private static Material ColorMat(string name, Color c)
        {
            string path = $"{Root}/Materials/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.SetColor("_BaseColor", c);
            mat.SetFloat("_Smoothness", 0.1f);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        private static GameObject Block(string name, Transform parent, Vector3 pos, Vector3 size, Vector3 euler, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.SetPositionAndRotation(pos, Quaternion.Euler(euler));
            go.transform.localScale = size;
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
            go.isStatic = true;
            return go;
        }

        private static void CreateScene(GameObject playerPrefab)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var sun = new GameObject("Sun");
            var light = sun.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.3f;
            light.color = new Color(1f, 0.96f, 0.88f);
            light.shadows = LightShadows.Soft;
            sun.transform.rotation = Quaternion.Euler(48f, -35f, 0f);
            RenderSettings.sun = light;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.62f, 0.7f, 0.8f);
            RenderSettings.ambientEquatorColor = new Color(0.45f, 0.5f, 0.45f);
            RenderSettings.ambientGroundColor = new Color(0.25f, 0.24f, 0.2f);

            var grey = ColorMat("Proto_Grey", new Color(0.55f, 0.55f, 0.53f));
            var blue = ColorMat("Proto_Blue", new Color(0.25f, 0.4f, 0.65f));

            // The training yard: slopes, steps, walls and a low roof for movement
            // and camera checks, on flat cleared ground (y = 0) inside the forest.
            var env = new GameObject("TrainingYard").transform;

            Block("Ramp_20deg", env, new Vector3(-8f, 1.0f, 12f), new Vector3(4f, 0.3f, 6f), new Vector3(-20f, 0f, 0f), grey);
            Block("Ramp_40deg", env, new Vector3(-2f, 1.6f, 12f), new Vector3(4f, 0.3f, 5f), new Vector3(-40f, 0f, 0f), grey);
            Block("Ramp_60deg_TooSteep", env, new Vector3(4f, 1.6f, 12f), new Vector3(4f, 0.3f, 4f), new Vector3(-60f, 0f, 0f), grey);
            for (int i = 0; i < 6; i++)
                Block($"Step_{i}", env, new Vector3(10f, 0.15f + i * 0.15f, 8f + i * 0.5f), new Vector3(3f, 0.3f + i * 0.3f, 0.5f), Vector3.zero, grey);
            Block("Wall_North", env, new Vector3(0f, 1.5f, 22f), new Vector3(30f, 3f, 0.5f), Vector3.zero, grey);
            Block("Wall_Corner_A", env, new Vector3(-12f, 1.5f, 2f), new Vector3(0.5f, 3f, 10f), Vector3.zero, grey);
            Block("Wall_Corner_B", env, new Vector3(-9.25f, 1.5f, -3f), new Vector3(6f, 3f, 0.5f), Vector3.zero, grey);
            Block("LowRoof_Post_A", env, new Vector3(6f, 1.1f, -6f), new Vector3(0.4f, 2.2f, 0.4f), Vector3.zero, grey);
            Block("LowRoof_Post_B", env, new Vector3(10f, 1.1f, -6f), new Vector3(0.4f, 2.2f, 0.4f), Vector3.zero, grey);
            Block("LowRoof", env, new Vector3(8f, 2.35f, -8f), new Vector3(5f, 0.3f, 5f), Vector3.zero, blue);
            for (int i = 0; i < 8; i++)
            {
                float a = i * Mathf.PI / 4f;
                Block($"Pillar_{i}", env, new Vector3(Mathf.Sin(a) * 18f, 1.5f, Mathf.Cos(a) * 18f - 10f),
                    new Vector3(0.6f, 3f, 0.6f), Vector3.zero, grey);
            }

            var player = (GameObject)PrefabUtility.InstantiatePrefab(playerPrefab);
            // Spawn on the firing line, facing down range (+X).
            player.transform.SetPositionAndRotation(new Vector3(FiringLineX - 2f, 0.05f, 0f), Quaternion.Euler(0f, 90f, 0f));
            BuildRange(env, grey);

            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            var cam = camGo.AddComponent<Camera>();
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 600f;
            camGo.AddComponent<AudioListener>();
            var rig = camGo.AddComponent<ThirdPersonCamera>();
            var so = new SerializedObject(rig);
            so.FindProperty("target").objectReferenceValue = player.transform;
            so.FindProperty("input").objectReferenceValue = player.GetComponent<SoldierInput>();
            // The camera ignores walkers (layer 10) so it doesn't lurch as they pass.
            so.FindProperty("collisionMask").intValue = ~(1 << PlayerLayerIndex) & ~(1 << 2) & ~(1 << 10);
            so.ApplyModifiedPropertiesWithoutUndo();
            camGo.transform.SetPositionAndRotation(new Vector3(0.55f, 1.65f, -3.4f), Quaternion.identity);

            var motorSo = new SerializedObject(player.GetComponent<SoldierMotor>());
            motorSo.FindProperty("cameraRig").objectReferenceValue = rig;
            motorSo.ApplyModifiedPropertiesWithoutUndo();

            var weaponSo = new SerializedObject(player.GetComponent<ForestSoldier.Combat.HitscanWeapon>());
            weaponSo.FindProperty("cameraRig").objectReferenceValue = rig;
            weaponSo.ApplyModifiedPropertiesWithoutUndo();
            AddHud(player, rig);
            BuildForest(light, cam);
            PlaceWalkers();
            PlaceHouses(cam);
            PlaceHelicopter(cam);

            EditorSceneManager.SaveScene(scene, ScenePath);

            var scenes = EditorBuildSettings.scenes.Where(s => s.path != ScenePath).ToList();
            scenes.Insert(0, new EditorBuildSettingsScene(ScenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();
        }
    }
}
