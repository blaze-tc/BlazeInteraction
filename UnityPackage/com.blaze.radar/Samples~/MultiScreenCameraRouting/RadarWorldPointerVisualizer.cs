using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blaze.Radar.Samples
{
    /// <summary>
    /// Routes logical screen pointers into world-space ray hits and owns a bounded particle pool.
    /// </summary>
    public sealed class RadarWorldPointerVisualizer : MonoBehaviour
    {
        [Header("Scene References")]
        [SerializeField] private RadarScreenCameraRouter router;
        [SerializeField] private RadarSampleLogPanel logPanel;
        [SerializeField] private ParticleSystem particlePrefab;
        [SerializeField] private Transform poolRoot;

        [Header("Pool")]
        [SerializeField, Min(0)] private int prewarmCount = 16;
        [SerializeField, Min(1)] private int maximumPoolSize = 64;
        [SerializeField, Min(0.01f)] private float zeroFrameReleaseDelay = 0.15f;

        private readonly Dictionary<PointerKey, ActiveEffect> active =
            new Dictionary<PointerKey, ActiveEffect>();
        private readonly Stack<ParticleSystem> pool = new Stack<ParticleSystem>();
        private readonly Dictionary<string, float> zeroFrameSince =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        private readonly List<PointerKey> releaseKeys = new List<PointerKey>();
        private int totalCreated;

        internal int ActiveCountForTests => active.Count;
        internal int PoolCountForTests => pool.Count;
        internal int TotalCreatedForTests => totalCreated;

        private void Awake()
        {
            EnsurePrewarmed();
        }

        private void Update()
        {
            ProcessZeroFrameTimeouts(Time.unscaledTime);
        }

        private void OnDisable()
        {
            ReleaseAll("component disabled");
            zeroFrameSince.Clear();
        }

        public void ProcessFrame(RadarScreenPointerFrame frame, long droppedBatches)
        {
            if (frame == null || frame.screen == null || string.IsNullOrWhiteSpace(frame.screen.screenId))
            {
                return;
            }

            var pointerCount = frame.pointers == null ? 0 : frame.pointers.Count;
            logPanel?.RecordFrame(frame, droppedBatches);
            if (pointerCount == 0)
            {
                if (!zeroFrameSince.ContainsKey(frame.screen.screenId))
                {
                    zeroFrameSince.Add(frame.screen.screenId, Time.unscaledTime);
                }

                return;
            }

            zeroFrameSince.Remove(frame.screen.screenId);
            for (var index = 0; index < frame.pointers.Count; index++)
            {
                var pointer = frame.pointers[index];
                if (pointer != null)
                {
                    ProcessPointer(frame.screen, pointer);
                }
            }
        }

        public void ProcessPointer(RadarScreenInfo screen, RadarScreenPointer pointer)
        {
            if (screen == null || pointer == null || string.IsNullOrWhiteSpace(screen.screenId))
            {
                return;
            }

            var key = new PointerKey(screen.screenId, pointer.pointerId);
            if (pointer.phase == RadarPointerPhase.Up)
            {
                logPanel?.RecordPointerUp(screen, pointer);
                Release(key);
                return;
            }

            if (router == null)
            {
                logPanel?.RecordMiss(screen, pointer, default(Vector2), default(Ray), "router unavailable");
                return;
            }

            var hasPixel = router.TryMapToCameraPixel(screen, pointer, out var cameraPixel);
            var hasRay = router.TryCreateRay(screen, pointer, out var ray);
            if (!hasPixel || !hasRay || !router.TryRaycast(screen, pointer, out var hit))
            {
                logPanel?.RecordMiss(
                    screen,
                    pointer,
                    hasPixel ? cameraPixel : default(Vector2),
                    hasRay ? ray : default(Ray),
                    hasRay ? "no collider hit" : "ray unavailable");
                return;
            }

            var particle = GetOrCreate(key);
            if (particle == null)
            {
                logPanel?.RecordMiss(screen, pointer, cameraPixel, ray, "particle pool cap reached");
                return;
            }

            particle.transform.SetPositionAndRotation(
                hit.point + hit.normal * 0.02f,
                Quaternion.LookRotation(hit.normal));
            ApplyAppearance(particle, screen.screenId, pointer.phase == RadarPointerPhase.Down);
            if (!particle.isPlaying)
            {
                particle.Play(true);
            }

            if (pointer.phase == RadarPointerPhase.Down)
            {
                particle.Emit(18);
            }

            active[key] = new ActiveEffect(particle, Time.unscaledTime);
            logPanel?.RecordHit(screen, pointer, cameraPixel, ray, hit);
        }

        public void ReleaseAll(string reason)
        {
            if (active.Count == 0)
            {
                return;
            }

            releaseKeys.Clear();
            foreach (var pair in active)
            {
                releaseKeys.Add(pair.Key);
            }

            for (var index = 0; index < releaseKeys.Count; index++)
            {
                Release(releaseKeys[index]);
            }

            logPanel?.RecordLifecycle("Released all world effects: " + reason + ".");
        }

        internal void ConfigurePoolForTests(ParticleSystem prototype, int prewarm, int cap)
        {
            ReleaseAll("test reset");
            while (pool.Count > 0)
            {
                DestroyImmediate(pool.Pop().gameObject);
            }

            particlePrefab = prototype;
            prewarmCount = Mathf.Max(0, prewarm);
            maximumPoolSize = Mathf.Max(1, cap);
            totalCreated = 0;
            EnsurePrewarmed();
        }

        internal ParticleSystem AcquireForTests(string screenId, int pointerId)
        {
            return GetOrCreate(new PointerKey(screenId, pointerId));
        }

        internal void ReleaseForTests(string screenId, int pointerId)
        {
            Release(new PointerKey(screenId, pointerId));
        }

        internal void ProcessZeroFrameTimeoutsForTests(float now)
        {
            ProcessZeroFrameTimeouts(now);
        }

        private ParticleSystem GetOrCreate(PointerKey key)
        {
            if (active.TryGetValue(key, out var effect) && effect.Particle != null)
            {
                return effect.Particle;
            }

            EnsurePrewarmed();
            ParticleSystem particle;
            if (pool.Count > 0)
            {
                particle = pool.Pop();
            }
            else
            {
                particle = CreatePooledParticle();
            }

            if (particle == null)
            {
                return null;
            }

            particle.gameObject.SetActive(true);
            active[key] = new ActiveEffect(particle, Time.unscaledTime);
            return particle;
        }

        private void Release(PointerKey key)
        {
            if (!active.TryGetValue(key, out var effect))
            {
                return;
            }

            active.Remove(key);
            var particle = effect.Particle;
            if (particle == null)
            {
                return;
            }

            particle.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            particle.gameObject.SetActive(false);
            if (pool.Count < maximumPoolSize)
            {
                pool.Push(particle);
            }
            else
            {
                Destroy(particle.gameObject);
            }
        }

        private void EnsurePrewarmed()
        {
            var desired = Mathf.Min(Mathf.Max(0, prewarmCount), Mathf.Max(1, maximumPoolSize));
            while (pool.Count + active.Count < desired)
            {
                var particle = CreatePooledParticle();
                if (particle == null)
                {
                    break;
                }

                particle.gameObject.SetActive(false);
                pool.Push(particle);
            }
        }

        private ParticleSystem CreatePooledParticle()
        {
            if (particlePrefab == null || totalCreated >= Mathf.Max(1, maximumPoolSize))
            {
                return null;
            }

            var parent = poolRoot != null ? poolRoot : transform;
            var particle = Instantiate(particlePrefab, parent, false);
            particle.name = "RadarPointerParticle_" + totalCreated.ToString("00");
            totalCreated++;
            particle.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            return particle;
        }

        private void ProcessZeroFrameTimeouts(float now)
        {
            if (zeroFrameSince.Count == 0)
            {
                return;
            }

            releaseKeys.Clear();
            foreach (var effect in active)
            {
                if (zeroFrameSince.TryGetValue(effect.Key.ScreenId, out var zeroSince) &&
                    now - zeroSince >= Mathf.Max(0.01f, zeroFrameReleaseDelay))
                {
                    releaseKeys.Add(effect.Key);
                }
            }

            for (var index = 0; index < releaseKeys.Count; index++)
            {
                Release(releaseKeys[index]);
            }
        }

        private static void ApplyAppearance(ParticleSystem particle, string screenId, bool intensified)
        {
            var color = StableScreenColor(screenId);
            if (intensified)
            {
                color = Color.Lerp(color, Color.white, 0.32f);
            }

            var main = particle.main;
            main.startColor = color;
            main.startSize = intensified ? 0.22f : 0.14f;
            main.startLifetime = intensified ? 0.72f : 0.5f;
            var emission = particle.emission;
            emission.rateOverTime = intensified ? 70f : 34f;
        }

        private static Color StableScreenColor(string screenId)
        {
            if (string.Equals(screenId, "left", StringComparison.OrdinalIgnoreCase))
            {
                return new Color(0.16f, 0.87f, 1f, 1f);
            }

            if (string.Equals(screenId, "front", StringComparison.OrdinalIgnoreCase))
            {
                return new Color(1f, 0.72f, 0.18f, 1f);
            }

            if (string.Equals(screenId, "right", StringComparison.OrdinalIgnoreCase))
            {
                return new Color(0.86f, 0.32f, 1f, 1f);
            }

            unchecked
            {
                uint hash = 2166136261;
                var value = screenId ?? string.Empty;
                for (var index = 0; index < value.Length; index++)
                {
                    hash ^= char.ToLowerInvariant(value[index]);
                    hash *= 16777619;
                }

                return Color.HSVToRGB((hash % 360u) / 360f, 0.72f, 1f);
            }
        }

        private readonly struct PointerKey : IEquatable<PointerKey>
        {
            public PointerKey(string screenId, int pointerId)
            {
                ScreenId = screenId == null ? string.Empty : screenId.Trim();
                PointerId = pointerId;
            }

            public string ScreenId { get; }
            public int PointerId { get; }

            public bool Equals(PointerKey other)
            {
                return PointerId == other.PointerId &&
                    string.Equals(ScreenId, other.ScreenId, StringComparison.OrdinalIgnoreCase);
            }

            public override bool Equals(object value)
            {
                return value is PointerKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (StringComparer.OrdinalIgnoreCase.GetHashCode(ScreenId) * 397) ^ PointerId;
                }
            }
        }

        private readonly struct ActiveEffect
        {
            public ActiveEffect(ParticleSystem particle, float lastSeen)
            {
                Particle = particle;
                LastSeen = lastSeen;
            }

            public ParticleSystem Particle { get; }
            public float LastSeen { get; }
        }
    }
}
