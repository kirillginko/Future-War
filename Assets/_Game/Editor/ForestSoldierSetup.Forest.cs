using System.Collections.Generic;
using System.IO;
using ForestSoldier.Environment;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace ForestSoldier.EditorTools
{
    /// <summary>
    /// Milestone 3: the forest. A Unity Terrain whose clearings (training yard,
    /// range lane) are exactly flat at y = 0, rolling forest floor around them,
    /// rim hills at the edge, a dirt trail, a pond, generated low-poly trees,
    /// rocks and bushes, landmarks, sky, fog, post-processing and ambience.
    /// Everything is seeded, so a rebuild gives the same forest.
    /// </summary>
    public static partial class ForestSoldierSetup
    {
        private const string EnvArt = Root + "/Art/Environment";
        private const string EnvPrefabs = Root + "/Prefabs/Environment";
        private const string TerrainDataPath = EnvArt + "/ForestTerrain.asset";
        private const int HeightRes = 513;
        private const int AlphaRes = 512;
        private const int Seed = 20261116;
        private const int FoliageLayerIndex = 9;
        private const string FoliageLayerName = "Foliage";

        private static Vector2[] trailSamples;
        private static float[] trailProfile;
        private static bool[,] nearTrailCell;

        // ================================================================ entry

        private static void BuildForest(Light sun, Camera cam)
        {
            Directory.CreateDirectory(EnvArt);
            Directory.CreateDirectory(EnvPrefabs);
            EnsureFoliageLayer();
            var root = new GameObject("Forest").transform;

            var terrain = BuildTerrain(root);
            var kit = BuildForestKit();
            Scatter(root, terrain, kit);
            BuildLandmarks(root, terrain, kit);
            BuildBoundary(root);
            BuildAtmosphere(sun, cam);
            BuildAmbience(root);
            AddFoliageFader(cam, kit);
        }

        // ================================================================ foliage fade

        private static void EnsureFoliageLayer()
        {
            var tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            var slot = tagManager.FindProperty("layers").GetArrayElementAtIndex(FoliageLayerIndex);
            if (slot.stringValue != FoliageLayerName)
            {
                if (!string.IsNullOrEmpty(slot.stringValue))
                    throw new System.InvalidOperationException($"Layer {FoliageLayerIndex} is already '{slot.stringValue}'.");
                slot.stringValue = FoliageLayerName;
                tagManager.ApplyModifiedPropertiesWithoutUndo();
            }
            // Canopy volumes are camera-only triggers: nothing should collide with them.
            for (int i = 0; i < 32; i++) Physics.IgnoreLayerCollision(FoliageLayerIndex, i, true);
        }

        private static void CanopyVolume(GameObject tree, Vector3 centre, float radius, float height)
        {
            var go = new GameObject("CanopyVolume") { layer = FoliageLayerIndex };
            go.transform.SetParent(tree.transform, false);
            var c = go.AddComponent<CapsuleCollider>();
            c.isTrigger = true;
            c.center = centre;
            c.radius = radius;
            c.height = height;
        }

        private static void AddFoliageFader(Camera cam, ForestKit kit)
        {
            var fader = cam.gameObject.AddComponent<FoliageFader>();
            var so = new SerializedObject(fader);
            var player = Object.FindFirstObjectByType<ForestSoldier.Player.SoldierInput>();
            so.FindProperty("target").objectReferenceValue = player != null ? player.transform : null;
            so.FindProperty("foliageMask").intValue = 1 << FoliageLayerIndex;
            var solid = so.FindProperty("solid");
            var faded = so.FindProperty("faded");
            solid.arraySize = faded.arraySize = kit.FoliageSolid.Count;
            for (int i = 0; i < kit.FoliageSolid.Count; i++)
            {
                solid.GetArrayElementAtIndex(i).objectReferenceValue = kit.FoliageSolid[i];
                faded.GetArrayElementAtIndex(i).objectReferenceValue = kit.FoliageFaded[i];
            }
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // ================================================================ height

        private static float Fbm(float x, float z, float freq, int octaves, float offset)
        {
            float sum = 0f, amp = 1f, norm = 0f;
            for (int o = 0; o < octaves; o++)
            {
                sum += amp * (Mathf.PerlinNoise(x * freq + offset, z * freq + offset * 1.7f) * 2f - 1f);
                norm += amp;
                amp *= 0.5f;
                freq *= 2.03f;
            }
            return sum / norm;
        }

        private static float Smooth(float a, float b, float x) => Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(a, b, x));

        /// <summary>Ground height before the trail is cut in.</summary>
        private static float BaseHeight(float x, float z)
        {
            var p = new Vector2(x, z);
            float forest = 4.2f * Fbm(x, z, 1f / 75f, 4, 37.1f) + 1.6f;
            float h = Mathf.Lerp(0f, forest, Smooth(0f, ForestLayout.ClearingFalloff, ForestLayout.DistanceToClearing(p)));

            // lookout hill
            float dh = (p - ForestLayout.LookoutHill).magnitude;
            h += 9f * Mathf.Exp(-(dh * dh) / (2f * 20f * 20f));

            // pond: a shallow bowl
            float dp = (p - ForestLayout.PondCentre).magnitude;
            h = Mathf.Lerp(h, -1.9f, Smooth(ForestLayout.PondRadius * 1.7f, ForestLayout.PondRadius * 0.55f, dp));

            // rim hills close the world in
            float edge = Mathf.Max(Mathf.Abs(x), Mathf.Abs(z));
            h += 28f * Smooth(ForestLayout.Boundary - 14f, ForestLayout.TerrainSize * 0.5f, edge);
            return h;
        }

        private static void PrepareTrail()
        {
            const int n = 480;
            trailSamples = new Vector2[n + 1];
            var raw = new float[n + 1];
            for (int i = 0; i <= n; i++)
            {
                trailSamples[i] = ForestLayout.TrailPoint(i / (float)n);
                raw[i] = BaseHeight(trailSamples[i].x, trailSamples[i].y);
            }
            // two passes of a ±12-sample moving average: a walkable grade
            trailProfile = raw;
            for (int pass = 0; pass < 2; pass++)
            {
                var next = new float[n + 1];
                for (int i = 0; i <= n; i++)
                {
                    float s = 0f;
                    int c = 0;
                    for (int k = -12; k <= 12; k++)
                    {
                        int j = Mathf.Clamp(i + k, 0, n);
                        s += trailProfile[j];
                        c++;
                    }
                    next[i] = s / c;
                }
                trailProfile = next;
            }
            // coarse 10 m cells that are anywhere near the trail
            int cells = Mathf.CeilToInt(ForestLayout.TerrainSize / 10f);
            nearTrailCell = new bool[cells, cells];
            foreach (var s in trailSamples)
                for (int dz = -2; dz <= 2; dz++)
                for (int dx = -2; dx <= 2; dx++)
                {
                    int cx = Mathf.FloorToInt((s.x + ForestLayout.TerrainSize * 0.5f) / 10f) + dx;
                    int cz = Mathf.FloorToInt((s.y + ForestLayout.TerrainSize * 0.5f) / 10f) + dz;
                    if (cx >= 0 && cz >= 0 && cx < cells && cz < cells) nearTrailCell[cx, cz] = true;
                }
        }

        /// <summary>Distance to the trail and the trail's graded height there (∞ when far away).</summary>
        private static float NearestTrail(Vector2 p, out float profile)
        {
            profile = 0f;
            int cells = nearTrailCell.GetLength(0);
            int cx = Mathf.FloorToInt((p.x + ForestLayout.TerrainSize * 0.5f) / 10f);
            int cz = Mathf.FloorToInt((p.y + ForestLayout.TerrainSize * 0.5f) / 10f);
            if (cx < 0 || cz < 0 || cx >= cells || cz >= cells || !nearTrailCell[cx, cz]) return float.MaxValue;
            float best = float.MaxValue;
            for (int i = 0; i < trailSamples.Length; i++)
            {
                float d = (trailSamples[i] - p).sqrMagnitude;
                if (d < best)
                {
                    best = d;
                    profile = trailProfile[i];
                }
            }
            return Mathf.Sqrt(best);
        }

        private static float GroundHeight(float x, float z)
        {
            var p = new Vector2(x, z);
            float h = BaseHeight(x, z);
            float d = NearestTrail(p, out float trail);
            if (d < float.MaxValue)
            {
                float w = (1f - Smooth(ForestLayout.TrailHalfWidth, ForestLayout.TrailHalfWidth + 6f, d))
                          * Smooth(0f, 3f, ForestLayout.DistanceToClearing(p));
                h = Mathf.Lerp(h, trail, w);
            }
            return h;
        }

        // ================================================================ terrain

        private static Terrain BuildTerrain(Transform root)
        {
            PrepareTrail();
            float size = ForestLayout.TerrainSize, half = size * 0.5f;

            AssetDatabase.DeleteAsset(TerrainDataPath);
            var data = new TerrainData { heightmapResolution = HeightRes };
            data.size = new Vector3(size, ForestLayout.TerrainHeight, size);
            data.alphamapResolution = AlphaRes;
            // Save first: splat maps written before the asset exists are not kept as sub-assets.
            AssetDatabase.CreateAsset(data, TerrainDataPath);

            var heights = new float[HeightRes, HeightRes];
            for (int iz = 0; iz < HeightRes; iz++)
            for (int ix = 0; ix < HeightRes; ix++)
            {
                float x = -half + ix * size / (HeightRes - 1);
                float z = -half + iz * size / (HeightRes - 1);
                float h = Mathf.Clamp(GroundHeight(x, z), -ForestLayout.TerrainBase, ForestLayout.TerrainHeight - ForestLayout.TerrainBase);
                heights[iz, ix] = (h + ForestLayout.TerrainBase) / ForestLayout.TerrainHeight;
            }
            data.SetHeights(0, 0, heights);

            data.terrainLayers = new[]
            {
                TerrainLayerAsset("Grass", 7f, new Color(0.34f, 0.46f, 0.20f), new Color(0.24f, 0.36f, 0.14f), 0.25f),
                TerrainLayerAsset("ForestFloor", 5f, new Color(0.28f, 0.25f, 0.16f), new Color(0.20f, 0.27f, 0.13f), 0.35f),
                TerrainLayerAsset("Dirt", 4f, new Color(0.45f, 0.35f, 0.23f), new Color(0.36f, 0.28f, 0.19f), 0.25f),
                TerrainLayerAsset("Gravel", 3f, new Color(0.53f, 0.50f, 0.44f), new Color(0.40f, 0.38f, 0.34f), 0.6f),
            };
            var alpha = new float[AlphaRes, AlphaRes, 4];
            for (int iz = 0; iz < AlphaRes; iz++)
            for (int ix = 0; ix < AlphaRes; ix++)
            {
                float u = (ix + 0.5f) / AlphaRes, v = (iz + 0.5f) / AlphaRes;
                float x = -half + u * size, z = -half + v * size;
                var p = new Vector2(x, z);
                float dc = ForestLayout.DistanceToClearing(p);
                float n = Fbm(x, z, 1f / 9f, 3, 11.3f);

                float gravel = 1f - Smooth(0f, 3.5f + n * 1.5f, dc);
                float floor = Smooth(5f, 22f, dc + n * 6f);
                float dt = NearestTrail(p, out _);
                float dirt = 1f - Smooth(ForestLayout.TrailHalfWidth - 0.4f, ForestLayout.TrailHalfWidth + 0.8f + 0.6f * n, dt);
                float steep = Smooth(24f, 34f, data.GetSteepness(u, v));
                float shore = 1f - Smooth(ForestLayout.PondRadius * 0.9f, ForestLayout.PondRadius * 1.35f, (p - ForestLayout.PondCentre).magnitude);
                dirt = Mathf.Max(dirt, steep * 0.3f, shore);

                float rest = 1f - gravel;
                float d = dirt * rest;
                rest -= d;
                float f = floor * rest;
                float g = rest - f;
                alpha[iz, ix, 0] = g;
                alpha[iz, ix, 1] = f;
                alpha[iz, ix, 2] = d;
                alpha[iz, ix, 3] = gravel;
            }
            data.SetAlphamaps(0, 0, alpha);
            EditorUtility.SetDirty(data);
            AssetDatabase.SaveAssets();

            var go = Terrain.CreateTerrainGameObject(data);
            go.name = "Terrain";
            go.transform.SetParent(root, false);
            go.transform.position = new Vector3(-half, -ForestLayout.TerrainBase, -half);
            var terrain = go.GetComponent<Terrain>();
            if (GraphicsSettings.currentRenderPipeline != null)
                terrain.materialTemplate = GraphicsSettings.currentRenderPipeline.defaultTerrainMaterial;
            terrain.heightmapPixelError = 4f;
            terrain.drawInstanced = true;
            terrain.basemapDistance = 120f;
            go.isStatic = true;
            terrain.Flush();
            return terrain;
        }

        // ---------------------------------------------------------------- textures

        /// <summary>Value noise on a wrapping lattice, so the texture tiles.</summary>
        private static float TileNoise(float u, float v, int lattice, int seed)
        {
            float x = u * lattice, y = v * lattice;
            int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
            float fx = x - x0, fy = y - y0;
            float H(int i, int j)
            {
                i = ((i % lattice) + lattice) % lattice;
                j = ((j % lattice) + lattice) % lattice;
                uint h = (uint)(i * 374761393 + j * 668265263 + seed * 2246822519);
                h = (h ^ (h >> 13)) * 1274126177;
                return ((h ^ (h >> 16)) & 0xffff) / 65535f;
            }
            float sx = fx * fx * (3f - 2f * fx), sy = fy * fy * (3f - 2f * fy);
            return Mathf.Lerp(Mathf.Lerp(H(x0, y0), H(x0 + 1, y0), sx), Mathf.Lerp(H(x0, y0 + 1), H(x0 + 1, y0 + 1), sx), sy);
        }

        private static TerrainLayer TerrainLayerAsset(string name, float tile, Color a, Color b, float speckle)
        {
            const int size = 256;
            string texPath = $"{EnvArt}/Ground_{name}.png";
            var tex = new Texture2D(size, size, TextureFormat.RGB24, false);
            int seed = name.GetHashCode() & 0x7fff;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float u = x / (float)size, v = y / (float)size;
                float n = 0.5f * TileNoise(u, v, 8, seed) + 0.3f * TileNoise(u, v, 16, seed + 1) + 0.2f * TileNoise(u, v, 64, seed + 2);
                float s = TileNoise(u, v, 128, seed + 3);
                Color c = Color.Lerp(a, b, n);
                c *= 1f + speckle * (s - 0.5f);
                c.a = 1f;
                tex.SetPixel(x, y, c);
            }
            File.WriteAllBytes(texPath, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(texPath);
            var ti = (TextureImporter)AssetImporter.GetAtPath(texPath);
            ti.wrapMode = TextureWrapMode.Repeat;
            ti.SaveAndReimport();

            string layerPath = $"{EnvArt}/Ground_{name}.terrainlayer";
            AssetDatabase.DeleteAsset(layerPath);
            var layer = new TerrainLayer
            {
                diffuseTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath),
                tileSize = new Vector2(tile, tile),
                smoothness = 0.05f,
            };
            AssetDatabase.CreateAsset(layer, layerPath);
            return layer;
        }

        // ================================================================ meshes

        /// <summary>Flat-shaded mesh builder: every triangle gets its own vertices.</summary>
        private class MeshBuilder
        {
            private readonly List<Vector3> verts = new List<Vector3>();
            private readonly List<int>[] subs;

            public MeshBuilder(int submeshes)
            {
                subs = new List<int>[submeshes];
                for (int i = 0; i < submeshes; i++) subs[i] = new List<int>();
            }

            public void Tri(int sub, Vector3 a, Vector3 b, Vector3 c)
            {
                int i = verts.Count;
                verts.Add(a);
                verts.Add(b);
                verts.Add(c);
                subs[sub].Add(i);
                subs[sub].Add(i + 1);
                subs[sub].Add(i + 2);
            }

            public void Quad(int sub, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
            {
                Tri(sub, a, b, c);
                Tri(sub, a, c, d);
            }

            public Mesh Build(string name)
            {
                var m = new Mesh { name = name };
                m.SetVertices(verts);
                m.subMeshCount = subs.Length;
                for (int i = 0; i < subs.Length; i++) m.SetTriangles(subs[i], i);
                m.RecalculateNormals();
                m.RecalculateBounds();
                return m;
            }
        }

        private static void Trunk(MeshBuilder mb, int sub, float y0, float y1, float r0, float r1, int seg)
        {
            for (int i = 0; i < seg; i++)
            {
                float a0 = i * Mathf.PI * 2f / seg, a1 = (i + 1) * Mathf.PI * 2f / seg;
                var b0 = new Vector3(Mathf.Cos(a0) * r0, y0, Mathf.Sin(a0) * r0);
                var b1 = new Vector3(Mathf.Cos(a1) * r0, y0, Mathf.Sin(a1) * r0);
                var t0 = new Vector3(Mathf.Cos(a0) * r1, y1, Mathf.Sin(a0) * r1);
                var t1 = new Vector3(Mathf.Cos(a1) * r1, y1, Mathf.Sin(a1) * r1);
                mb.Quad(sub, b0, t0, t1, b1);
            }
        }

        private static void Cone(MeshBuilder mb, int sub, float y, float radius, float height, int seg, System.Random rng)
        {
            var ring = new Vector3[seg];
            float twist = (float)rng.NextDouble() * Mathf.PI;
            for (int i = 0; i < seg; i++)
            {
                float a = twist + i * Mathf.PI * 2f / seg;
                float r = radius * (0.85f + 0.3f * (float)rng.NextDouble());
                ring[i] = new Vector3(Mathf.Cos(a) * r, y + ((float)rng.NextDouble() - 0.5f) * 0.25f * height, Mathf.Sin(a) * r);
            }
            var tip = new Vector3(((float)rng.NextDouble() - 0.5f) * 0.15f * radius, y + height, ((float)rng.NextDouble() - 0.5f) * 0.15f * radius);
            var under = new Vector3(0f, y + height * 0.12f, 0f);
            for (int i = 0; i < seg; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % seg];
                mb.Tri(sub, a, tip, b);
                mb.Tri(sub, a, b, under); // closes the underside
            }
        }

        private static readonly Vector3[] IcoBase = BuildIcoBase();

        private static Vector3[] BuildIcoBase()
        {
            float t = (1f + Mathf.Sqrt(5f)) / 2f;
            var v = new[]
            {
                new Vector3(-1, t, 0), new Vector3(1, t, 0), new Vector3(-1, -t, 0), new Vector3(1, -t, 0),
                new Vector3(0, -1, t), new Vector3(0, 1, t), new Vector3(0, -1, -t), new Vector3(0, 1, -t),
                new Vector3(t, 0, -1), new Vector3(t, 0, 1), new Vector3(-t, 0, -1), new Vector3(-t, 0, 1),
            };
            for (int i = 0; i < v.Length; i++) v[i] = v[i].normalized;
            return v;
        }

        private static readonly int[] IcoTris =
        {
            0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11, 1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7, 6, 7, 1, 8,
            3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9, 4, 9, 5, 2, 4, 11, 6, 2, 10, 8, 6, 7, 9, 8, 1,
        };

        /// <summary>A lumpy once-subdivided icosahedron (80 faces).</summary>
        private static void Blob(MeshBuilder mb, int sub, Vector3 centre, Vector3 scale, float lumpiness, System.Random rng)
        {
            var verts = new List<Vector3>(IcoBase);
            var mids = new Dictionary<long, int>();
            int Mid(int a, int b)
            {
                long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                if (!mids.TryGetValue(key, out int i))
                {
                    i = verts.Count;
                    verts.Add(((verts[a] + verts[b]) * 0.5f).normalized);
                    mids[key] = i;
                }
                return i;
            }
            var tris = new List<int>();
            for (int i = 0; i < IcoTris.Length; i += 3)
            {
                int a = IcoTris[i], b = IcoTris[i + 1], c = IcoTris[i + 2];
                int ab = Mid(a, b), bc = Mid(b, c), ca = Mid(c, a);
                tris.AddRange(new[] { a, ab, ca, b, bc, ab, c, ca, bc, ab, bc, ca });
            }
            var pos = new Vector3[verts.Count];
            for (int i = 0; i < verts.Count; i++)
            {
                float k = 1f + lumpiness * ((float)rng.NextDouble() * 2f - 1f);
                pos[i] = centre + Vector3.Scale(verts[i] * k, scale);
            }
            // The table's order gives outward normals with Unity's cross(b - a, c - a).
            for (int i = 0; i < tris.Count; i += 3) mb.Tri(sub, pos[tris[i]], pos[tris[i + 1]], pos[tris[i + 2]]);
        }

        private class ForestKit
        {
            public readonly List<GameObject> Pines = new List<GameObject>();
            public readonly List<GameObject> Broadleaves = new List<GameObject>();
            public readonly List<GameObject> Rocks = new List<GameObject>();
            public readonly List<GameObject> Bushes = new List<GameObject>();
            public Material Wood, Roof, Water, Bark, Rock, Signal;
            public readonly List<Material> FoliageSolid = new List<Material>();
            public readonly List<Material> FoliageFaded = new List<Material>();

            public void Fadeable(Material solid, float alpha)
            {
                var faded = ColorMat(solid.name + "_Faded", solid.GetColor("_BaseColor"));
                var c = solid.GetColor("_BaseColor");
                c.a = alpha;
                faded.SetColor("_BaseColor", c);
                MakeTransparent(faded, 0.05f);
                faded.SetFloat("_Cull", 2f);
                FoliageSolid.Add(solid);
                FoliageFaded.Add(faded);
            }
        }

        private static Material EnvMat(string name, Color c, float smooth = 0.08f)
        {
            var m = ColorMat(name, c);
            m.SetFloat("_Smoothness", smooth);
            m.enableInstancing = true;
            return m;
        }

        private static Mesh SaveMesh(Mesh mesh)
        {
            string path = $"{EnvArt}/{mesh.name}.asset";
            AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(mesh, path);
            return mesh;
        }

        private static GameObject EnvPrefab(string name, Mesh mesh, Material[] mats, System.Action<GameObject> colliders, float cullHeight)
        {
            var go = new GameObject(name);
            try
            {
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterials = mats;
                colliders?.Invoke(go);
                var lod = go.AddComponent<LODGroup>();
                lod.SetLODs(new[] { new LOD(cullHeight, new Renderer[] { r }) });
                lod.RecalculateBounds();
                return PrefabUtility.SaveAsPrefabAsset(go, $"{EnvPrefabs}/{name}.prefab");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        private static ForestKit BuildForestKit()
        {
            var kit = new ForestKit();
            kit.Bark = EnvMat("Env_Bark", new Color(0.30f, 0.21f, 0.14f));
            var needles = new[] { EnvMat("Env_Needles_A", new Color(0.12f, 0.26f, 0.15f)), EnvMat("Env_Needles_B", new Color(0.17f, 0.31f, 0.16f)) };
            var leaves = new[] { EnvMat("Env_Leaves_A", new Color(0.27f, 0.42f, 0.16f)), EnvMat("Env_Leaves_B", new Color(0.38f, 0.46f, 0.17f)) };
            kit.Rock = EnvMat("Env_Rock", new Color(0.46f, 0.46f, 0.44f), 0.15f);
            var moss = EnvMat("Env_RockMoss", new Color(0.38f, 0.42f, 0.33f), 0.1f);
            var bush = EnvMat("Env_Bush", new Color(0.22f, 0.34f, 0.15f));
            foreach (var m in needles) kit.Fadeable(m, 0.22f);
            foreach (var m in leaves) kit.Fadeable(m, 0.22f);
            kit.Fadeable(bush, 0.3f);
            kit.Wood = EnvMat("Env_Wood", new Color(0.44f, 0.31f, 0.19f));
            kit.Roof = EnvMat("Env_Roof", new Color(0.55f, 0.16f, 0.10f));
            kit.Signal = EnvMat("Env_Signal", new Color(0.95f, 0.15f, 0.1f));
            kit.Signal.EnableKeyword("_EMISSION");
            kit.Signal.SetColor("_EmissionColor", new Color(2.5f, 0.2f, 0.1f));
            kit.Water = ColorMat("Env_Water", new Color(0.20f, 0.33f, 0.38f, 0.78f));
            MakeTransparent(kit.Water, 0.92f);

            var rng = new System.Random(Seed);
            void TrunkCollider(GameObject g, float radius, float height)
            {
                var c = g.AddComponent<CapsuleCollider>();
                c.radius = radius;
                c.height = height;
                c.center = new Vector3(0f, height * 0.5f, 0f);
            }

            for (int v = 0; v < 4; v++)
            {
                var mb = new MeshBuilder(2);
                float trunkTop = 2.6f + v * 0.3f;
                Trunk(mb, 0, -0.4f, trunkTop + 1.5f, 0.24f, 0.12f, 6);
                int tiers = 3 + (v % 2);
                float y = 1.5f, radius = 2.1f + 0.15f * v;
                for (int t = 0; t < tiers; t++)
                {
                    float h = 2.6f - t * 0.25f;
                    Cone(mb, 1, y, radius, h, 7, rng);
                    y += h * 0.55f;
                    radius *= 0.74f;
                }
                var mesh = SaveMesh(mb.Build($"Mesh_Pine_{v}"));
                kit.Pines.Add(EnvPrefab($"Pine_{v}", mesh, new[] { kit.Bark, needles[v % 2] }, g =>
                {
                    TrunkCollider(g, 0.28f, 4f);
                    CanopyVolume(g, new Vector3(0f, 4.6f, 0f), 2.0f, 7f);
                }, 0.015f));
            }

            for (int v = 0; v < 3; v++)
            {
                var mb = new MeshBuilder(2);
                Trunk(mb, 0, -0.4f, 3.6f, 0.3f, 0.18f, 6);
                int blobs = 2 + v % 2;
                for (int b = 0; b < blobs; b++)
                {
                    var c = new Vector3(((float)rng.NextDouble() - 0.5f) * 1.6f, 4.3f + (float)rng.NextDouble() * 1.2f, ((float)rng.NextDouble() - 0.5f) * 1.6f);
                    float r = 1.6f + (float)rng.NextDouble() * 0.8f;
                    Blob(mb, 1, c, new Vector3(r, r * 0.85f, r), 0.18f, rng);
                }
                var mesh = SaveMesh(mb.Build($"Mesh_Broadleaf_{v}"));
                kit.Broadleaves.Add(EnvPrefab($"Broadleaf_{v}", mesh, new[] { kit.Bark, leaves[v % 2] }, g =>
                {
                    TrunkCollider(g, 0.34f, 4f);
                    CanopyVolume(g, new Vector3(0f, 4.9f, 0f), 2.4f, 5f);
                }, 0.015f));
            }

            for (int v = 0; v < 5; v++)
            {
                var mb = new MeshBuilder(1);
                Blob(mb, 0, new Vector3(0f, 0.25f, 0f), new Vector3(1.0f, 0.65f + 0.1f * v, 0.8f), 0.28f, rng);
                var mesh = SaveMesh(mb.Build($"Mesh_Rock_{v}"));
                kit.Rocks.Add(EnvPrefab($"Rock_{v}", mesh, new[] { v % 2 == 0 ? kit.Rock : moss }, g =>
                {
                    var mc = g.AddComponent<MeshCollider>();
                    mc.sharedMesh = mesh;
                    mc.convex = true;
                }, 0.008f));
            }

            for (int v = 0; v < 3; v++)
            {
                var mb = new MeshBuilder(1);
                for (int b = 0; b < 3; b++)
                {
                    var c = new Vector3(((float)rng.NextDouble() - 0.5f) * 0.9f, 0.35f + (float)rng.NextDouble() * 0.25f, ((float)rng.NextDouble() - 0.5f) * 0.9f);
                    float r = 0.45f + (float)rng.NextDouble() * 0.3f;
                    Blob(mb, 0, c, new Vector3(r, r * 0.8f, r), 0.2f, rng);
                }
                var mesh = SaveMesh(mb.Build($"Mesh_Bush_{v}"));
                // Bushes don't block: they hide you, they don't stop you or bullets.
                kit.Bushes.Add(EnvPrefab($"Bush_{v}", mesh, new[] { bush }, g => CanopyVolume(g, new Vector3(0f, 0.5f, 0f), 0.85f, 1.7f), 0.02f));
            }
            return kit;
        }

        private static void MakeTransparent(Material m, float smoothness)
        {
            m.SetFloat("_Surface", 1f);
            m.SetFloat("_Blend", 0f);
            m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_ZWrite", 0f);
            m.SetFloat("_Smoothness", smoothness);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.renderQueue = (int)RenderQueue.Transparent;
            EditorUtility.SetDirty(m);
        }

        // ================================================================ scatter

        private static Vector3 OnGround(Terrain t, float x, float z, float sink = 0f) =>
            new Vector3(x, t.SampleHeight(new Vector3(x, 0f, z)) + t.transform.position.y - sink, z);

        private static readonly (Vector2 at, float radius)[] KeepClear =
        {
            (new Vector2(0f, 44f), 6f),          // mast
            (ForestLayout.LookoutHill, 10f),
            (ForestLayout.ThreeStones, 10f),
        };

        private static bool Blocked(Vector2 p, float clearingMargin, float trailMargin)
        {
            if (ForestLayout.DistanceToClearing(p) < clearingMargin) return true;
            if (NearestTrail(p, out _) < ForestLayout.TrailHalfWidth + trailMargin) return true;
            if ((p - ForestLayout.PondCentre).magnitude < ForestLayout.PondRadius + 2f) return true;
            if (Mathf.Abs(p.x) > ForestLayout.Boundary - 1f || Mathf.Abs(p.y) > ForestLayout.Boundary - 1f) return true;
            foreach (var (at, r) in KeepClear)
                if ((p - at).magnitude < r) return true;
            return false;
        }

        private static GameObject Place(GameObject prefab, Transform parent, Vector3 pos, float yaw, Vector3 scale)
        {
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
            go.transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, yaw, 0f));
            go.transform.localScale = scale;
            GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccludeeStatic);
            return go;
        }

        private static void Scatter(Transform root, Terrain terrain, ForestKit kit)
        {
            var rng = new System.Random(Seed + 1);
            float R() => (float)rng.NextDouble();
            float half = ForestLayout.Boundary;

            var trees = new GameObject("Trees").transform;
            trees.SetParent(root, false);
            int treeCount = 0;
            const float cell = 6.5f;
            for (float cz = -half; cz < half; cz += cell)
            for (float cx = -half; cx < half; cx += cell)
            {
                var p = new Vector2(cx + R() * cell, cz + R() * cell);
                if (Blocked(p, ForestLayout.ClearingTreeMargin, 1.5f)) continue;
                float dc = ForestLayout.DistanceToClearing(p);
                float density = Smooth(ForestLayout.ClearingTreeMargin, 26f, dc) * (0.55f + 0.45f * Fbm(p.x, p.y, 1f / 40f, 2, 91f));
                if (R() > density * 0.9f) continue;
                bool broad = R() < Mathf.Lerp(0.55f, 0.2f, Smooth(8f, 40f, dc)); // more broadleaf at the forest edge
                var list = broad ? kit.Broadleaves : kit.Pines;
                var prefab = list[rng.Next(list.Count)];
                float s = 0.75f + R() * 0.6f;
                Place(prefab, trees, OnGround(terrain, p.x, p.y, 0.1f), R() * 360f, new Vector3(s, s * (0.9f + R() * 0.25f), s));
                treeCount++;
            }

            var rocks = new GameObject("Rocks").transform;
            rocks.SetParent(root, false);
            int rockCount = 0;
            for (float cz = -half; cz < half; cz += 17f)
            for (float cx = -half; cx < half; cx += 17f)
            {
                var p = new Vector2(cx + R() * 17f, cz + R() * 17f);
                if (Blocked(p, 1.5f, 1f) || R() > 0.42f) continue;
                int cluster = 1 + rng.Next(3);
                for (int i = 0; i < cluster; i++)
                {
                    var q = p + new Vector2(R() - 0.5f, R() - 0.5f) * 3f;
                    if (Blocked(q, 1.5f, 1f)) continue;
                    float s = Mathf.Lerp(0.5f, 2.2f, R() * R());
                    Place(kit.Rocks[rng.Next(kit.Rocks.Count)], rocks, OnGround(terrain, q.x, q.y, s * 0.15f), R() * 360f,
                        new Vector3(s * (0.8f + R() * 0.5f), s * (0.7f + R() * 0.5f), s * (0.8f + R() * 0.5f)));
                    rockCount++;
                }
            }
            // Cover along the range's south side and the yard's west edge.
            foreach (var c in new[] { new Vector2(34f, -15f), new Vector2(52f, 15.5f), new Vector2(70f, -15.5f), new Vector2(-33f, 8f), new Vector2(-34f, -16f) })
            {
                float s = 1.4f + R() * 0.6f;
                Place(kit.Rocks[rng.Next(kit.Rocks.Count)], rocks, OnGround(terrain, c.x, c.y, 0.2f), R() * 360f, new Vector3(s * 1.3f, s, s));
                rockCount++;
            }

            var bushes = new GameObject("Bushes").transform;
            bushes.SetParent(root, false);
            int bushCount = 0;
            for (float cz = -half; cz < half; cz += 9f)
            for (float cx = -half; cx < half; cx += 9f)
            {
                var p = new Vector2(cx + R() * 9f, cz + R() * 9f);
                if (Blocked(p, 2f, 0.6f) || R() > 0.33f) continue;
                float s = 0.8f + R() * 0.7f;
                Place(kit.Bushes[rng.Next(kit.Bushes.Count)], bushes, OnGround(terrain, p.x, p.y, 0.1f), R() * 360f, Vector3.one * s);
                bushCount++;
            }
            Debug.Log($"[ForestSoldierSetup] forest: {treeCount} trees, {rockCount} rocks, {bushCount} bushes");
        }

        // ================================================================ landmarks

        private static GameObject Beam(string name, Transform parent, Vector3 pos, Vector3 size, Quaternion rot, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.SetPositionAndRotation(pos, rot);
            go.transform.localScale = size;
            go.GetComponent<Renderer>().sharedMaterial = mat;
            go.isStatic = true;
            return go;
        }

        private static void BuildLandmarks(Transform root, Terrain terrain, ForestKit kit)
        {
            var marks = new GameObject("Landmarks").transform;
            marks.SetParent(root, false);

            // Fire lookout on the north-west hill: rises above the canopy.
            var lookout = new GameObject("Lookout_Tower").transform;
            lookout.SetParent(marks, false);
            Vector3 b = OnGround(terrain, ForestLayout.LookoutHill.x, ForestLayout.LookoutHill.y);
            lookout.position = b;
            const float legH = 13f, w = 2.2f;
            foreach (var (sx, sz) in new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) })
            {
                var leg = new Vector3(sx * w, 0f, sz * w);
                Beam("Leg", lookout, b + leg * 1.2f + Vector3.up * (legH * 0.5f - 0.5f), new Vector3(0.35f, legH + 1f, 0.35f),
                    Quaternion.Euler(sz * -2.5f, 0f, sx * 2.5f), kit.Wood);
            }
            for (int i = 1; i <= 3; i++)
            {
                float y = i * legH / 4f;
                Beam("Brace", lookout, b + new Vector3(0f, y, -w * 1.15f), new Vector3(w * 2.4f, 0.18f, 0.18f), Quaternion.identity, kit.Wood);
                Beam("Brace", lookout, b + new Vector3(0f, y, w * 1.15f), new Vector3(w * 2.4f, 0.18f, 0.18f), Quaternion.identity, kit.Wood);
                Beam("Brace", lookout, b + new Vector3(-w * 1.15f, y, 0f), new Vector3(0.18f, 0.18f, w * 2.4f), Quaternion.identity, kit.Wood);
                Beam("Brace", lookout, b + new Vector3(w * 1.15f, y, 0f), new Vector3(0.18f, 0.18f, w * 2.4f), Quaternion.identity, kit.Wood);
            }
            Beam("Cabin", lookout, b + Vector3.up * (legH + 1.2f), new Vector3(w * 2.3f, 2.4f, w * 2.3f), Quaternion.identity, kit.Wood);
            Beam("Roof", lookout, b + Vector3.up * (legH + 2.7f), new Vector3(w * 2.8f, 0.5f, w * 2.8f), Quaternion.Euler(0f, 45f, 0f), kit.Roof);
            Beam("RoofCap", lookout, b + Vector3.up * (legH + 3.2f), new Vector3(w * 1.6f, 0.5f, w * 1.6f), Quaternion.Euler(0f, 45f, 0f), kit.Roof);

            // Radio mast north of the yard, with a red light on top.
            var mast = new GameObject("Radio_Mast").transform;
            mast.SetParent(marks, false);
            Vector3 m = OnGround(terrain, 0f, 44f);
            Beam("Mast", mast, m + Vector3.up * 9f, new Vector3(0.5f, 18f, 0.5f), Quaternion.identity, ColorMat("Proto_Landmark", new Color(0.9f, 0.45f, 0.15f)));
            Beam("Base", mast, m + Vector3.up * 0.4f, new Vector3(2f, 0.8f, 2f), Quaternion.identity, kit.Rock);
            Beam("Signal", mast, m + Vector3.up * 18.3f, new Vector3(0.6f, 0.6f, 0.6f), Quaternion.identity, kit.Signal);

            // Three standing stones in the south-east.
            var stones = new GameObject("Three_Stones").transform;
            stones.SetParent(marks, false);
            for (int i = 0; i < 3; i++)
            {
                float a = i * Mathf.PI * 2f / 3f + 0.4f;
                var at = ForestLayout.ThreeStones + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * 3.2f;
                Place(kit.Rocks[i], stones, OnGround(terrain, at.x, at.y, 0.4f), i * 70f, new Vector3(2.2f, 5.5f + i, 2f));
            }

            // Pond surface.
            var water = GameObject.CreatePrimitive(PrimitiveType.Plane);
            water.name = "Pond_Water";
            water.transform.SetParent(marks, false);
            Object.DestroyImmediate(water.GetComponent<Collider>());
            water.transform.position = new Vector3(ForestLayout.PondCentre.x, ForestLayout.WaterLevel, ForestLayout.PondCentre.y);
            float d = ForestLayout.PondRadius * 2.6f / 10f;
            water.transform.localScale = new Vector3(d, 1f, d);
            var wr = water.GetComponent<Renderer>();
            wr.sharedMaterial = kit.Water;
            wr.shadowCastingMode = ShadowCastingMode.Off;
        }

        private static void BuildBoundary(Transform root)
        {
            var bounds = new GameObject("Boundary").transform;
            bounds.SetParent(root, false);
            float b = ForestLayout.Boundary, len = b * 2f + 4f;
            void Wall(string n, Vector3 pos, Vector3 size)
            {
                var go = new GameObject(n);
                go.transform.SetParent(bounds, false);
                go.transform.position = pos;
                go.AddComponent<BoxCollider>().size = size;
                go.isStatic = true;
            }
            Wall("North", new Vector3(0f, 20f, b + 1f), new Vector3(len, 60f, 2f));
            Wall("South", new Vector3(0f, 20f, -b - 1f), new Vector3(len, 60f, 2f));
            Wall("East", new Vector3(b + 1f, 20f, 0f), new Vector3(2f, 60f, len));
            Wall("West", new Vector3(-b - 1f, 20f, 0f), new Vector3(2f, 60f, len));
        }

        // ================================================================ atmosphere

        private static void BuildAtmosphere(Light sun, Camera cam)
        {
            sun.transform.rotation = Quaternion.Euler(38f, -32f, 0f);
            sun.color = new Color(1f, 0.93f, 0.8f);
            sun.intensity = 1.5f;
            sun.shadowStrength = 0.85f;

            string skyPath = $"{Root}/Materials/Env_Sky.mat";
            var sky = AssetDatabase.LoadAssetAtPath<Material>(skyPath);
            if (sky == null)
            {
                sky = new Material(Shader.Find("Skybox/Procedural"));
                AssetDatabase.CreateAsset(sky, skyPath);
            }
            sky.SetFloat("_SunSize", 0.035f);
            sky.SetFloat("_AtmosphereThickness", 0.9f);
            sky.SetColor("_SkyTint", new Color(0.52f, 0.58f, 0.64f));
            sky.SetColor("_GroundColor", new Color(0.36f, 0.40f, 0.35f));
            sky.SetFloat("_Exposure", 1.15f);
            EditorUtility.SetDirty(sky);
            RenderSettings.skybox = sky;

            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.56f, 0.64f, 0.72f);
            RenderSettings.ambientEquatorColor = new Color(0.40f, 0.46f, 0.38f);
            RenderSettings.ambientGroundColor = new Color(0.20f, 0.20f, 0.15f);

            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogDensity = 0.0062f;
            RenderSettings.fogColor = new Color(0.68f, 0.75f, 0.78f);

            cam.farClipPlane = 500f;
            var camData = cam.GetUniversalAdditionalCameraData();
            camData.renderPostProcessing = true;
            camData.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;

            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>("Assets/Settings/SampleSceneProfile.asset");
            if (profile != null)
            {
                var vol = new GameObject("Post Processing").AddComponent<Volume>();
                vol.isGlobal = true;
                vol.sharedProfile = profile;
            }

            // Forest shadows reach a little further than the template's default.
            foreach (var path in new[] { "Assets/Settings/PC_RPAsset.asset" })
            {
                var urp = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(path);
                if (urp == null) continue;
                urp.shadowDistance = 90f;
                EditorUtility.SetDirty(urp);
            }
        }

        private static void BuildAmbience(Transform root)
        {
            const int rate = 44100;
            const float seconds = 24f, fade = 1.5f;
            int n = (int)(rate * seconds), nf = (int)(rate * fade);
            var rng = new System.Random(Seed + 2);
            var s = new float[n + nf];
            float lp = 0f, lp2 = 0f;
            for (int i = 0; i < s.Length; i++)
            {
                float t = i / (float)rate;
                float noise = (float)rng.NextDouble() * 2f - 1f;
                lp += 0.02f * (noise - lp);    // low wind
                lp2 += 0.25f * (noise - lp2);  // leaf hiss
                float gust = 0.55f + 0.45f * Mathf.Sin(t * 0.37f) * Mathf.Sin(t * 0.11f + 1f);
                s[i] = lp * 2.2f * gust + lp2 * 0.035f * gust * gust;
            }
            // birds: short rising chirps in little phrases
            for (int phrase = 0; phrase < 9; phrase++)
            {
                float start = (float)rng.NextDouble() * (seconds - 2f);
                float pitch = 2400f + (float)rng.NextDouble() * 1800f;
                int chirps = 2 + rng.Next(4);
                for (int c = 0; c < chirps; c++)
                {
                    float t0 = start + c * (0.12f + (float)rng.NextDouble() * 0.08f);
                    int i0 = (int)(t0 * rate), len = (int)(0.07f * rate);
                    float ph = 0f;
                    for (int k = 0; k < len && i0 + k < s.Length; k++)
                    {
                        float u = k / (float)len;
                        ph += 2f * Mathf.PI * pitch * (1f + 0.35f * u) / rate;
                        s[i0 + k] += 0.12f * Mathf.Sin(ph) * Mathf.Sin(Mathf.PI * u);
                    }
                }
            }
            // crossfade the tail into the head so it loops without a click
            var loop = new float[n];
            for (int i = 0; i < n; i++) loop[i] = s[i];
            for (int i = 0; i < nf; i++)
            {
                float w = i / (float)nf;
                loop[i] = s[i] * w + s[n + i] * (1f - w);
            }
            var clip = WriteClip("ForestAmbience", Normalize(loop, 0.6f), rate);

            var go = new GameObject("Ambience");
            go.transform.SetParent(root, false);
            var src = go.AddComponent<AudioSource>();
            src.clip = clip;
            src.loop = true;
            src.playOnAwake = true;
            src.spatialBlend = 0f;
            src.volume = 0.35f;
        }
    }
}
