using System.IO;
using System.Linq;
using ForestSoldier.Combat;
using ForestSoldier.Player;
using UnityEditor;
using UnityEngine;

namespace ForestSoldier.EditorTools
{
    public static partial class ForestSoldierSetup
    {
        private const string WeaponModels = WeaponArtDir + "/Models";
        private static readonly string[] WeaponFiles = { "pp-19_bizon", "pp-19-01", "low-poly_famas_f1" };
        private static readonly string[] WeaponNames = { "PP-19 Bizon", "PP-19-01", "FAMAS F1" };

        [System.Serializable]
        private class WeaponMaterial
        {
            public string name;
            public string texture;
            public float[] color;
        }

        [System.Serializable]
        private class WeaponMaterials
        {
            public WeaponMaterial[] materials;
        }

        private static void ConfigureWeaponModels()
        {
            foreach (string name in WeaponFiles)
            {
                var description = JsonUtility.FromJson<WeaponMaterials>(File.ReadAllText($"{WeaponModels}/{name}.json"));
                var importer = (ModelImporter)AssetImporter.GetAtPath($"{WeaponModels}/{name}.fbx");
                importer.globalScale = 1f;
                importer.importAnimation = false;
                importer.importCameras = false;
                importer.importLights = false;
                foreach (var entry in description.materials)
                {
                    string path = $"{WeaponModels}/{name}_{entry.name}.mat";
                    var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                    if (material == null)
                    {
                        material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                        AssetDatabase.CreateAsset(material, path);
                    }
                    material.SetColor("_BaseColor", new Color(entry.color[0], entry.color[1], entry.color[2], entry.color[3]));
                    material.SetFloat("_Metallic", 0f);
                    material.SetFloat("_Smoothness", 0.25f);
                    material.SetFloat("_Cull", 0f);
                    if (!string.IsNullOrEmpty(entry.texture))
                        material.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>($"{WeaponModels}/{entry.texture}"));
                    EditorUtility.SetDirty(material);
                    importer.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), entry.name), material);
                }
                importer.SaveAndReimport();
            }
        }

        private static void AddWeaponLoadout(GameObject player, GameObject model)
        {
            var pose = model.GetComponentsInChildren<Transform>(true)
                .Select(t => (bone: t, position: t.localPosition, rotation: t.localRotation, scale: t.localScale)).ToArray();
            LoadClips()["Rifle Aim Idle"].SampleAnimation(model, 0f);
            var rig = FindDeep(model.transform, "SMGBone");
            var firingMuzzle = FindDeep(rig, "Muzzle");
            var wrist = FindDeep(model.transform, "HandWrist.R");
            var original = model.GetComponentsInChildren<SkinnedMeshRenderer>().First(r => r.name == "Object_23");
            var loadout = player.AddComponent<WeaponLoadout>();
            var settings = new SerializedObject(loadout);
            settings.FindProperty("originalGun").objectReferenceValue = original;
            settings.FindProperty("firingMuzzle").objectReferenceValue = firingMuzzle;
            var slots = settings.FindProperty("slots");
            slots.arraySize = 4;

            // Preserve the stock gun's independent socket when the shared FX muzzle moves.
            var stockMuzzle = new GameObject("StockMuzzle").transform;
            stockMuzzle.SetParent(rig, false);
            stockMuzzle.SetLocalPositionAndRotation(firingMuzzle.localPosition, firingMuzzle.localRotation);
            stockMuzzle.localScale = firingMuzzle.localScale;
            var stockSight = new GameObject("StockSight").transform;
            stockSight.SetParent(stockMuzzle, false);
            stockSight.localPosition = Vector3.up * 0.075f;
            SetSlot(0, "Soldier SMG", null, stockMuzzle, stockSight, null);

            for (int i = 0; i < WeaponFiles.Length; i++)
            {
                var asset = AssetDatabase.LoadAssetAtPath<GameObject>($"{WeaponModels}/{WeaponFiles[i]}.fbx");
                var mount = new GameObject(WeaponNames[i]).transform;
                mount.SetParent(rig, false);
                mount.SetPositionAndRotation(wrist.position, firingMuzzle.rotation);
                Vector3 scale = rig.lossyScale;
                mount.localScale = new Vector3(1f / scale.x, 1f / scale.y, 1f / scale.z);
                var gun = (GameObject)PrefabUtility.InstantiatePrefab(asset, mount);
                // The FBX's local mesh axes retain Blender's -Y forward and +Z up.
                gun.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.Euler(-90f, 0f, 0f));
                gun.transform.localScale = Vector3.one;
                SetSlot(i + 1, WeaponNames[i], mount.gameObject, FindDeep(gun.transform, "Muzzle"),
                    FindDeep(gun.transform, "Sight"), FindDeep(gun.transform, "Support"));
                mount.gameObject.SetActive(false);
            }
            settings.ApplyModifiedPropertiesWithoutUndo();
            ConfigureWeaponContacts(player, model);
            foreach (var bone in pose)
            {
                bone.bone.SetLocalPositionAndRotation(bone.position, bone.rotation);
                bone.bone.localScale = bone.scale;
            }

            void SetSlot(int index, string name, GameObject gun, Transform muzzle, Transform sight, Transform support)
            {
                var slot = slots.GetArrayElementAtIndex(index);
                slot.FindPropertyRelative("Name").stringValue = name;
                slot.FindPropertyRelative("Model").objectReferenceValue = gun;
                slot.FindPropertyRelative("Muzzle").objectReferenceValue = muzzle;
                slot.FindPropertyRelative("Sight").objectReferenceValue = sight;
                slot.FindPropertyRelative("Support").objectReferenceValue = support;
            }
        }

        private static void ConfigureWeaponContacts(GameObject player, GameObject model)
        {
            var settings = new SerializedObject(player.GetComponent<WeaponLoadout>());
            var slots = settings.FindProperty("slots");
            var rig = FindDeep(model.transform, "SMGBone");
            for (int i = 0; i < slots.arraySize; i++)
            {
                var slot = slots.GetArrayElementAtIndex(i);
                Transform grip, stock, support;
                if (i == 0)
                {
                    var muzzle = (Transform)slot.FindPropertyRelative("Muzzle").objectReferenceValue;
                    grip = Marker("StockGrip", rig);
                    grip.position = PalmContact(".R");
                    support = Marker("StockSupport", rig);
                    support.position = PalmContact(".L") + muzzle.up * 0.025f;
                    stock = Marker("StockButt", muzzle);
                    stock.localPosition = new Vector3(0f, 0.005f, -0.90f);
                }
                else
                {
                    var gun = (GameObject)slot.FindPropertyRelative("Model").objectReferenceValue;
                    grip = FindDeep(gun.transform, "Grip");
                    stock = FindDeep(gun.transform, "Stock");
                    support = FindDeep(gun.transform, "Support");
                }
                slot.FindPropertyRelative("Grip").objectReferenceValue = grip;
                slot.FindPropertyRelative("Stock").objectReferenceValue = stock;
                slot.FindPropertyRelative("Support").objectReferenceValue = support;
                slot.FindPropertyRelative("VerticalSupport").boolValue = i == 2;
            }
            settings.ApplyModifiedPropertiesWithoutUndo();

            Vector3 PalmContact(string side) =>
                (FindDeep(model.transform, "Hand" + side + ".001").position +
                 FindDeep(model.transform, "Fingers" + side).position) * 0.5f;

            Transform Marker(string name, Transform parent)
            {
                var marker = parent.Find(name);
                if (marker != null) return marker;
                marker = new GameObject(name).transform;
                marker.SetParent(parent, false);
                return marker;
            }
        }

        [MenuItem("Forest Soldier/Update Player Weapon Loadout")]
        public static void UpdateWeaponLoadout()
        {
            ConfigureWeaponModels();
            var player = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                var model = player.GetComponent<SoldierAnimator>().Animator.gameObject;
                var existing = player.GetComponent<WeaponLoadout>();
                if (existing == null) AddWeaponLoadout(player, model);
                else
                {
                    var pose = model.GetComponentsInChildren<Transform>(true)
                        .Select(t => (bone: t, position: t.localPosition, rotation: t.localRotation, scale: t.localScale)).ToArray();
                    LoadClips()["Rifle Aim Idle"].SampleAnimation(model, 0f);
                    ConfigureWeaponContacts(player, model);
                    foreach (var bone in pose)
                    {
                        bone.bone.SetLocalPositionAndRotation(bone.position, bone.rotation);
                        bone.bone.localScale = bone.scale;
                    }
                }
                PrefabUtility.SaveAsPrefabAsset(player, PrefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(player);
            }
            AssetDatabase.SaveAssets();
        }
    }
}
