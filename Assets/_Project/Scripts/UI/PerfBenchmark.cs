using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ToLive.CameraRig;
using ToLive.Combat;
using ToLive.Destruction;
using ToLive.Input;
using ToLive.Net;
using UnityEngine;

namespace ToLive.UI
{
    /// <summary>
    /// Automated destruction stress benchmark. Hosts a game, looks at the breakable building,
    /// destroys all of it at once and records frame times while the debris falls and settles.
    /// Start it from the command line of a build:
    ///   ToLive.exe -tolive-bench -tolive-cap 400 -tolive-out bench.csv
    /// or from the editor via DevProbe. Results are appended as one CSV line per run.
    /// </summary>
    public class PerfBenchmark : MonoBehaviour
    {
        public static bool Running { get; private set; }
        public static string LastResult { get; private set; } = "";

        void Start()
        {
            var args = Environment.GetCommandLineArgs();
            var join = Arg(args, "-tolive-join");
            if (join != null) { StartCoroutine(AutoJoin(join)); return; }
            if (!args.Contains("-tolive-bench")) return;
            var cap = int.TryParse(Arg(args, "-tolive-cap"), out var c) ? c : 400;
            var mult = int.TryParse(Arg(args, "-tolive-mult"), out var m) ? m : 1;
            var output = Arg(args, "-tolive-out") ?? Path.Combine(Application.dataPath, "..", "bench.csv");
            StartCoroutine(Run(cap, output, quitWhenDone: true, mult));
        }

        static IEnumerator AutoJoin(string address)
        {
            yield return null;
            var ui = FindAnyObjectByType<GameUI>();
            ui.JoinAddress(address);
        }

        static string Arg(string[] args, string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        public static void StartInEditor(int cap, string output, int mult = 1)
        {
            var host = FindAnyObjectByType<PerfBenchmark>();
            if (host != null && !Running) host.StartCoroutine(host.Run(cap, output, quitWhenDone: false, mult));
        }

        IEnumerator Run(int cap, string output, bool quitWhenDone, int mult = 1)
        {
            Running = true;
            DebrisSystem.LiveCapOverride = cap;
            DebrisSystem.SpawnMultiplier = mult;
            DebrisSystem.TotalCapOverride = 100000;
            Application.targetFrameRate = -1;
            QualitySettings.vSyncCount = 0;

            var ui = FindAnyObjectByType<GameUI>();
            if (!NetSession.IsActive) ui.Host();
            while (!FlightCameraRig.HasLocalPlayer || DebrisSystem.ChunkCount == 0) yield return null;
            DebugCommands.Send(DebugCommandRequest.ResetDummyAndBuilding);
            yield return new WaitForSeconds(3f);

            // Look at the building from where we spawned.
            var reader = FindAnyObjectByType<InputReader>();
            if (reader != null) reader.enabled = false;
            GameplayInputGate.Blocked = false;
            AimAtBuilding();

            var idle = new List<float>();
            yield return Measure(idle, 3f);

            DebugCommands.Send(DebugCommandRequest.DestroyBuilding);
            var frames = new List<float>();
            int peakLive = 0, peakTotal = 0;
            var t = 0f;
            while (t < 15f)
            {
                AimAtBuilding();
                yield return null;
                frames.Add(Time.unscaledDeltaTime * 1000f);
                t += Time.unscaledDeltaTime;
                peakLive = Mathf.Max(peakLive, DebrisSystem.LiveDebris);
                peakTotal = Mathf.Max(peakTotal, DebrisSystem.TotalDebris);
            }

            var first3 = Take(frames, 3000f);
            LastResult = string.Join(",",
                DateTime.Now.ToString("s"), Application.isEditor ? "Editor" : (Debug.isDebugBuild ? "DevBuild" : "Build"),
                cap, mult, $"{Screen.width}x{Screen.height}",
                Avg(idle).ToString("0.00"), Avg(frames).ToString("0.00"), (1000f / Avg(frames)).ToString("0.0"),
                Pct(first3, 0.5f).ToString("0.00"), Pct(frames, 0.99f).ToString("0.00"), frames.Max().ToString("0.00"),
                peakLive, peakTotal, DebrisSystem.BrokenChunks, DebrisSystem.TotalDebris,
                Quote(SystemInfo.processorType), Quote(SystemInfo.graphicsDeviceName));
            try
            {
                var exists = File.Exists(output);
                using var w = new StreamWriter(output, append: true);
                if (!exists)
                    w.WriteLine("time,target,liveCap,pieceMultiplier,resolution,idleAvgMs,destroyAvgMs,destroyAvgFps,first3sMedianMs,p99Ms,maxMs,peakLiveDebris,peakTotalDebris,brokenChunks,debrisAtEnd,cpu,gpu");
                w.WriteLine(LastResult);
            }
            catch (Exception e) { Debug.LogError($"[ToLive] Could not write {output}: {e.Message}"); }
            Debug.Log($"[ToLive] BENCH {LastResult}");

            if (reader != null) reader.enabled = true;
            DebrisSystem.LiveCapOverride = -1;
            DebrisSystem.SpawnMultiplier = 1;
            DebrisSystem.TotalCapOverride = -1;
            Running = false;
            if (quitWhenDone) Application.Quit();
        }

        static void AimAtBuilding()
        {
            var cam = Camera.main;
            if (cam == null) return;
            var to = (Vector3)DebrisSystem.BuildingCenter - cam.transform.position;
            if (to.sqrMagnitude < 1f) return;
            var e = Quaternion.LookRotation(to).eulerAngles;
            FlightCameraRig.Yaw = Mathf.DeltaAngle(0f, e.y);
            FlightCameraRig.Pitch = Mathf.Clamp(Mathf.DeltaAngle(0f, e.x), -60f, 60f);
        }

        static IEnumerator Measure(List<float> into, float seconds)
        {
            var t = 0f;
            while (t < seconds)
            {
                yield return null;
                into.Add(Time.unscaledDeltaTime * 1000f);
                t += Time.unscaledDeltaTime;
            }
        }

        static List<float> Take(List<float> frames, float ms)
        {
            var result = new List<float>();
            var sum = 0f;
            foreach (var f in frames) { if (sum > ms) break; result.Add(f); sum += f; }
            return result;
        }

        static float Avg(List<float> v) => v.Count == 0 ? 0f : v.Average();

        static float Pct(List<float> v, float p)
        {
            if (v.Count == 0) return 0f;
            var s = v.OrderBy(x => x).ToList();
            return s[Mathf.Clamp(Mathf.CeilToInt(p * s.Count) - 1, 0, s.Count - 1)];
        }

        static string Quote(string s) => "\"" + s.Replace("\"", "'") + "\"";
    }
}
