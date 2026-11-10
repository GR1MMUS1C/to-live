using Unity.Mathematics;
using UnityEngine;

namespace ToLive.Player
{
    /// <summary>Placeholder VFX: an expanding white ring. Real shockwave comes in a later milestone.</summary>
    public class SupersonicRing : MonoBehaviour
    {
        const int Segments = 48;
        LineRenderer m_Line;
        float m_Age;

        public static void Spawn(float3 position, float3 forward)
        {
            var go = new GameObject("SupersonicRing");
            go.transform.SetPositionAndRotation(position, Quaternion.LookRotation(forward));
            go.AddComponent<SupersonicRing>();
        }

        void Awake()
        {
            m_Line = gameObject.AddComponent<LineRenderer>();
            m_Line.loop = true;
            m_Line.useWorldSpace = false;
            m_Line.positionCount = Segments;
            m_Line.widthMultiplier = 0.6f;
            m_Line.material = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            m_Line.material.color = Color.white;
            SetRadius(1f);
        }

        void Update()
        {
            m_Age += Time.deltaTime;
            SetRadius(2f + m_Age * 60f);
            m_Line.widthMultiplier = Mathf.Lerp(0.6f, 0f, m_Age / 0.8f);
            if (m_Age > 0.8f) Destroy(gameObject);
        }

        void SetRadius(float r)
        {
            for (var i = 0; i < Segments; i++)
            {
                var a = i * Mathf.PI * 2f / Segments;
                m_Line.SetPosition(i, new Vector3(Mathf.Cos(a) * r, Mathf.Sin(a) * r, 0f));
            }
        }
    }
}
