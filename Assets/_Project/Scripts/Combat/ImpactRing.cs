using Unity.Mathematics;
using UnityEngine;

namespace ToLive.Combat
{
    /// <summary>Placeholder impact VFX: a quick expanding ring facing the camera.</summary>
    public class ImpactRing : MonoBehaviour
    {
        const int Segments = 32;
        static Material s_Material;
        LineRenderer m_Line;
        float m_Age, m_Scale;

        public static void Spawn(float3 position, float scale)
        {
            var go = new GameObject("ImpactRing");
            var cam = Camera.main;
            var rot = cam != null ? Quaternion.LookRotation(cam.transform.forward) : Quaternion.identity;
            go.transform.SetPositionAndRotation(position, rot);
            var ring = go.AddComponent<ImpactRing>();
            ring.m_Scale = scale;
        }

        void Awake()
        {
            if (s_Material == null)
            {
                s_Material = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                s_Material.color = new Color(1f, 0.85f, 0.4f);
            }
            m_Line = gameObject.AddComponent<LineRenderer>();
            m_Line.loop = true;
            m_Line.useWorldSpace = false;
            m_Line.positionCount = Segments;
            m_Line.sharedMaterial = s_Material;
            m_Line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        void Update()
        {
            m_Age += Time.deltaTime;
            const float life = 0.25f;
            var r = (0.5f + m_Age * 14f) * m_Scale;
            m_Line.widthMultiplier = Mathf.Lerp(0.35f, 0f, m_Age / life) * m_Scale;
            for (var i = 0; i < Segments; i++)
            {
                var a = i * Mathf.PI * 2f / Segments;
                m_Line.SetPosition(i, new Vector3(Mathf.Cos(a) * r, Mathf.Sin(a) * r, 0f));
            }
            if (m_Age > life) Destroy(gameObject);
        }
    }
}
