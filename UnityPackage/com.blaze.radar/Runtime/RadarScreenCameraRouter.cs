using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blaze.Radar
{
    [Serializable]
    public sealed class RadarScreenCameraBinding
    {
        public string screenId;
        public Camera camera;
        public LayerMask raycastLayerMask = ~0;
        public float maximumRayDistance = 1000f;
    }

    [AddComponentMenu("Blaze/Radar Screen Camera Router")]
    public sealed class RadarScreenCameraRouter : MonoBehaviour
    {
        [SerializeField] private List<RadarScreenCameraBinding> bindings =
            new List<RadarScreenCameraBinding>();

        private readonly Dictionary<string, RadarScreenCameraBinding> bindingsByScreen =
            new Dictionary<string, RadarScreenCameraBinding>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> ambiguousScreenIds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> warningKeys =
            new HashSet<string>(StringComparer.Ordinal);
        private int warningCount;

        internal int WarningCountForTests => warningCount;

        private void Awake()
        {
            RebuildBindings();
        }

        private void OnValidate()
        {
            RebuildBindings();
        }

        public bool TryGetCamera(string screenId, out Camera camera)
        {
            camera = null;
            if (!TryGetBinding(screenId, out var binding))
            {
                return false;
            }

            camera = binding.camera;
            return true;
        }

        public bool TryMapToCameraPixel(
            RadarScreenInfo screen,
            RadarScreenPointer pointer,
            out Vector2 pixel)
        {
            pixel = default(Vector2);
            if (screen == null)
            {
                WarnOnce("map-screen-null", "Cannot map a null logical screen.");
                return false;
            }

            var id = screen.screenId ?? string.Empty;
            if (pointer == null)
            {
                WarnOnce("map-pointer-null:" + id, "Cannot map a null pointer for screen '" + id + "'.");
                return false;
            }

            if (screen.widthPixels <= 0 || screen.heightPixels <= 0)
            {
                WarnOnce(
                    "map-resolution:" + id,
                    "Screen '" + id + "' has an invalid logical resolution " +
                    screen.widthPixels + "x" + screen.heightPixels + ".");
                return false;
            }

            if (!IsFinite(pointer.pixelX) || !IsFinite(pointer.pixelY))
            {
                WarnOnce("map-coordinate:" + id, "Screen '" + id + "' supplied nonfinite logical pixels.");
                return false;
            }

            if (!TryGetBinding(id, out var binding))
            {
                return false;
            }

            var camera = binding.camera;
            float originX;
            float originY;
            float targetWidth;
            float targetHeight;
            if (camera.targetTexture != null)
            {
                originX = 0f;
                originY = 0f;
                targetWidth = camera.targetTexture.width;
                targetHeight = camera.targetTexture.height;
            }
            else
            {
                var pixelRect = camera.pixelRect;
                originX = pixelRect.x;
                originY = pixelRect.y;
                targetWidth = pixelRect.width;
                targetHeight = pixelRect.height;
            }

            if (!IsFinite(originX) || !IsFinite(originY) ||
                !IsFinite(targetWidth) || !IsFinite(targetHeight) ||
                targetWidth <= 0f || targetHeight <= 0f)
            {
                WarnOnce("map-camera-area:" + id, "Camera for screen '" + id + "' has an invalid pixel area.");
                return false;
            }

            pixel = new Vector2(
                originX + pointer.pixelX / screen.widthPixels * targetWidth,
                originY + pointer.pixelY / screen.heightPixels * targetHeight);
            if (!IsFinite(pixel.x) || !IsFinite(pixel.y))
            {
                pixel = default(Vector2);
                WarnOnce("map-result:" + id, "Camera mapping for screen '" + id + "' produced nonfinite pixels.");
                return false;
            }

            return true;
        }

        public bool TryCreateRay(
            RadarScreenInfo screen,
            RadarScreenPointer pointer,
            out Ray ray)
        {
            ray = default(Ray);
            if (!TryMapToCameraPixel(screen, pointer, out var pixel) ||
                !TryGetBinding(screen.screenId, out var binding))
            {
                return false;
            }

            ray = binding.camera.ScreenPointToRay(pixel);
            if (!IsFinite(ray.origin.x) || !IsFinite(ray.origin.y) || !IsFinite(ray.origin.z) ||
                !IsFinite(ray.direction.x) || !IsFinite(ray.direction.y) || !IsFinite(ray.direction.z))
            {
                ray = default(Ray);
                WarnOnce("ray-result:" + screen.screenId, "Camera for screen '" + screen.screenId + "' produced an invalid ray.");
                return false;
            }

            return true;
        }

        public bool TryRaycast(
            RadarScreenInfo screen,
            RadarScreenPointer pointer,
            out RaycastHit hit)
        {
            hit = default(RaycastHit);
            if (!TryCreateRay(screen, pointer, out var ray) ||
                !TryGetBinding(screen.screenId, out var binding))
            {
                return false;
            }

            return Physics.Raycast(
                ray,
                out hit,
                binding.maximumRayDistance,
                binding.raycastLayerMask,
                QueryTriggerInteraction.UseGlobal);
        }

        internal void ConfigureForTests(IEnumerable<RadarScreenCameraBinding> testBindings)
        {
            bindings = testBindings == null
                ? new List<RadarScreenCameraBinding>()
                : new List<RadarScreenCameraBinding>(testBindings);
            RebuildBindings();
        }

        private void RebuildBindings()
        {
            bindingsByScreen.Clear();
            ambiguousScreenIds.Clear();
            if (bindings == null)
            {
                WarnOnce("bindings-null", "Camera binding collection is missing.");
                return;
            }

            var seenScreenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < bindings.Count; index++)
            {
                var binding = bindings[index];
                if (binding == null)
                {
                    WarnOnce("binding-null:" + index, "Camera binding at index " + index + " is null.");
                    continue;
                }

                var id = binding.screenId == null ? string.Empty : binding.screenId.Trim();
                if (id.Length == 0)
                {
                    WarnOnce("binding-id:" + index, "Camera binding at index " + index + " has an empty screen ID.");
                    continue;
                }

                if (!seenScreenIds.Add(id))
                {
                    bindingsByScreen.Remove(id);
                    ambiguousScreenIds.Add(id);
                    WarnOnce("binding-duplicate:" + id.ToLowerInvariant(), "Camera bindings contain duplicate screen ID '" + id + "'.");
                    continue;
                }

                if (binding.camera == null)
                {
                    WarnOnce("binding-camera:" + id, "Camera binding for screen '" + id + "' has no Camera.");
                    continue;
                }

                if (!IsFinite(binding.maximumRayDistance) || binding.maximumRayDistance <= 0f)
                {
                    WarnOnce(
                        "binding-distance:" + id,
                        "Camera binding for screen '" + id + "' has an invalid maximum ray distance.");
                    continue;
                }

                binding.screenId = id;
                bindingsByScreen.Add(id, binding);
            }
        }

        private bool TryGetBinding(string screenId, out RadarScreenCameraBinding binding)
        {
            binding = null;
            var id = screenId == null ? string.Empty : screenId.Trim();
            if (id.Length == 0)
            {
                WarnOnce("lookup-empty", "Cannot route an empty screen ID.");
                return false;
            }

            if (ambiguousScreenIds.Contains(id))
            {
                WarnOnce("lookup-ambiguous:" + id.ToLowerInvariant(), "Screen ID '" + id + "' has ambiguous Camera bindings.");
                return false;
            }

            if (!bindingsByScreen.TryGetValue(id, out binding))
            {
                WarnOnce("lookup-unknown:" + id.ToLowerInvariant(), "No Camera binding exists for screen '" + id + "'.");
                return false;
            }

            if (binding.camera == null)
            {
                binding = null;
                WarnOnce("lookup-camera:" + id.ToLowerInvariant(), "Camera for screen '" + id + "' is unavailable.");
                return false;
            }

            if (!IsFinite(binding.maximumRayDistance) || binding.maximumRayDistance <= 0f)
            {
                binding = null;
                WarnOnce("lookup-distance:" + id.ToLowerInvariant(), "Screen '" + id + "' has an invalid maximum ray distance.");
                return false;
            }

            return true;
        }

        private void WarnOnce(string key, string message)
        {
            if (!warningKeys.Add(key))
            {
                return;
            }

            warningCount++;
            Debug.LogWarning("[Blaze Radar] Camera router: " + message, this);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
