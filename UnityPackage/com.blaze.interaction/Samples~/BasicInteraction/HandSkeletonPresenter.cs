using System.Collections.Generic;
using UnityEngine;

namespace Blaze.Interaction.Samples
{
    public sealed class HandSkeletonPresenter : MonoBehaviour
    {
        private static readonly Color[] TrackPalette =
        {
            new Color(.15f, .85f, 1f, .95f),
            new Color(1f, .45f, .25f, .95f),
            new Color(.4f, 1f, .45f, .95f),
            new Color(1f, .8f, .2f, .95f),
            new Color(.75f, .45f, 1f, .95f),
            new Color(1f, .35f, .75f, .95f),
            new Color(.35f, .7f, 1f, .95f),
            new Color(.75f, 1f, .3f, .95f)
        };

        [SerializeField] private RectTransform visualRoot;
        [SerializeField] private float jointSize = 12f;
        [SerializeField] private float boneThickness = 4f;

        private readonly HandSkeletonGeometry geometry = new HandSkeletonGeometry();
        private readonly Dictionary<HandSkeletonKey, HandSkeletonVisual> visuals =
            new Dictionary<HandSkeletonKey, HandSkeletonVisual>();
        private RectTransform skeletonRoot;
        private HandSkeletonVisualPool visualPool;

        public int ActiveTrackCount { get { return visuals.Count; } }
        public int ActiveJointVisualCount { get { return visuals.Count * HandInteractionExtension.LandmarkCount; } }
        public int ActiveBoneVisualCount { get { return visuals.Count * HandSkeletonConnections.All.Count; } }
        public int CreatedTrackVisualCount { get { return visualPool == null ? 0 : visualPool.CreatedTrackCount; } }

        private void OnEnable()
        {
            EnsurePool();
            var manager = InteractionManager.Instance;
            manager.PointAdded += OnPointChanged;
            manager.PointUpdated += OnPointChanged;
            manager.PointRemoved += OnPointRemoved;
            manager.ConnectionChanged += OnConnectionChanged;
            if (skeletonRoot != null)
            {
                skeletonRoot.gameObject.SetActive(true);
            }

            for (var index = 0; index < manager.Points.Count; index++)
            {
                OnPointChanged(manager.Points[index]);
            }
        }

        private void OnDisable()
        {
            var manager = InteractionManager.Instance;
            manager.PointAdded -= OnPointChanged;
            manager.PointUpdated -= OnPointChanged;
            manager.PointRemoved -= OnPointRemoved;
            manager.ConnectionChanged -= OnConnectionChanged;
            ClearVisuals();
            if (skeletonRoot != null)
            {
                skeletonRoot.gameObject.SetActive(false);
            }
        }

        private void OnPointChanged(InteractionPoint point)
        {
            var surface = FindSurface(point == null ? null : point.SurfaceId);
            HandSkeletonTrackGeometry track;
            if (!geometry.TryApply(point, surface, out track))
            {
                if (point != null)
                {
                    ReleaseVisual(new HandSkeletonKey(
                        point.ProviderInstanceId,
                        point.SurfaceId,
                        point.Id));
                }

                return;
            }

            EnsurePool();
            if (visualPool == null)
            {
                return;
            }

            HandSkeletonVisual visual;
            if (!visuals.TryGetValue(track.Key, out visual))
            {
                visual = visualPool.Acquire(track.Key, ColorForTrack(track.Key.PointId));
                visuals.Add(track.Key, visual);
            }

            Canvas.ForceUpdateCanvases();
            visual.UpdateGeometry(track);
        }

        private void OnPointRemoved(InteractionPoint point)
        {
            if (point == null)
            {
                return;
            }

            geometry.Remove(point);
            ReleaseVisual(new HandSkeletonKey(
                point.ProviderInstanceId,
                point.SurfaceId,
                point.Id));
        }

        private void OnConnectionChanged(bool connected)
        {
            if (!connected)
            {
                ClearVisuals();
            }
        }

        private InteractionSurface FindSurface(string surfaceId)
        {
            var surfaces = InteractionManager.Instance.Surfaces;
            for (var index = 0; index < surfaces.Count; index++)
            {
                var surface = surfaces[index];
                if (surface != null &&
                    string.Equals(surface.SurfaceId, surfaceId, System.StringComparison.OrdinalIgnoreCase))
                {
                    return surface;
                }
            }

            return null;
        }

        private void EnsurePool()
        {
            if (visualPool != null)
            {
                return;
            }

            if (visualRoot == null)
            {
                var canvas = FindObjectOfType<Canvas>();
                if (canvas != null)
                {
                    visualRoot = canvas.transform as RectTransform;
                }
            }

            if (visualRoot == null)
            {
                return;
            }

            var rootObject = new GameObject("Hand Skeletons", typeof(RectTransform));
            rootObject.transform.SetParent(visualRoot, false);
            skeletonRoot = rootObject.GetComponent<RectTransform>();
            skeletonRoot.anchorMin = Vector2.zero;
            skeletonRoot.anchorMax = Vector2.one;
            skeletonRoot.offsetMin = Vector2.zero;
            skeletonRoot.offsetMax = Vector2.zero;
            skeletonRoot.SetAsLastSibling();
            visualPool = new HandSkeletonVisualPool(skeletonRoot, jointSize, boneThickness);
        }

        private void ReleaseVisual(HandSkeletonKey key)
        {
            HandSkeletonVisual visual;
            if (!visuals.TryGetValue(key, out visual))
            {
                return;
            }

            visuals.Remove(key);
            visualPool.Release(visual);
        }

        private void ClearVisuals()
        {
            foreach (var pair in visuals)
            {
                visualPool.Release(pair.Value);
            }

            visuals.Clear();
            geometry.Clear();
        }

        private static Color ColorForTrack(long pointId)
        {
            var hash = unchecked((int)(pointId ^ (pointId >> 32))) & int.MaxValue;
            return TrackPalette[hash % TrackPalette.Length];
        }
    }
}
