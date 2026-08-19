using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blaze.Radar.Samples
{
    /// <summary>
    /// Minimal example of binding RadarFrameDispatcher.ScreenPointerReceived and
    /// converting one logical screen's pixel coordinates into a Camera world position.
    /// </summary>
    [AddComponentMenu("Blaze/Samples/Radar Pointer Particle Binder")]
    public sealed class RadarPointerParticleBinder : MonoBehaviour
    {
        [Header("Point Delegate")]
        [SerializeField] private RadarFrameDispatcher radarDispatcher;
        [SerializeField] private string screenId = "main";

        [Header("World Projection")]
        [SerializeField] private Camera targetCamera;
        [SerializeField, Min(0.01f)] private float distanceFromCamera = 8f;

        [Header("Particle Feedback")]
        [SerializeField] private ParticleSystem particlePrefab;
        [SerializeField, Range(1, 32)] private int hoverParticleCount = 1;
        [SerializeField, Range(1, 32)] private int downParticleCount = 8;
        [SerializeField, Range(1, 32)] private int upParticleCount = 4;
        [SerializeField, Min(0f)] private float moveEmissionIntervalSeconds = 0.04f;
        [SerializeField] private RadarDemoLogger demoLogger;

        private readonly Dictionary<int, float> nextMoveEmissionTimes = new Dictionary<int, float>();
        private ParticleSystem pointerParticleSystem;
        private bool subscribed;

        private void Awake()
        {
            if (targetCamera == null)
            {
                targetCamera = Camera.main;
            }

            if (particlePrefab != null)
            {
                // Keep the world-space particle system outside the UGUI Canvas hierarchy so
                // Canvas scaling cannot distort the camera-projected position.
                pointerParticleSystem = Instantiate(particlePrefab);
                pointerParticleSystem.name = "Radar Pointer Particles (Runtime)";
            }

            if (pointerParticleSystem != null)
            {
                var main = pointerParticleSystem.main;
                main.playOnAwake = false;
                main.simulationSpace = ParticleSystemSimulationSpace.World;
            }
        }

        private void OnEnable()
        {
            Subscribe();
        }

        private void Start()
        {
            // OnEnable can run before scene references finish deserializing after a domain reload.
            Subscribe();
        }

        private void OnDisable()
        {
            if (!subscribed || radarDispatcher == null)
            {
                return;
            }

            radarDispatcher.ScreenPointerReceived -= OnScreenPointerReceived;
            subscribed = false;
            nextMoveEmissionTimes.Clear();
        }

        private void Subscribe()
        {
            if (subscribed || radarDispatcher == null)
            {
                return;
            }

            radarDispatcher.ScreenPointerReceived += OnScreenPointerReceived;
            subscribed = true;
        }

        private void OnScreenPointerReceived(RadarScreenInfo screen, RadarScreenPointer pointer)
        {
            if (screen == null || pointer == null || targetCamera == null || pointerParticleSystem == null ||
                screen.widthPixels <= 0 || screen.heightPixels <= 0 ||
                !string.Equals(screen.screenId, screenId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!ShouldEmit(pointer))
            {
                return;
            }

            var cameraRect = targetCamera.pixelRect;
            var cameraPixel = new Vector3(
                cameraRect.x + Mathf.Clamp01(pointer.pixelX / screen.widthPixels) * cameraRect.width,
                cameraRect.y + Mathf.Clamp01(pointer.pixelY / screen.heightPixels) * cameraRect.height,
                Mathf.Max(targetCamera.nearClipPlane + 0.01f, distanceFromCamera));
            var worldPosition = targetCamera.ScreenToWorldPoint(cameraPixel);
            pointerParticleSystem.transform.position = worldPosition;
            pointerParticleSystem.Emit(ParticleCount(pointer.phase));

            demoLogger?.LogContinuousUiEvent(
                "PointDelegate",
                $"screen {screen.screenId} pointer {pointer.pointerId} {pointer.phase} " +
                $"pixel ({pointer.pixelX:0.0}, {pointer.pixelY:0.0}) world {worldPosition}");
        }

        private bool ShouldEmit(RadarScreenPointer pointer)
        {
            if (pointer.phase == RadarPointerPhase.Down || pointer.phase == RadarPointerPhase.Up)
            {
                nextMoveEmissionTimes[pointer.pointerId] = Time.unscaledTime + moveEmissionIntervalSeconds;
                return true;
            }

            if (nextMoveEmissionTimes.TryGetValue(pointer.pointerId, out var nextTime) &&
                Time.unscaledTime < nextTime)
            {
                return false;
            }

            nextMoveEmissionTimes[pointer.pointerId] = Time.unscaledTime + moveEmissionIntervalSeconds;
            return true;
        }

        private int ParticleCount(RadarPointerPhase phase)
        {
            switch (phase)
            {
                case RadarPointerPhase.Down:
                    return downParticleCount;
                case RadarPointerPhase.Up:
                    return upParticleCount;
                default:
                    return hoverParticleCount;
            }
        }
    }
}
