using System.Collections.Generic;
using System.IO;
using System.Linq;
using ForestSoldier.Player;
using UnityEditor;
using UnityEngine;

namespace ForestSoldier.EditorTools
{
    public static partial class ForestSoldierSetup
    {
        // Foot contacts measured at 48 evenly spaced points over each complete cycle.
        private static readonly Dictionary<string, (int first, int second)> FootContacts = new()
        {
            { "Walking", (8, 29) },
            { "Walking Backwards", (13, 37) },
            { "Crouch Walking", (8, 28) },
            { "Loop Run Forward", (5, 28) },
            { "Sprint Forward", (12, 40) },
            { "Loop Strafe Left", (8, 35) },
            { "Loop Strafe Right", (8, 35) },
            { "Aim Walk Forward Left", (12, 36) },
            { "Aim Walk Forward Right", (12, 36) },
        };

        private static void ConfigureFootstepEvents(ModelImporterClipAnimation clip)
        {
            if (!FootContacts.TryGetValue(clip.name, out var contacts)) return;

            const string callback = nameof(SoldierFootsteps.Footstep);
            var events = (clip.events ?? System.Array.Empty<AnimationEvent>())
                .Where(e => e.functionName != callback).ToList();
            events.Add(new AnimationEvent { functionName = callback, time = contacts.first / 48f });
            events.Add(new AnimationEvent { functionName = callback, time = contacts.second / 48f });
            clip.events = events.OrderBy(e => e.time).ToArray();
        }

        private static void AddFootsteps(GameObject model)
        {
            const string path = AudioDir + "/Footsteps";
            var footsteps = model.AddComponent<SoldierFootsteps>();
            var source = model.GetComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = 0.7f;
            source.minDistance = 2f;
            source.maxDistance = 18f;

            var serialized = new SerializedObject(footsteps);
            var clips = AssetDatabase.FindAssets("t:AudioClip", new[] { path })
                .Select(AssetDatabase.GUIDToAssetPath)
                .OrderBy(asset => asset, System.StringComparer.Ordinal)
                .ToArray();
            AssignBank("walk", "GrassWalk_");
            AssignBank("run", "GrassRun_");
            serialized.ApplyModifiedPropertiesWithoutUndo();

            void AssignBank(string property, string prefix)
            {
                var bank = clips.Where(asset => Path.GetFileNameWithoutExtension(asset).StartsWith(prefix, System.StringComparison.Ordinal))
                    .Select(AssetDatabase.LoadAssetAtPath<AudioClip>).ToArray();
                var array = serialized.FindProperty(property);
                array.arraySize = bank.Length;
                for (int i = 0; i < bank.Length; i++)
                    array.GetArrayElementAtIndex(i).objectReferenceValue = bank[i];
            }
        }
    }
}
