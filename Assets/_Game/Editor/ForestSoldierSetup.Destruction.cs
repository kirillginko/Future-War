using System.Collections.Generic;
using System.IO;
using System.Linq;
using ForestSoldier.Combat;
using ForestSoldier.Destruction;
using ForestSoldier.Environment;
using ForestSoldier.Player;
using UnityEditor;
using UnityEngine;

namespace ForestSoldier.EditorTools
{
    /// <summary>
    /// Destructible houses (pre-fractured, one skinned mesh each, one bone per
    /// fragment, one shared palette material), the under-barrel grenade
    /// launcher, its explosion, and mesh-particle shell casings.
    /// </summary>
    public static partial class ForestSoldierSetup
    {
        private const string DestructionArt = Root + "/Art/Environment/Houses";
        private const int PaletteSize = 8;
        // Palette slots
        private const int PalPlaster = 0, PalBrick = 1, PalStone = 2, PalWhite = 3, PalRoof = 4, PalBroken = 5, PalSand = 6, PalBlue = 7;

        private static Material houseMat;
        private static readonly List<GameObject> housePrefabs = new List<GameObject>();
        private static GameObject launcherRound;
        private static ExplosionEffect explosionPrefab;
        private static ParticleSystem casingPrefab;
        private static AudioClip boomClip, launchThumpClip;
        private static Material gunMetal;

        private static void BuildDestructionAssets()
        {
            Directory.CreateDirectory(DestructionArt);
            BuildHousePalette();
            housePrefabs.Clear();
            int[] walls = { PalPlaster, PalBrick, PalWhite, PalStone };
            for (int i = 0; i < ForestLayout.Houses.Length; i++)
                housePrefabs.Add(BuildHouse(i, walls[i % walls.Length], 1000 + i * 17));
            BuildLauncherAssets();
            BuildCasings();
            // Bullet streaks glow: HDR tint on the additive tracer material so the bloom catches them.
            tracerMat.SetColor("_BaseColor", new Color(2.6f, 2.0f, 1.3f, 1f));
            EditorUtility.SetDirty(tracerMat);
        }

        // ================================================================ houses

        private static void BuildHousePalette()
        {
            var colors = new Color[PaletteSize];
            colors[PalPlaster] = new Color(0.80f, 0.73f, 0.60f);
            colors[PalBrick] = new Color(0.62f, 0.33f, 0.25f);
            colors[PalStone] = new Color(0.56f, 0.56f, 0.53f);
            colors[PalWhite] = new Color(0.88f, 0.87f, 0.83f);
            colors[PalRoof] = new Color(0.33f, 0.22f, 0.18f);
            colors[PalBroken] = new Color(0.47f, 0.43f, 0.38f); // the cut faces: rough, darker
            colors[PalSand] = new Color(0.62f, 0.55f, 0.40f);
            colors[PalBlue] = new Color(0.25f, 0.40f, 0.65f);
            string path = $"{DestructionArt}/HousePalette.png";
            var tex = new Texture2D(PaletteSize, 1, TextureFormat.RGB24, false);
            tex.SetPixels(colors);
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path);
            var ti = (TextureImporter)AssetImporter.GetAtPath(path);
            ti.filterMode = FilterMode.Point;
            ti.mipmapEnabled = false;
            ti.wrapMode = TextureWrapMode.Clamp;
            ti.textureCompression = TextureImporterCompression.Uncompressed;
            ti.SaveAndReimport();
            houseMat = ColorMat("Env_House", Color.white);
            houseMat.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(path));
            houseMat.SetFloat("_Smoothness", 0.08f);
            EditorUtility.SetDirty(houseMat);
        }

        private static Vector2 PaletteUV(int slot) => new Vector2((slot + 0.5f) / PaletteSize, 0.5f);

        private struct Piece
        {
            public Vector3 Centre, Size; // root-local, axis-aligned
            public int FaceSlot;         // palette slot for faces on the original surface
            public Bounds Outer;         // the original block: faces on its surface are painted, cut faces look broken
        }

        private struct Opening
        {
            public float U0, U1, V0, V1;
            public Opening(float u0, float u1, float v0, float v1) { U0 = u0; U1 = u1; V0 = v0; V1 = v1; }
        }

        /// <summary>Rect minus rect: up to four pieces (left, right, below, above the hole).</summary>
        private static IEnumerable<Rect> Subtract(Rect cell, Opening o)
        {
            var hole = Rect.MinMaxRect(o.U0, o.V0, o.U1, o.V1);
            if (!cell.Overlaps(hole))
            {
                yield return cell;
                yield break;
            }
            float hx0 = Mathf.Max(cell.xMin, hole.xMin), hx1 = Mathf.Min(cell.xMax, hole.xMax);
            float hy0 = Mathf.Max(cell.yMin, hole.yMin), hy1 = Mathf.Min(cell.yMax, hole.yMax);
            if (hx0 > cell.xMin) yield return Rect.MinMaxRect(cell.xMin, cell.yMin, hx0, cell.yMax);
            if (hx1 < cell.xMax) yield return Rect.MinMaxRect(hx1, cell.yMin, cell.xMax, cell.yMax);
            if (hy0 > cell.yMin) yield return Rect.MinMaxRect(hx0, cell.yMin, hx1, hy0);
            if (hy1 < cell.yMax) yield return Rect.MinMaxRect(hx0, hy1, hx1, cell.yMax);
        }

        /// <summary>
        /// A wall as staggered, uneven blocks. origin: the wall's (u=0, v=0) corner on its centre plane;
        /// uAxis along the wall, normal through it.
        /// </summary>
        private static void FractureWall(List<Piece> pieces, Vector3 origin, Vector3 uAxis, Vector3 normal,
            float length, float height, float thickness, Opening[] openings, int slot, System.Random rng)
        {
            float R(float a, float b) => a + (float)rng.NextDouble() * (b - a);
            float v = 0f;
            int row = 0;
            while (v < height - 0.01f)
            {
                float rh = Mathf.Min(R(0.42f, 0.72f), height - v);
                if (height - (v + rh) < 0.25f) rh = height - v; // no sliver on top
                float u = row % 2 == 0 ? 0f : -R(0.2f, 0.5f);   // stagger like coursed stone
                while (u < length - 0.01f)
                {
                    float w = R(0.45f, 1.1f);
                    float u0 = Mathf.Max(0f, u), u1 = Mathf.Min(length, u + w);
                    if (length - u1 < 0.2f) u1 = length;
                    IEnumerable<Rect> parts = new[] { Rect.MinMaxRect(u0, v, u1, v + rh) };
                    foreach (var o in openings) parts = parts.SelectMany(p => Subtract(p, o)).ToList();
                    foreach (var p in parts)
                    {
                        if (p.width < 0.08f || p.height < 0.08f) continue;
                        Vector3 c = origin + uAxis * p.center.x + Vector3.up * p.center.y;
                        Vector3 size = Abs(uAxis * p.width) + Vector3.up * p.height + Abs(normal * thickness);
                        Vector3 wallCentre = origin + uAxis * (length * 0.5f) + Vector3.up * (height * 0.5f);
                        Vector3 wallSize = Abs(uAxis * length) + Vector3.up * height + Abs(normal * thickness);
                        pieces.Add(new Piece { Centre = c, Size = size, FaceSlot = slot, Outer = new Bounds(wallCentre, wallSize) });
                    }
                    u = u1;
                }
                v += rh;
                row++;
            }
        }

        private static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

        private static GameObject BuildHouse(int index, int wallSlot, int seed)
        {
            var rng = new System.Random(seed);
            const float W = 6f, D = 5f, H = 3f, T = 0.25f, roofT = 0.22f, eave = 0.15f;
            float hw = W * 0.5f, hd = D * 0.5f;
            var pieces = new List<Piece>();

            // Front (door + two windows), back (two windows), sides (one window each).
            FractureWall(pieces, new Vector3(-hw, 0f, -hd + T * 0.5f), Vector3.right, Vector3.back, W, H, T,
                new[] { new Opening(hw - 0.5f, hw + 0.5f, 0f, 2.1f), new Opening(0.8f, 1.8f, 1.0f, 2.0f), new Opening(W - 1.8f, W - 0.8f, 1.0f, 2.0f) }, wallSlot, rng);
            FractureWall(pieces, new Vector3(-hw, 0f, hd - T * 0.5f), Vector3.right, Vector3.forward, W, H, T,
                new[] { new Opening(1.2f, 2.4f, 1.0f, 2.0f), new Opening(W - 2.4f, W - 1.2f, 1.0f, 2.0f) }, wallSlot, rng);
            float side = D - 2f * T;
            FractureWall(pieces, new Vector3(-hw + T * 0.5f, 0f, -hd + T), Vector3.forward, Vector3.left, side, H, T,
                new[] { new Opening(side * 0.5f - 0.6f, side * 0.5f + 0.6f, 1.0f, 2.0f) }, wallSlot, rng);
            FractureWall(pieces, new Vector3(hw - T * 0.5f, 0f, -hd + T), Vector3.forward, Vector3.right, side, H, T,
                new[] { new Opening(side * 0.5f - 0.6f, side * 0.5f + 0.6f, 1.0f, 2.0f) }, wallSlot, rng);

            // Flat roof in 3 x 3 slabs with uneven seams.
            float[] xs = { -hw - eave, -1f + (float)rng.NextDouble() * 0.6f - 0.3f, 1f + (float)rng.NextDouble() * 0.6f - 0.3f, hw + eave };
            float[] zs = { -hd - eave, -0.8f + (float)rng.NextDouble() * 0.5f - 0.25f, 0.8f + (float)rng.NextDouble() * 0.5f - 0.25f, hd + eave };
            for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                var c = new Vector3((xs[i] + xs[i + 1]) * 0.5f, H + roofT * 0.5f, (zs[j] + zs[j + 1]) * 0.5f);
                var s = new Vector3(xs[i + 1] - xs[i], roofT, zs[j + 1] - zs[j]);
                pieces.Add(new Piece { Centre = c, Size = s, FaceSlot = PalRoof,
                    Outer = new Bounds(new Vector3(0f, H + roofT * 0.5f, 0f), new Vector3(W + 2f * eave, roofT, D + 2f * eave)) });
            }

            var root = BuildFractured($"House_{index}", pieces, $"{DestructionArt}/Mesh_House_{index}.asset",
                new Bounds(new Vector3(0f, H * 0.5f + 0.2f, 0f), new Vector3(W + 0.6f, H + 1f, D + 0.6f)));
            try
            {
                Directory.CreateDirectory(EnvPrefabs);
                return PrefabUtility.SaveAsPrefabAsset(root, $"{EnvPrefabs}/House_{index}.prefab");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        /// <summary>
        /// Pieces → one GameObject: a bone (with a static box collider) per piece, one skinned mesh
        /// (24 vertices a piece, each weighted 1 to its bone), the support graph, a DestructibleStructure.
        /// The caller places it, saves it as a prefab, or leaves it in the scene.
        /// </summary>
        private static GameObject BuildFractured(string name, List<Piece> pieces, string meshPath, Bounds bounds)
        {
            var root = new GameObject(name);
            int n = pieces.Count;
            var bones = new Transform[n];
            var sizes = new Vector3[n];
            var anchored = new bool[n];
            var bind = new Matrix4x4[n];
            float groundY = float.MaxValue;
            foreach (var p in pieces) groundY = Mathf.Min(groundY, p.Centre.y - p.Size.y * 0.5f);
            for (int i = 0; i < n; i++)
            {
                var b = new GameObject($"F{i:000}").transform;
                b.SetParent(root.transform, false);
                b.localPosition = pieces[i].Centre;
                b.gameObject.AddComponent<BoxCollider>().size = pieces[i].Size;
                bones[i] = b;
                sizes[i] = pieces[i].Size;
                anchored[i] = pieces[i].Centre.y - pieces[i].Size.y * 0.5f < groundY + 0.01f;
                bind[i] = b.worldToLocalMatrix * root.transform.localToWorldMatrix;
            }
            var start = new List<int>(n + 1);
            var neigh = new List<int>();
            const float touch = 0.03f;
            for (int i = 0; i < n; i++)
            {
                start.Add(neigh.Count);
                var a = new Bounds(pieces[i].Centre, pieces[i].Size + Vector3.one * touch);
                for (int j = 0; j < n; j++)
                    if (j != i && a.Intersects(new Bounds(pieces[j].Centre, pieces[j].Size))) neigh.Add(j);
            }
            start.Add(neigh.Count);

            var verts = new List<Vector3>(n * 24);
            var norms = new List<Vector3>(n * 24);
            var uvs = new List<Vector2>(n * 24);
            var weights = new List<BoneWeight>(n * 24);
            var tris = new List<int>(n * 36);
            Vector3[] faceN = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
            for (int i = 0; i < n; i++)
            {
                var p = pieces[i];
                Vector3 h = p.Size * 0.5f;
                foreach (var fn in faceN)
                {
                    Vector3 t1 = Mathf.Abs(fn.y) > 0.5f ? Vector3.right : Vector3.up;
                    Vector3 t2 = Vector3.Cross(fn, t1);
                    Vector3 fc = p.Centre + Vector3.Scale(fn, h);
                    Vector3 e1 = Vector3.Scale(t1, h), e2 = Vector3.Scale(t2, h);
                    int baseV = verts.Count;
                    verts.Add(fc - e1 - e2);
                    verts.Add(fc - e1 + e2);
                    verts.Add(fc + e1 + e2);
                    verts.Add(fc + e1 - e2);
                    // On the original block's surface: painted. Anywhere inside: a cut face.
                    float along = Vector3.Dot(fc, fn);
                    float surface = Vector3.Dot(p.Outer.center + Vector3.Scale(fn, p.Outer.extents), fn);
                    var uv = PaletteUV(Mathf.Abs(along - surface) < 0.002f ? p.FaceSlot : PalBroken);
                    for (int k = 0; k < 4; k++)
                    {
                        norms.Add(fn);
                        uvs.Add(uv);
                        weights.Add(new BoneWeight { boneIndex0 = i, weight0 = 1f });
                    }
                    Vector3 cr = Vector3.Cross(verts[baseV + 1] - verts[baseV], verts[baseV + 2] - verts[baseV]);
                    if (Vector3.Dot(cr, fn) > 0f)
                        tris.AddRange(new[] { baseV, baseV + 1, baseV + 2, baseV, baseV + 2, baseV + 3 });
                    else
                        tris.AddRange(new[] { baseV, baseV + 2, baseV + 1, baseV, baseV + 3, baseV + 2 });
                }
            }
            var mesh = new Mesh { name = Path.GetFileNameWithoutExtension(meshPath), indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(verts);
            mesh.SetNormals(norms);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.boneWeights = weights.ToArray();
            mesh.bindposes = bind;
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            AssetDatabase.DeleteAsset(meshPath);
            AssetDatabase.CreateAsset(mesh, meshPath);

            var smr = root.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = mesh;
            smr.sharedMaterial = houseMat;
            smr.bones = bones;
            smr.rootBone = root.transform;
            smr.quality = SkinQuality.Bone1;
            smr.updateWhenOffscreen = false;
            smr.skinnedMotionVectors = false;
            smr.localBounds = bounds;

            var ds = root.AddComponent<DestructibleStructure>();
            var so = new SerializedObject(ds);
            so.FindProperty("meshRenderer").objectReferenceValue = smr;
            SetArray(so.FindProperty("fragments"), bones, (pp, v) => pp.objectReferenceValue = v);
            SetArray(so.FindProperty("sizes"), sizes, (pp, v) => pp.vector3Value = v);
            SetArray(so.FindProperty("anchored"), anchored, (pp, v) => pp.boolValue = v);
            SetArray(so.FindProperty("neighbourStart"), start.ToArray(), (pp, v) => pp.intValue = v);
            SetArray(so.FindProperty("neighbourIndex"), neigh.ToArray(), (pp, v) => pp.intValue = v);
            so.ApplyModifiedPropertiesWithoutUndo();
            Debug.Log($"[ForestSoldierSetup] {name}: {n} fragments (bones), {verts.Count} vertices, {neigh.Count / 2} neighbour links, 1 skinned mesh");
            return root;
        }

        // ================================================================ yard and range structures

        /// <summary>
        /// A solid box as coursed blocks: rows up the height, staggered cuts along the
        /// longer horizontal side, and a split across the shorter side if it's thick.
        /// Cell size grows with the box so long walls stay a few hundred pieces.
        /// </summary>
        private static void FractureBlock(List<Piece> pieces, Vector3 centre, Vector3 size, int slot, System.Random rng)
        {
            float R(float a, float b) => a + (float)rng.NextDouble() * (b - a);
            float cell = Mathf.Clamp(Mathf.Max(size.x, size.y, size.z) / 28f, 0.42f, 0.9f);
            var outer = new Bounds(centre, size);
            bool alongX = size.x >= size.z;
            float lenA = alongX ? size.x : size.z, lenB = alongX ? size.z : size.x;
            int splitsB = Mathf.Max(1, Mathf.RoundToInt(lenB / (cell * 1.2f)));
            Vector3 min = centre - size * 0.5f;
            float y = 0f;
            int row = 0;
            while (y < size.y - 0.01f)
            {
                float rh = Mathf.Min(R(0.75f, 1.2f) * cell, size.y - y);
                if (size.y - (y + rh) < cell * 0.35f) rh = size.y - y;
                for (int k = 0; k < splitsB; k++)
                {
                    float b0 = lenB * k / splitsB, b1 = lenB * (k + 1) / splitsB;
                    float a = (row + k) % 2 == 0 ? 0f : -R(0.25f, 0.6f) * cell;
                    while (a < lenA - 0.01f)
                    {
                        float a0 = Mathf.Max(0f, a), a1 = Mathf.Min(lenA, a + R(0.6f, 1.4f) * cell);
                        if (lenA - a1 < cell * 0.3f) a1 = lenA;
                        Vector3 lo = alongX ? new Vector3(a0, y, b0) : new Vector3(b0, y, a0);
                        Vector3 hi = alongX ? new Vector3(a1, y + rh, b1) : new Vector3(b1, y + rh, a1);
                        pieces.Add(new Piece { Centre = min + (lo + hi) * 0.5f, Size = hi - lo, FaceSlot = slot, Outer = outer });
                        a = a1;
                    }
                }
                y += rh;
                row++;
            }
        }

        private static readonly (string name, int slot)[] DestructibleBlocks =
        {
            ("Wall_North", PalStone), ("Wall_Corner_A", PalStone), ("Wall_Corner_B", PalStone),
            ("Pillar_0", PalStone), ("Pillar_1", PalStone), ("Pillar_2", PalStone), ("Pillar_3", PalStone),
            ("Pillar_4", PalStone), ("Pillar_5", PalStone), ("Pillar_6", PalStone), ("Pillar_7", PalStone),
            ("FiringLine_Bench", PalSand), ("Cover_Crate_50m", PalSand), ("Cover_Wall_25m", PalStone), ("Backstop", PalSand),
        };

        /// <summary>Swap the yard's and range's solid blocks for fractured ones, in place, keeping their names.</summary>
        private static void MakeBlocksDestructible()
        {
            var rng = new System.Random(4242);
            string dir = DestructionArt + "/Blocks";
            Directory.CreateDirectory(dir);
            foreach (var (name, slot) in DestructibleBlocks)
            {
                var block = GameObject.Find(name);
                if (block == null)
                {
                    Debug.LogWarning($"[ForestSoldierSetup] no block named {name} to make destructible");
                    continue;
                }
                var t = block.transform;
                Vector3 size = t.lossyScale;
                var pieces = new List<Piece>();
                FractureBlock(pieces, Vector3.zero, size, slot, rng);
                var root = BuildFractured(name, pieces, $"{dir}/Mesh_{name}.asset", new Bounds(Vector3.zero, size + Vector3.one * 0.5f));
                root.transform.SetParent(t.parent, false);
                root.transform.SetPositionAndRotation(t.position, t.rotation);
                Object.DestroyImmediate(block);
            }

            // The low roof and its two posts are one structure: take the posts out and the roof comes down.
            var roof = GameObject.Find("LowRoof");
            var postA = GameObject.Find("LowRoof_Post_A");
            var postB = GameObject.Find("LowRoof_Post_B");
            if (roof != null && postA != null && postB != null)
            {
                var origin = roof.transform.position;
                var pieces = new List<Piece>();
                FractureBlock(pieces, postA.transform.position - origin, postA.transform.lossyScale, PalStone, rng);
                FractureBlock(pieces, postB.transform.position - origin, postB.transform.lossyScale, PalStone, rng);
                Vector3 rs = roof.transform.lossyScale;
                // the slab as 3 x 3 panels
                for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                {
                    var c = new Vector3((i - 1) * rs.x / 3f, 0f, (j - 1) * rs.z / 3f);
                    pieces.Add(new Piece { Centre = c, Size = new Vector3(rs.x / 3f, rs.y, rs.z / 3f), FaceSlot = PalBlue,
                        Outer = new Bounds(Vector3.zero, rs) });
                }
                var root = BuildFractured("LowRoof", pieces, $"{dir}/Mesh_LowRoof.asset", new Bounds(Vector3.zero, new Vector3(6f, 6f, 6f)));
                root.transform.SetParent(roof.transform.parent, false);
                root.transform.position = origin;
                Object.DestroyImmediate(roof);
                Object.DestroyImmediate(postA);
                Object.DestroyImmediate(postB);
            }
        }

        private static void SetArray<T>(SerializedProperty prop, T[] values, System.Action<SerializedProperty, T> set)
        {
            prop.arraySize = values.Length;
            for (int i = 0; i < values.Length; i++) set(prop.GetArrayElementAtIndex(i), values[i]);
        }

        private static void PlaceHouses(Camera cam)
        {
            var parent = new GameObject("Hamlet").transform;
            for (int i = 0; i < ForestLayout.Houses.Length && i < housePrefabs.Count; i++)
            {
                var h = ForestLayout.Houses[i];
                var go = (GameObject)PrefabUtility.InstantiatePrefab(housePrefabs[i], parent);
                go.transform.SetPositionAndRotation(new Vector3(h.x, 0f, h.y), Quaternion.Euler(0f, h.z, 0f));
            }
            if (cam.GetComponent<CameraShake>() == null) cam.gameObject.AddComponent<CameraShake>();
            MakeBlocksDestructible();
        }

        // ================================================================ launcher and explosion

        private static float[] Synth(float seconds, int rate, System.Func<float, System.Func<float>, float> f, int seed)
        {
            var rng = new System.Random(seed);
            float Noise() => (float)(rng.NextDouble() * 2.0 - 1.0);
            var s = new float[(int)(seconds * rate)];
            for (int i = 0; i < s.Length; i++) s[i] = f(i / (float)rate, Noise);
            return s;
        }

        private static void BuildLauncherAssets()
        {
            const int rate = 44100;
            // Boom: a crack, a body of low noise, a sub thump and a long rumble.
            float lp1 = 0f, lp2 = 0f, ph = 0f;
            boomClip = WriteClip("GrenadeBoom", Normalize(Synth(2.6f, rate, (t, noise) =>
            {
                float n = noise();
                lp1 += 0.05f * (n - lp1);
                lp2 += 0.008f * (n - lp2);
                ph += 2f * Mathf.PI * Mathf.Lerp(55f, 30f, Mathf.Clamp01(t / 0.6f)) / rate;
                float crack = n * Mathf.Exp(-t / 0.012f);
                float body = lp1 * 3f * Mathf.Exp(-t / 0.3f);
                float sub = Mathf.Sin(ph) * 0.9f * Mathf.Exp(-t / 0.45f);
                float rumble = lp2 * 9f * Mathf.Exp(-t / 1.1f);
                return (crack * 0.6f + body + sub + rumble) * Mathf.Clamp01(t / 0.0008f);
            }, 51), 0.95f), rate);
            // Launch: a hollow "thoomp".
            float lp3 = 0f, ph2 = 0f;
            launchThumpClip = WriteClip("GrenadeLaunch", Normalize(Synth(0.4f, rate, (t, noise) =>
            {
                float n = noise();
                lp3 += 0.18f * (n - lp3);
                ph2 += 2f * Mathf.PI * Mathf.Lerp(150f, 60f, Mathf.Clamp01(t / 0.12f)) / rate;
                return (Mathf.Sin(ph2) * Mathf.Exp(-t / 0.07f) + lp3 * 1.6f * Mathf.Exp(-t / 0.035f)) * Mathf.Clamp01(t / 0.001f);
            }, 52), 0.9f), rate);

            gunMetal = ColorMat("Weapon_GunMetal", new Color(0.13f, 0.14f, 0.13f));
            gunMetal.SetFloat("_Smoothness", 0.35f);
            gunMetal.SetFloat("_Metallic", 0.6f);
            var olive = ColorMat("Weapon_Grenade", new Color(0.32f, 0.36f, 0.2f));

            // 40 mm round with a short smoke trail.
            var round = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            try
            {
                round.name = "Grenade40mm";
                Object.DestroyImmediate(round.GetComponent<Collider>());
                round.transform.localScale = new Vector3(0.045f, 0.045f, 0.075f);
                round.GetComponent<Renderer>().sharedMaterial = olive;
                var trail = round.AddComponent<TrailRenderer>();
                trail.time = 0.35f;
                trail.widthMultiplier = 0.05f;
                trail.widthCurve = AnimationCurve.Linear(0f, 1f, 1f, 0f);
                trail.sharedMaterial = impactMat;
                trail.startColor = new Color(0.8f, 0.8f, 0.78f, 0.5f);
                trail.endColor = new Color(0.8f, 0.8f, 0.78f, 0f);
                trail.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                launcherRound = PrefabUtility.SaveAsPrefabAsset(round, Root + "/Prefabs/Player/Grenade40mm.prefab");
            }
            finally
            {
                Object.DestroyImmediate(round);
            }

            explosionPrefab = BuildExplosionPrefab();
        }

        private static ParticleSystem BurstSystem(Transform parent, string name, Material mat, int count, Vector2 life, Vector2 speed,
            Vector2 size, Color a, Color b, float gravity, float sizeGrow, Gradient fade, ParticleSystemRenderMode mode)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.duration = 0.3f;
            main.loop = false;
            main.playOnAwake = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(life.x, life.y);
            main.startSpeed = new ParticleSystem.MinMaxCurve(speed.x, speed.y);
            main.startSize = new ParticleSystem.MinMaxCurve(size.x, size.y);
            main.startColor = new ParticleSystem.MinMaxGradient(a, b);
            main.gravityModifier = gravity;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = count * 2;
            var em = ps.emission;
            em.rateOverTime = 0f;
            em.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)count) });
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.25f;
            if (sizeGrow != 1f)
            {
                var sol = ps.sizeOverLifetime;
                sol.enabled = true;
                sol.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 1f, 1f, sizeGrow));
            }
            if (fade != null)
            {
                var col = ps.colorOverLifetime;
                col.enabled = true;
                col.color = fade;
            }
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = mat;
            r.renderMode = mode;
            if (mode == ParticleSystemRenderMode.Stretch) r.velocityScale = 0.04f;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return ps;
        }

        private static Gradient Fade(Color c0, Color c1, float holdTo = 0.2f)
        {
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(c0, 0f), new GradientColorKey(c1, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, holdTo), new GradientAlphaKey(0f, 1f) });
            return g;
        }

        private static ExplosionEffect BuildExplosionPrefab()
        {
            var dot = AssetDatabase.LoadAssetAtPath<Texture2D>(WeaponArtDir + "/SoftDot.png");
            var fire = ParticleMat("FX_Fireball", dot, Color.white, additive: true);
            var smoke = ParticleMat("FX_Smoke", dot, Color.white, additive: false);
            var root = new GameObject("Explosion");
            try
            {
                var systems = new List<ParticleSystem>
                {
                    BurstSystem(root.transform, "Fireball", fire, 34, new Vector2(0.2f, 0.5f), new Vector2(2f, 9f), new Vector2(0.9f, 2.4f),
                        new Color(1f, 0.85f, 0.4f), new Color(1f, 0.45f, 0.1f), -0.1f, 1.6f, Fade(new Color(1f, 0.9f, 0.6f), new Color(0.9f, 0.25f, 0.05f)), ParticleSystemRenderMode.Billboard),
                    BurstSystem(root.transform, "Smoke", smoke, 26, new Vector2(2f, 4.2f), new Vector2(1f, 3.8f), new Vector2(1.6f, 3.6f),
                        new Color(0.34f, 0.32f, 0.29f, 0.75f), new Color(0.22f, 0.21f, 0.2f, 0.7f), -0.06f, 2.2f, Fade(Color.white, new Color(0.8f, 0.8f, 0.8f), 0.35f), ParticleSystemRenderMode.Billboard),
                    BurstSystem(root.transform, "Sparks", fire, 46, new Vector2(0.5f, 1.3f), new Vector2(7f, 18f), new Vector2(0.03f, 0.07f),
                        new Color(1f, 0.75f, 0.3f), new Color(1f, 0.5f, 0.15f), 1f, 1f, Fade(Color.white, Color.white, 0.6f), ParticleSystemRenderMode.Stretch),
                    BurstSystem(root.transform, "Dirt", smoke, 30, new Vector2(0.7f, 1.6f), new Vector2(4f, 11f), new Vector2(0.06f, 0.16f),
                        new Color(0.28f, 0.22f, 0.15f), new Color(0.18f, 0.15f, 0.11f), 1.6f, 1f, Fade(Color.white, Color.white, 0.8f), ParticleSystemRenderMode.Billboard),
                };
                systems.AddRange(ExtraExplosionSystems(root.transform, fire, smoke));
                var scorchList = BuildScorches(root.transform);
                var lightGo = new GameObject("Flash");
                lightGo.transform.SetParent(root.transform, false);
                lightGo.transform.localPosition = Vector3.up * 0.6f;
                var light = lightGo.AddComponent<Light>();
                light.type = LightType.Point;
                light.range = 16f;
                light.color = new Color(1f, 0.7f, 0.35f);
                light.intensity = 0f;
                light.shadows = LightShadows.None;
                var audio = root.AddComponent<AudioSource>();
                audio.playOnAwake = false;
                audio.spatialBlend = 0.85f;
                audio.rolloffMode = AudioRolloffMode.Linear;
                audio.minDistance = 8f;
                audio.maxDistance = 170f;
                var fx = root.AddComponent<ExplosionEffect>();
                var so = new SerializedObject(fx);
                SetArray(so.FindProperty("systems"), systems.ToArray(), (p, v) => p.objectReferenceValue = v);
                so.FindProperty("flash").objectReferenceValue = light;
                so.FindProperty("source").objectReferenceValue = audio;
                so.FindProperty("boom").objectReferenceValue = boomClip;
                SetArray(so.FindProperty("scorches"), scorchList, (p, v) => p.objectReferenceValue = v);
                so.ApplyModifiedPropertiesWithoutUndo();
                return PrefabUtility.SaveAsPrefabAsset(root, Root + "/Prefabs/Player/Explosion.prefab").GetComponent<ExplosionEffect>();
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        /// <summary>The bigger blast: core flash, shockwave ring, ground dust, embers with trails, bouncing rocks, a smoke column.</summary>
        private static IEnumerable<ParticleSystem> ExtraExplosionSystems(Transform root, Material fire, Material smoke)
        {
            var ringTex = WriteTexture("Shockwave", 128, (u, v) =>
            {
                float r = Mathf.Sqrt(u * u + v * v);
                float band = Mathf.Exp(-Mathf.Pow((r - 0.82f) / 0.07f, 2f));
                return new Color(1f, 1f, 1f, band * Mathf.Clamp01(1f - r));
            });
            var ring = ParticleMat("FX_Shockwave", ringTex, Color.white, additive: true);

            // A blinding core for a couple of frames.
            yield return BurstSystem(root, "CoreFlash", fire, 2, new Vector2(0.06f, 0.1f), Vector2.zero, new Vector2(5.5f, 7f),
                new Color(1f, 0.95f, 0.8f), new Color(1f, 0.85f, 0.6f), 0f, 1.3f, Fade(Color.white, Color.white, 0.1f), ParticleSystemRenderMode.Billboard);

            // Flat shockwave ring racing out along the ground.
            var shock = BurstSystem(root, "Shockwave", ring, 1, new Vector2(0.38f, 0.38f), Vector2.zero, new Vector2(1f, 1f),
                new Color(1f, 0.92f, 0.8f, 0.7f), new Color(1f, 0.92f, 0.8f, 0.7f), 0f, 16f, Fade(Color.white, Color.white, 0.05f), ParticleSystemRenderMode.HorizontalBillboard);
            var sol = shock.sizeOverLifetime;
            sol.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 0.1f, 0f, 6f), new Keyframe(1f, 16f)));
            yield return shock;

            // Dust kicked out low and wide, slowing as it spreads.
            var dust = BurstSystem(root, "GroundDust", smoke, 30, new Vector2(1.6f, 3.2f), new Vector2(5f, 10f), new Vector2(1.2f, 2.6f),
                new Color(0.55f, 0.48f, 0.36f, 0.55f), new Color(0.44f, 0.39f, 0.3f, 0.5f), -0.01f, 2.4f, Fade(Color.white, Color.white, 0.25f), ParticleSystemRenderMode.Billboard);
            var dshape = dust.shape;
            dshape.shapeType = ParticleSystemShapeType.Circle;
            dshape.radius = 0.6f;
            dshape.rotation = new Vector3(-90f, 0f, 0f); // flat on the ground
            var dlimit = dust.limitVelocityOverLifetime;
            dlimit.enabled = true;
            dlimit.limit = 0.6f;
            dlimit.dampen = 0.12f;
            yield return dust;

            // Glowing embers arcing out on short trails.
            var embers = BurstSystem(root, "Embers", fire, 44, new Vector2(1.4f, 3.2f), new Vector2(4f, 13f), new Vector2(0.035f, 0.07f),
                new Color(1f, 0.62f, 0.2f), new Color(1f, 0.32f, 0.06f), 0.35f, 0.6f, Fade(Color.white, new Color(1f, 0.4f, 0.1f), 0.5f), ParticleSystemRenderMode.Billboard);
            var trails = embers.trails;
            trails.enabled = true;
            trails.ratio = 1f;
            trails.lifetime = 0.22f;
            trails.minVertexDistance = 0.08f;
            trails.widthOverTrail = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0f));
            trails.inheritParticleColor = true;
            embers.GetComponent<ParticleSystemRenderer>().trailMaterial = fire;
            yield return embers;

            // Chunks of ground and stone that bounce and settle.
            var rocks = BurstSystem(root, "Rocks", null, 22, new Vector2(2.5f, 4.5f), new Vector2(5f, 13f), new Vector2(0.08f, 0.2f),
                new Color(0.4f, 0.38f, 0.35f), new Color(0.27f, 0.24f, 0.2f), 1.6f, 1f, null, ParticleSystemRenderMode.Mesh);
            var rmain = rocks.main;
            rmain.startRotation3D = true;
            rmain.startRotationX = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            rmain.startRotationY = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            rmain.startRotationZ = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            var rrot = rocks.rotationOverLifetime;
            rrot.enabled = true;
            rrot.separateAxes = true;
            rrot.x = new ParticleSystem.MinMaxCurve(-6f, 6f);
            rrot.y = new ParticleSystem.MinMaxCurve(-6f, 6f);
            rrot.z = new ParticleSystem.MinMaxCurve(-6f, 6f);
            var rcol = rocks.collision;
            rcol.enabled = true;
            rcol.type = ParticleSystemCollisionType.World;
            rcol.mode = ParticleSystemCollisionMode.Collision3D;
            rcol.quality = ParticleSystemCollisionQuality.High;
            rcol.bounce = 0.3f;
            rcol.dampen = 0.45f;
            rcol.radiusScale = 1.3f; // the rock mesh is about the particle size across: collide at its surface, not its core
            rcol.enableDynamicColliders = false;
            rcol.collidesWith = HitMask & ~(1 << NpcLayerIndex);
            var rfade = rocks.colorOverLifetime;
            rfade.enabled = false;
            var rr = rocks.GetComponent<ParticleSystemRenderer>();
            rr.mesh = AssetDatabase.LoadAssetAtPath<Mesh>(EnvArt + "/Mesh_Rock_0.asset");
            rr.sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>(Root + "/Materials/Env_Rock.mat");
            rr.alignment = ParticleSystemRenderSpace.World;
            rr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            yield return rocks;

            // A dark column that keeps rising for several seconds.
            var column = BurstSystem(root, "SmokeColumn", smoke, 14, new Vector2(5f, 8f), new Vector2(0.4f, 1.4f), new Vector2(2.4f, 4.8f),
                new Color(0.2f, 0.19f, 0.18f, 0.6f), new Color(0.13f, 0.12f, 0.12f, 0.55f), 0f, 2.6f, Fade(Color.white, new Color(0.85f, 0.85f, 0.85f), 0.4f), ParticleSystemRenderMode.Billboard);
            var cmain = column.main;
            cmain.duration = 0.6f;
            var cem = column.emission;
            cem.SetBursts(new[] { new ParticleSystem.Burst(0.05f, 6), new ParticleSystem.Burst(0.25f, 5), new ParticleSystem.Burst(0.5f, 3) });
            var vel = column.velocityOverLifetime;
            vel.enabled = true;
            vel.space = ParticleSystemSimulationSpace.World;
            vel.x = new ParticleSystem.MinMaxCurve(-0.2f, 0.4f);
            vel.y = new ParticleSystem.MinMaxCurve(1.2f, 2.3f);
            vel.z = new ParticleSystem.MinMaxCurve(-0.2f, 0.3f);
            var noise = column.noise;
            noise.enabled = true;
            noise.strength = 0.35f;
            noise.frequency = 0.25f;
            yield return column;
        }

        private static Renderer[] BuildScorches(Transform root)
        {
            var tex = WriteTexture("Scorch", 128, (u, v) =>
            {
                float r = Mathf.Sqrt(u * u + v * v), a = Mathf.Atan2(v, u);
                float edge = 0.75f + 0.12f * Mathf.Sin(a * 5f + 1.3f) + 0.06f * Mathf.Sin(a * 11f);
                float k = Mathf.Clamp01((edge - r) / 0.35f);
                float streaks = 0.5f + 0.5f * Mathf.Sin(a * 23f);
                return new Color(0f, 0f, 0f, Mathf.Clamp01(k * (0.75f + 0.25f * streaks)));
            });
            var mat = ParticleMat("FX_Scorch", tex, new Color(0.05f, 0.04f, 0.035f, 0.85f), additive: false);
            var list = new List<Renderer>();
            for (int i = 0; i < 3; i++)
            {
                var q = Part(PrimitiveType.Quad, $"Scorch{i}", root, Vector3.zero, Vector3.one, mat, false);
                var r = q.GetComponent<Renderer>();
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                list.Add(r);
            }
            return list.ToArray();
        }

        /// <summary>Called from AddCombat: the launcher component. It fires from the Bizon's own tube; no model is added.</summary>
        private static void AddLauncher(GameObject root, Transform muzzle, AudioSource audio)
        {
            var gl = root.AddComponent<GrenadeLauncher>();
            var so = new SerializedObject(gl);
            so.FindProperty("roundPrefab").objectReferenceValue = launcherRound;
            so.FindProperty("explosionPrefab").objectReferenceValue = explosionPrefab;
            so.FindProperty("audioSource").objectReferenceValue = audio;
            so.FindProperty("launchClip").objectReferenceValue = launchThumpClip;
            so.FindProperty("loadClip").objectReferenceValue = clackClip;
            so.FindProperty("hitMask").intValue = HitMask;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // ================================================================ casings

        private static void BuildCasings()
        {
            // A 9 mm case at about 2.5x real size: anything smaller vanishes at third-person distance.
            const float half = 0.019f, radius = 0.0115f;
            var mb = new MeshBuilder(1);
            Trunk(mb, 0, -half, half, radius, radius * 0.9f, 8);
            for (int i = 0; i < 8; i++)
            {
                float a0 = i * Mathf.PI / 4f, a1 = (i + 1) * Mathf.PI / 4f;
                var c = new Vector3(0f, -half, 0f);
                mb.Tri(0, c, new Vector3(Mathf.Cos(a1) * radius, -half, Mathf.Sin(a1) * radius), new Vector3(Mathf.Cos(a0) * radius, -half, Mathf.Sin(a0) * radius));
            }
            var mesh = mb.Build("Mesh_Casing");
            string meshPath = $"{WeaponArtDir}/Mesh_Casing.asset";
            AssetDatabase.DeleteAsset(meshPath);
            AssetDatabase.CreateAsset(mesh, meshPath);

            var brass = AssetDatabase.LoadAssetAtPath<Material>($"{Root}/Materials/Weapon_Brass.mat");
            if (brass == null)
            {
                brass = new Material(Shader.Find("Universal Render Pipeline/Particles/Lit"));
                AssetDatabase.CreateAsset(brass, $"{Root}/Materials/Weapon_Brass.mat");
            }
            brass.shader = Shader.Find("Universal Render Pipeline/Particles/Lit");
            brass.SetColor("_BaseColor", new Color(1f, 0.76f, 0.3f));
            brass.SetFloat("_Metallic", 0.8f);
            brass.SetFloat("_Smoothness", 0.75f);
            // A touch of self-light so brass reads against gravel and in shade.
            brass.EnableKeyword("_EMISSION");
            brass.SetColor("_EmissionColor", new Color(0.28f, 0.19f, 0.06f));
            brass.enableInstancing = true;
            EditorUtility.SetDirty(brass);

            var go = new GameObject("Casings");
            try
            {
                var ps = go.AddComponent<ParticleSystem>();
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                var main = ps.main;
                main.loop = true;
                main.playOnAwake = true;
                main.startLifetime = 120f; // they lie where they land until recycled
                main.startSpeed = 0f;
                main.startSize3D = false;
                main.startSize = 1f;
                main.startRotation3D = true;
                main.gravityModifier = 1.1f;
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                main.maxParticles = 400;
                main.ringBufferMode = ParticleSystemRingBufferMode.PauseUntilReplaced;
                var em = ps.emission;
                em.rateOverTime = 0f;
                var shape = ps.shape;
                shape.enabled = false;
                var col = ps.collision;
                col.enabled = true;
                col.type = ParticleSystemCollisionType.World;
                col.mode = ParticleSystemCollisionMode.Collision3D;
                col.quality = ParticleSystemCollisionQuality.High; // Medium's voxel cache lets small, fast casings tunnel through the terrain
                col.dampen = 0.45f;
                col.bounce = 0.32f;
                col.lifetimeLoss = 0f;
                col.minKillSpeed = 0f;
                // Collision radius scales with particle size, which is 1 (the mesh is real size): ~7 mm.
                col.radiusScale = 0.024f;
                col.enableDynamicColliders = false;
                col.collidesWith = HitMask & ~(1 << NpcLayerIndex);
                col.sendCollisionMessages = false;
                var rot = ps.rotationOverLifetime;
                rot.enabled = false;
                var r = go.GetComponent<ParticleSystemRenderer>();
                r.renderMode = ParticleSystemRenderMode.Mesh;
                r.mesh = mesh;
                r.sharedMaterial = brass;
                r.alignment = ParticleSystemRenderSpace.World;
                r.enableGPUInstancing = true;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                casingPrefab = PrefabUtility.SaveAsPrefabAsset(go, Root + "/Prefabs/Player/Casings.prefab").GetComponent<ParticleSystem>();
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        private static void WireCasings(SerializedObject effects)
        {
            effects.FindProperty("casings").objectReferenceValue = casingPrefab;
        }
    }
}
