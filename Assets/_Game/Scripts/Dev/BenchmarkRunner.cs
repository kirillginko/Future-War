using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ForestSoldier.Combat;
using ForestSoldier.Environment;
using ForestSoldier.Player;
using UnityEngine;

namespace ForestSoldier.Dev
{
    /// <summary>
    /// Scripted performance run for a built player. Does nothing unless the
    /// app is launched with <c>-benchmark</c>. Then it drives the soldier over a
    /// fixed route (firing line, shooting, the yard, the trail, a slow spin in
    /// dense forest), records every frame, writes a JSON report and quits.
    ///
    ///   ForestSoldier.app/Contents/MacOS/ForestSoldier -benchmark -benchmarkOut /tmp/bench.json
    /// </summary>
    public class BenchmarkRunner : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoStart()
        {
            if (!HasArg("-benchmark")) return;
            new GameObject("BenchmarkRunner").AddComponent<BenchmarkRunner>();
        }

        private static bool HasArg(string name)
        {
            foreach (var a in System.Environment.GetCommandLineArgs())
                if (a == name) return true;
            return false;
        }

        private static string ArgValue(string name)
        {
            var args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == name) return args[i + 1];
            return null;
        }

        private class Phase
        {
            public string Name;
            public readonly List<float> FrameMs = new List<float>(4096);
            public readonly List<float> CpuMs = new List<float>(4096);
            public readonly List<float> GpuMs = new List<float>(4096);
            public long HeapGrowth;
            public int FramesThatAllocated, Frames, Collections;
        }

        private readonly List<Phase> phases = new List<Phase>();
        private Phase current;
        private readonly FrameTiming[] timing = new FrameTiming[1];

        private SoldierInput input;
        private ThirdPersonCamera rig;
        private Transform player;

        private IEnumerator Start()
        {
            DontDestroyOnLoad(gameObject);
            Application.targetFrameRate = -1;
            QualitySettings.vSyncCount = 0; // measure the real cost, not the display's refresh
            yield return null;
            input = FindFirstObjectByType<SoldierInput>();
            rig = FindFirstObjectByType<ThirdPersonCamera>();
            player = input.transform;
            input.ExternalControl = true;
            input.SetCursorLocked(false);
            yield return Wait(2f, null); // settle: shaders, pools

            yield return Wait(4f, "firing_line_idle");

            input.Aim = true;
            AimAt(DamageableTarget.All[0].transform.position + Vector3.up);
            yield return Wait(0.6f, null);
            current = Begin("shooting");
            for (int burst = 0; burst < 5; burst++)
            {
                var t = DamageableTarget.All[burst % DamageableTarget.All.Count];
                AimAt(t.transform.position + Vector3.up * 1.1f);
                input.Fire = true;
                yield return Record(0.6f);
                input.Fire = false;
                yield return Record(0.4f);
            }
            input.ReloadRequested = true;
            yield return Record(2.5f);
            input.Aim = false;

            Teleport(new Vector2(0f, -20f), 0f);
            input.Move = new Vector2(0f, 1f);
            input.Sprint = true;
            yield return Wait(4f, "yard_sprint");
            input.Sprint = false;

            current = Begin("trail_walk");
            Teleport(ForestLayout.TrailPoint(0.15f), 0f);
            for (float u = 0.15f; u < 0.45f; u += 0.0025f)
            {
                Vector2 next = ForestLayout.TrailPoint(u + 0.01f);
                rig.Yaw = Mathf.Atan2(next.x - player.position.x, next.y - player.position.z) * Mathf.Rad2Deg;
                input.Sprint = true;
                yield return Record(0.1f);
            }
            input.Sprint = false;
            input.Move = Vector2.zero;

            current = Begin("forest_spin");
            Teleport(new Vector2(-70f, 20f), 0f);
            rig.Pitch = 5f;
            for (int i = 0; i < 360; i++)
            {
                rig.Yaw += 1f;
                yield return Record(1f / 60f);
            }

            Write();
            Application.Quit();
        }

        private Phase Begin(string name)
        {
            var p = new Phase { Name = name };
            phases.Add(p);
            return p;
        }

        private IEnumerator Wait(float seconds, string phase)
        {
            if (phase != null) current = Begin(phase);
            yield return Record(seconds, phase != null);
        }

        private long lastHeap;
        private int lastCollections;

        private IEnumerator Record(float seconds, bool record = true)
        {
            float end = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < end)
            {
                yield return null;
                // Managed heap growth per frame: positive steps are allocations.
                long heap = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
                int gcs = System.GC.CollectionCount(0);
                long step = heap - lastHeap;
                bool collected = gcs != lastCollections;
                lastHeap = heap;
                lastCollections = gcs;
                if (!record || current == null) continue;
                current.Frames++;
                if (collected) current.Collections++;
                else if (step > 0)
                {
                    current.FramesThatAllocated++;
                    current.HeapGrowth += step;
                }
                current.FrameMs.Add(Time.unscaledDeltaTime * 1000f);
                FrameTimingManager.CaptureFrameTimings();
                if (FrameTimingManager.GetLatestTimings(1, timing) > 0)
                {
                    current.CpuMs.Add((float)timing[0].cpuFrameTime);
                    current.GpuMs.Add((float)timing[0].gpuFrameTime);
                }
            }
        }

        private void AimAt(Vector3 p)
        {
            for (int i = 0; i < 4; i++)
            {
                rig.Snap();
                Vector3 d = (p - rig.transform.position).normalized;
                rig.Yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
                rig.Pitch = -Mathf.Asin(d.y) * Mathf.Rad2Deg;
            }
        }

        private void Teleport(Vector2 at, float yaw)
        {
            var terrain = Terrain.activeTerrain;
            float y = terrain != null ? terrain.SampleHeight(new Vector3(at.x, 0f, at.y)) + terrain.transform.position.y : 0f;
            var cc = player.GetComponent<CharacterController>();
            cc.enabled = false;
            player.SetPositionAndRotation(new Vector3(at.x, y + 0.05f, at.y), Quaternion.Euler(0f, yaw, 0f));
            cc.enabled = true;
            rig.Yaw = yaw;
            rig.Snap();
        }

        private static string Stats(List<float> v)
        {
            if (v.Count == 0) return "null";
            var s = new List<float>(v);
            s.Sort();
            float sum = 0f;
            foreach (var x in s) sum += x;
            float P(float q) => s[Mathf.Clamp(Mathf.RoundToInt(q * (s.Count - 1)), 0, s.Count - 1)];
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{{\"avg\":{0:F2},\"p50\":{1:F2},\"p95\":{2:F2},\"p99\":{3:F2},\"max\":{4:F2},\"frames\":{5}}}",
                sum / s.Count, P(0.5f), P(0.95f), P(0.99f), s[s.Count - 1], s.Count);
        }

        private void Write()
        {
            var sb = new StringBuilder();
            sb.Append("{\"resolution\":\"").Append(Screen.width).Append('x').Append(Screen.height)
              .Append("\",\"gpu\":\"").Append(SystemInfo.graphicsDeviceName)
              .Append("\",\"cpu\":\"").Append(SystemInfo.processorType)
              .Append("\",\"quality\":\"").Append(QualitySettings.names[QualitySettings.GetQualityLevel()])
              .Append("\",\"phases\":{");
            for (int i = 0; i < phases.Count; i++)
            {
                var p = phases[i];
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(p.Name).Append("\":{\"frame_ms\":").Append(Stats(p.FrameMs))
                  .Append(",\"cpu_ms\":").Append(Stats(p.CpuMs))
                  .Append(",\"gpu_ms\":").Append(Stats(p.GpuMs))
                  .Append(",\"frames_that_allocated\":").Append(p.FramesThatAllocated)
                  .Append(",\"heap_growth_bytes\":").Append(p.HeapGrowth)
                  .Append(",\"gc_collections\":").Append(p.Collections).Append('}');
            }
            sb.Append("}}");
            string path = ArgValue("-benchmarkOut") ?? Path.Combine(Application.persistentDataPath, "benchmark.json");
            File.WriteAllText(path, sb.ToString());
            Debug.Log($"[Benchmark] wrote {path}");
        }
    }
}
