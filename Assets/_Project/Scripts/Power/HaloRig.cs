using UnityEngine;

namespace ToLive.Power
{
    /// <summary>
    /// The "fractured halo": hard-edged shards of light orbiting a character. One instance per
    /// character, driven every frame by <see cref="PowerPresenter"/>. Two personalities blended by
    /// alignment:
    ///  - TRUST: two calm, level, symmetrical rings; shards lie along the orbit; steady glow.
    ///  - DOMINANCE: a jagged, tilted, uneven swarm; shards point outward like cracks; fast,
    ///    unstable orbit with flicker, and shards occasionally snap out and pull back.
    /// </summary>
    public class HaloRig
    {
        readonly GameObject m_Root;
        readonly Transform[] m_Shards;
        readonly Renderer[] m_Renderers;
        readonly MaterialPropertyBlock m_Block = new MaterialPropertyBlock();
        readonly float[] m_Seed;
        readonly Light m_Light;
        float m_Time;
        static Material s_Material;
        static readonly int k_BaseColor = Shader.PropertyToID("_BaseColor");

        public HaloRig(string name, int shardCount)
        {
            if (s_Material == null)
            {
                s_Material = ToLive.Combat.FxMaterials.Unlit(); s_Material.name = "HaloShard (runtime)";
                s_Material.enableInstancing = true;
            }
            m_Root = new GameObject(name);
            m_Shards = new Transform[shardCount];
            m_Renderers = new Renderer[shardCount];
            m_Seed = new float[shardCount];
            var rng = new System.Random(name.GetHashCode());
            for (var i = 0; i < shardCount; i++)
            {
                var go = new GameObject("Shard");
                go.transform.SetParent(m_Root.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = ShardMesh.Get(i);
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterial = s_Material;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                m_Shards[i] = go.transform;
                m_Renderers[i] = r;
                m_Seed[i] = (float)rng.NextDouble();
            }
            // the energy colour washing over the surroundings
            var lightGo = new GameObject("PowerWash");
            lightGo.transform.SetParent(m_Root.transform, false);
            m_Light = lightGo.AddComponent<Light>();
            m_Light.type = LightType.Point;
            m_Light.shadows = LightShadows.None;
            m_Light.intensity = 0f;
        }

        public void Destroy() => Object.Destroy(m_Root);

        /// <param name="intensity">0 hidden, ~0.3 tier-2 idle, 1 full surge.</param>
        /// <param name="visibleShards">how many shards are shown.</param>
        /// <param name="glow">HDR multiplier on the colour.</param>
        /// <param name="burst">0..1 extra outward expansion (ritual peak / eruption).</param>
        public void Update(Vector3 center, float alignment, float intensity, int visibleShards, float glow, float burst,
            Color color, float washIntensity, float washRange, PowerLook look, float dt)
        {
            m_Time += dt;
            var t = Mathf.Clamp01((alignment + 1f) * 0.5f); // 0 trust .. 1 dominance
            m_Root.transform.position = center;
            var radius = look.HaloRadius * Mathf.Lerp(0.75f, 1f, intensity) * (1f + burst * 1.8f);
            var orbit = Mathf.Lerp(look.TrustOrbitSpeed, look.DominanceOrbitSpeed, t);

            for (var i = 0; i < m_Shards.Length; i++)
            {
                var shard = m_Shards[i];
                var show = i < visibleShards && intensity > 0.01f;
                if (shard.gameObject.activeSelf != show) shard.gameObject.SetActive(show);
                if (!show) continue;
                var s = m_Seed[i];
                var n = Mathf.Max(1, visibleShards);

                // TRUST layout: two level rings, evenly spaced, alternating direction.
                var ringIndex = i % 2;
                var trustAngle = (i / 2) * Mathf.PI * 2f / Mathf.Ceil(n / 2f) + (ringIndex == 0 ? 1f : -1f) * m_Time * orbit;
                var trustPos = new Vector3(Mathf.Cos(trustAngle), (ringIndex == 0 ? 0.25f : -0.45f), Mathf.Sin(trustAngle)) * 1f;
                trustPos.x *= radius * (ringIndex == 0 ? 1f : 0.8f);
                trustPos.z *= radius * (ringIndex == 0 ? 1f : 0.8f);
                var trustRot = Quaternion.LookRotation(new Vector3(-Mathf.Sin(trustAngle), 0f, Mathf.Cos(trustAngle))); // along the orbit

                // DOMINANCE layout: uneven radii, tilted orbits, jitter, outward-pointing shards.
                var tilt = Quaternion.Euler(Mathf.Sin(s * 40f) * 55f, s * 360f, Mathf.Cos(s * 23f) * 40f);
                var domAngle = s * Mathf.PI * 2f + m_Time * orbit * (0.6f + s * 0.9f) * (s > 0.5f ? 1f : -1.3f);
                var domR = radius * (0.7f + 0.65f * Mathf.PerlinNoise(s * 13f, m_Time * 1.7f));
                var snap = Mathf.Pow(Mathf.Max(0f, Mathf.Sin(m_Time * (1.5f + s * 3f) + s * 20f)), 24f) * 1.2f; // occasional snap out
                var domPos = tilt * new Vector3(Mathf.Cos(domAngle), 0f, Mathf.Sin(domAngle)) * (domR + snap * radius);
                domPos += Random.insideUnitSphere * 0.05f * t;
                var domRot = Quaternion.LookRotation(domPos.sqrMagnitude > 1e-4f ? domPos.normalized : Vector3.up) * Quaternion.Euler(0, 0, s * 360f);

                var pos = Vector3.Lerp(trustPos, domPos, t);
                var rot = Quaternion.Slerp(trustRot, domRot, t);
                shard.localPosition = pos;
                shard.localRotation = rot;
                var len = look.ShardLength * Mathf.Lerp(1f, 0.7f + s * 0.9f, t) * Mathf.Lerp(0.5f, 1f, intensity);
                var thick = Mathf.Lerp(0.9f, 0.6f + s * 0.8f, t);
                shard.localScale = new Vector3(thick, thick, 1f) * len;

                // colour: steady for trust, flickering for dominance
                var flicker = 1f - t * 0.7f * Mathf.PerlinNoise(s * 31f, m_Time * 9f);
                m_Block.SetColor(k_BaseColor, color * glow * flicker * Mathf.Lerp(0.6f, 1.3f, s));
                m_Renderers[i].SetPropertyBlock(m_Block);
            }

            m_Light.color = color.maxColorComponent > 1f ? color / color.maxColorComponent : color;
            m_Light.intensity = washIntensity * (1f - t * 0.35f * Mathf.PerlinNoise(0.5f, m_Time * 7f));
            m_Light.range = washRange;
            m_Light.enabled = washIntensity > 0.01f;
        }
    }
}
