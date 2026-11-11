using UnityEngine;

namespace ToLive.Power
{
    /// <summary>Cosmetic expanding pressure ring (horizontal), coloured with the energy hue.</summary>
    public class GroundRing : MonoBehaviour
    {
        const int Segments = 64;
        static Material s_Material;
        LineRenderer m_Line;
        float m_Age, m_Life, m_StartRadius, m_EndRadius, m_Width;
        Color m_Color;

        public static void Spawn(Vector3 center, Color color, float startRadius, float endRadius, float life, float width, Vector3 normal = default)
        {
            var go = new GameObject("PressureRing");
            go.transform.position = center;
            go.transform.rotation = normal == default ? Quaternion.identity : Quaternion.FromToRotation(Vector3.up, normal);
            var ring = go.AddComponent<GroundRing>();
            ring.m_Color = color; ring.m_StartRadius = startRadius; ring.m_EndRadius = endRadius; ring.m_Life = life; ring.m_Width = width;
        }

        void Start()
        {
            if (s_Material == null) s_Material = ToLive.Combat.FxMaterials.Unlit();
            m_Line = gameObject.AddComponent<LineRenderer>();
            m_Line.loop = true;
            m_Line.useWorldSpace = false;
            m_Line.positionCount = Segments;
            m_Line.material = s_Material; // own instance so the colour can differ per ring
            m_Line.material.color = m_Color;
            m_Line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            Draw(0f);
        }

        void Update()
        {
            m_Age += Time.deltaTime;
            var t = Mathf.Clamp01(m_Age / m_Life);
            Draw(t);
            if (t >= 1f) Destroy(gameObject);
        }

        void Draw(float t)
        {
            var r = Mathf.Lerp(m_StartRadius, m_EndRadius, 1f - (1f - t) * (1f - t));
            m_Line.widthMultiplier = m_Width * (1f - t);
            for (var i = 0; i < Segments; i++)
            {
                var a = i * Mathf.PI * 2f / Segments;
                m_Line.SetPosition(i, new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r));
            }
        }
    }
}
