using UnityEngine;

namespace ToLive.Power
{
    /// <summary>
    /// Cosmetic "windows blowing out" on a building face: a burst of small bright glass shards
    /// flying off the surface. Placeholder until buildings have real windows.
    /// </summary>
    public class GlassBurst : MonoBehaviour
    {
        static Material s_Material;

        public static void Spawn(Vector3 position, Vector3 normal, Color tint, int count)
        {
            if (s_Material == null)
            {
                s_Material = ToLive.Combat.FxMaterials.Particles(); // additive transparent (set up by the setup tool)
            }
            var go = new GameObject("GlassBurst");
            go.transform.SetPositionAndRotation(position, Quaternion.LookRotation(normal));
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.duration = 0.2f;
            main.loop = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.8f, 1.8f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(6f, 18f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.08f, 0.35f);
            main.startRotation3D = true;
            main.gravityModifier = 1.2f;
            main.startColor = new ParticleSystem.MinMaxGradient(Color.Lerp(Color.white, tint, 0.35f) * 2f, Color.white * 2f);
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.stopAction = ParticleSystemStopAction.Destroy;
            var emission = ps.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)count) });
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 35f;
            shape.radius = 2.5f;
            var rot = ps.rotationOverLifetime;
            rot.enabled = true;
            rot.separateAxes = true;
            rot.x = new ParticleSystem.MinMaxCurve(-8f, 8f);
            rot.y = new ParticleSystem.MinMaxCurve(-8f, 8f);
            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = s_Material;
            renderer.renderMode = ParticleSystemRenderMode.Mesh;
            renderer.mesh = Resources.GetBuiltinResource<Mesh>("Quad.fbx");
            ps.Play();
            go.AddComponent<GlassBurst>();
        }
    }
}
