using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blaze.Interaction
{
    [Serializable]
    public sealed class InteractionSurfaceCameraBinding
    {
        public string surfaceId;
        public Camera camera;
        public LayerMask raycastLayerMask = ~0;
        public float maximumRayDistance = 1000f;
    }

    [AddComponentMenu("Blaze/Interaction Camera Router")]
    public sealed class InteractionCameraRouter : MonoBehaviour
    {
        [SerializeField] private List<InteractionSurfaceCameraBinding> bindings =
            new List<InteractionSurfaceCameraBinding>();

        private readonly Dictionary<string, InteractionSurfaceCameraBinding> bindingsBySurface =
            new Dictionary<string, InteractionSurfaceCameraBinding>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> ambiguousSurfaceIds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> warningKeys = new HashSet<string>(StringComparer.Ordinal);
        private int warningCount;

        internal int WarningCountForTests { get { return warningCount; } }

        private void Awake()
        {
            RebuildBindings();
        }

        private void OnValidate()
        {
            RebuildBindings();
        }

        public bool TryGetCamera(string surfaceId, out Camera camera)
        {
            camera = null;
            InteractionSurfaceCameraBinding binding;
            if (!TryGetBinding(surfaceId, out binding))
            {
                return false;
            }

            camera = binding.camera;
            return true;
        }

        public bool TryMapToCameraPixel(
            InteractionSurface surface,
            InteractionPoint point,
            out Vector2 pixel)
        {
            pixel = default(Vector2);
            if (surface == null)
            {
                WarnOnce("map-surface-null", "Cannot map a null logical surface.");
                return false;
            }

            var id = surface.SurfaceId ?? string.Empty;
            if (point == null || point.PixelPosition == null)
            {
                WarnOnce("map-point-null:" + id, "Cannot map an empty point for surface '" + id + "'.");
                return false;
            }

            if (surface.LogicalWidth <= 0 || surface.LogicalHeight <= 0)
            {
                WarnOnce(
                    "map-resolution:" + id,
                    "Surface '" + id + "' has an invalid logical resolution " +
                    surface.LogicalWidth + "x" + surface.LogicalHeight + ".");
                return false;
            }

            if (!IsFinite(point.PixelPosition.X) || !IsFinite(point.PixelPosition.Y))
            {
                WarnOnce("map-coordinate:" + id, "Surface '" + id + "' supplied nonfinite logical pixels.");
                return false;
            }

            InteractionSurfaceCameraBinding binding;
            if (!TryGetBinding(id, out binding))
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
                WarnOnce("map-camera-area:" + id, "Camera for surface '" + id + "' has an invalid pixel area.");
                return false;
            }

            pixel = new Vector2(
                originX + point.PixelPosition.X / surface.LogicalWidth * targetWidth,
                originY + point.PixelPosition.Y / surface.LogicalHeight * targetHeight);
            if (!IsFinite(pixel.x) || !IsFinite(pixel.y))
            {
                pixel = default(Vector2);
                WarnOnce("map-result:" + id, "Camera mapping for surface '" + id + "' produced nonfinite pixels.");
                return false;
            }

            return true;
        }

        public bool TryCreateRay(
            InteractionSurface surface,
            InteractionPoint point,
            out Ray ray)
        {
            ray = default(Ray);
            Vector2 pixel;
            InteractionSurfaceCameraBinding binding;
            if (!TryMapToCameraPixel(surface, point, out pixel) ||
                !TryGetBinding(surface.SurfaceId, out binding))
            {
                return false;
            }

            ray = binding.camera.ScreenPointToRay(pixel);
            if (!IsFinite(ray.origin.x) || !IsFinite(ray.origin.y) || !IsFinite(ray.origin.z) ||
                !IsFinite(ray.direction.x) || !IsFinite(ray.direction.y) || !IsFinite(ray.direction.z))
            {
                ray = default(Ray);
                WarnOnce(
                    "ray-result:" + surface.SurfaceId,
                    "Camera for surface '" + surface.SurfaceId + "' produced an invalid ray.");
                return false;
            }

            return true;
        }

        public bool TryRaycast(
            InteractionSurface surface,
            InteractionPoint point,
            out RaycastHit hit)
        {
            hit = default(RaycastHit);
            Ray ray;
            InteractionSurfaceCameraBinding binding;
            if (!TryCreateRay(surface, point, out ray) ||
                !TryGetBinding(surface.SurfaceId, out binding))
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

        internal void ConfigureForTests(IEnumerable<InteractionSurfaceCameraBinding> testBindings)
        {
            bindings = testBindings == null
                ? new List<InteractionSurfaceCameraBinding>()
                : new List<InteractionSurfaceCameraBinding>(testBindings);
            RebuildBindings();
        }

        private void RebuildBindings()
        {
            bindingsBySurface.Clear();
            ambiguousSurfaceIds.Clear();
            if (bindings == null)
            {
                WarnOnce("bindings-null", "Camera binding collection is missing.");
                return;
            }

            var seenSurfaceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < bindings.Count; index++)
            {
                var binding = bindings[index];
                if (binding == null)
                {
                    WarnOnce("binding-null:" + index, "Camera binding at index " + index + " is null.");
                    continue;
                }

                var id = binding.surfaceId == null ? string.Empty : binding.surfaceId.Trim();
                if (id.Length == 0)
                {
                    WarnOnce("binding-id:" + index, "Camera binding at index " + index + " has an empty surface ID.");
                    continue;
                }

                if (!seenSurfaceIds.Add(id))
                {
                    bindingsBySurface.Remove(id);
                    ambiguousSurfaceIds.Add(id);
                    WarnOnce(
                        "binding-duplicate:" + id.ToLowerInvariant(),
                        "Camera bindings contain duplicate surface ID '" + id + "'.");
                    continue;
                }

                if (binding.camera == null)
                {
                    WarnOnce("binding-camera:" + id, "Camera binding for surface '" + id + "' has no Camera.");
                    continue;
                }

                if (!IsFinite(binding.maximumRayDistance) || binding.maximumRayDistance <= 0f)
                {
                    WarnOnce(
                        "binding-distance:" + id,
                        "Camera binding for surface '" + id + "' has an invalid maximum ray distance.");
                    continue;
                }

                binding.surfaceId = id;
                bindingsBySurface.Add(id, binding);
            }
        }

        private bool TryGetBinding(string surfaceId, out InteractionSurfaceCameraBinding binding)
        {
            binding = null;
            var id = surfaceId == null ? string.Empty : surfaceId.Trim();
            if (id.Length == 0)
            {
                WarnOnce("lookup-empty", "Cannot route an empty surface ID.");
                return false;
            }

            if (ambiguousSurfaceIds.Contains(id))
            {
                WarnOnce(
                    "lookup-ambiguous:" + id.ToLowerInvariant(),
                    "Surface ID '" + id + "' has ambiguous Camera bindings.");
                return false;
            }

            if (!bindingsBySurface.TryGetValue(id, out binding))
            {
                WarnOnce(
                    "lookup-unknown:" + id.ToLowerInvariant(),
                    "No Camera binding exists for surface '" + id + "'.");
                return false;
            }

            if (binding.camera == null)
            {
                binding = null;
                WarnOnce(
                    "lookup-camera:" + id.ToLowerInvariant(),
                    "Camera for surface '" + id + "' is unavailable.");
                return false;
            }

            if (!IsFinite(binding.maximumRayDistance) || binding.maximumRayDistance <= 0f)
            {
                binding = null;
                WarnOnce(
                    "lookup-distance:" + id.ToLowerInvariant(),
                    "Surface '" + id + "' has an invalid maximum ray distance.");
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
            Debug.LogWarning("[Blaze Interaction] Camera router: " + message, this);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
