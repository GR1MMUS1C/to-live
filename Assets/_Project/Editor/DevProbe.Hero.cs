using System.Text;
using ToLive.Player;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using Unity.Rendering;
using UnityEngine;

namespace ToLive.EditorTools
{
    /// <summary>DevProbe helpers for the hero look (HeroPresenter).</summary>
    public static partial class DevProbe
    {
        static readonly int k_HeroSpeed = Animator.StringToHash("Speed");
        static readonly int k_HeroGrounded = Animator.StringToHash("Grounded");

        /// <summary>Every hero GameObject (active form, position, animator state) and whether each capsule is hidden.</summary>
        public static string HeroReport()
        {
            var sb = new StringBuilder();
            foreach (var an in Object.FindObjectsByType<Animator>(FindObjectsInactive.Include))
            {
                if (!an.name.StartsWith("Hero")) continue;
                var st = an.isActiveAndEnabled ? an.GetCurrentAnimatorStateInfo(0) : default;
                var state = !an.isActiveAndEnabled ? "-" : st.IsName("Idle") ? "Idle" : st.IsName("Locomotion") ? "Locomotion" : st.IsName("Air") ? "Air" : st.shortNameHash.ToString();
                sb.AppendLine($"{an.name} active={an.gameObject.activeInHierarchy} pos={an.transform.position} pitch={an.transform.eulerAngles.x:0} state={state} speed={(an.isActiveAndEnabled ? an.GetFloat(k_HeroSpeed) : 0f):0.00} grounded={(an.isActiveAndEnabled && an.GetBool(k_HeroGrounded))} valid={(an.avatar != null && an.avatar.isValid && an.avatar.isHuman)}");
            }
            var client = ClientServerBootstrap.ClientWorld;
            if (client != null && client.IsCreated)
            {
                var em = client.EntityManager;
                using var q = em.CreateEntityQuery(ComponentType.ReadOnly<PlayerVisual>());
                using var es = q.ToEntityArray(Allocator.Temp);
                foreach (var e in es)
                {
                    var pv = em.GetComponentData<PlayerVisual>(e);
                    var who = em.HasComponent<GhostOwner>(e) ? $"owner{em.GetComponentData<GhostOwner>(e).NetworkId}" : "dummy";
                    if (em.HasComponent<ToLive.Opposition.CharacterKind>(e) && em.GetComponentData<ToLive.Opposition.CharacterKind>(e).Kind == ToLive.Opposition.CharacterKind.Rival) who = "rival";
                    sb.AppendLine($"{client.Name} {who} entity {e.Index}: capsuleHidden={em.HasComponent<DisableRendering>(pv.Body)}");
                }
            }
            return sb.ToString();
        }

        /// <summary>Server: sets alignment and tier on every human player (to test the dominance form).</summary>
        public static string SetHumanAlignment(float alignment, int tier)
        {
            foreach (var world in World.All)
            {
                if (!world.IsCreated || !world.IsServer()) continue;
                var em = world.EntityManager;
                using var q = em.CreateEntityQuery(ComponentType.ReadWrite<ToLive.Power.PowerState>(), ComponentType.ReadOnly<GhostOwner>());
                using var es = q.ToEntityArray(Allocator.Temp);
                var n = 0;
                foreach (var e in es)
                {
                    if (em.HasComponent<ToLive.Opposition.CharacterKind>(e) && em.GetComponentData<ToLive.Opposition.CharacterKind>(e).Kind != ToLive.Opposition.CharacterKind.Human) continue;
                    var p = em.GetComponentData<ToLive.Power.PowerState>(e);
                    p.Alignment = alignment; p.Tier = (byte)tier;
                    em.SetComponentData(e, p);
                    n++;
                }
                return $"set {n} players to alignment {alignment} tier {tier}";
            }
            return "no server";
        }

        /// <summary>Renders the main camera to a PNG (works when the Game view is unfocused).</summary>
        public static string Shot(string path, int width = 1600, int height = 900)
        {
            var cam = Camera.main;
            if (cam == null) return "no camera";
            var rt = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
            var prev = cam.targetTexture;
            cam.targetTexture = rt;
            cam.Render();
            cam.targetTexture = prev;
            var active = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            tex.Apply();
            RenderTexture.active = active;
            RenderTexture.ReleaseTemporary(rt);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)));
            System.IO.File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            return "saved " + System.IO.Path.GetFullPath(path);
        }

        /// <summary>Despawns the second client's connection (tests that its hero disappears).</summary>
        public static string DisconnectSecondClient()
        {
            foreach (var world in World.All)
            {
                if (!world.IsCreated || world.Name != "ClientWorld2") continue;
                using var q = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamConnection>());
                using var es = q.ToEntityArray(Allocator.Temp);
                foreach (var e in es) world.EntityManager.AddComponent<NetworkStreamRequestDisconnect>(e);
                return $"disconnecting {es.Length}";
            }
            return "no ClientWorld2";
        }
    }
}
