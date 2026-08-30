using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace Blaze.Interaction.Samples
{
    public sealed class BasicInteractionPresenter : MonoBehaviour
    {
        [SerializeField] private Text statusText;
        [SerializeField] private int maximumVisiblePoints = 8;
        [SerializeField] private RectTransform cursor;
        [SerializeField] private Image cursorImage;
        [SerializeField] private float cursorSize = 32f;
        [SerializeField] private float footprintParticleSize = 10f;
        [SerializeField] private float footprintLifetimeSeconds = 0.5f;
        [SerializeField] private Color footprintParticleColor = new Color(0.1f, 0.9f, 1f, 0.9f);
        private readonly StringBuilder text = new StringBuilder(256);
        private readonly List<FootprintParticle> activeFootprintParticles = new List<FootprintParticle>();
        private readonly Stack<FootprintParticle> footprintParticlePool = new Stack<FootprintParticle>();
        private RectTransform footprintParticleRoot;

        private void OnEnable()
        {
            EnsureCursor();
            InteractionManager.Instance.ConnectionChanged += OnConnectionChanged;
            InteractionManager.Instance.PointAdded += OnPointChanged;
            InteractionManager.Instance.PointUpdated += OnPointUpdated;
            InteractionManager.Instance.PointRemoved += OnPointChanged;
            if (footprintParticleRoot != null) footprintParticleRoot.gameObject.SetActive(true);
            Refresh();
        }

        private void OnDisable()
        {
            InteractionManager.Instance.ConnectionChanged -= OnConnectionChanged;
            InteractionManager.Instance.PointAdded -= OnPointChanged;
            InteractionManager.Instance.PointUpdated -= OnPointUpdated;
            InteractionManager.Instance.PointRemoved -= OnPointChanged;
            if (cursor != null) cursor.gameObject.SetActive(false);
            ReleaseAllFootprintParticles();
            if (footprintParticleRoot != null) footprintParticleRoot.gameObject.SetActive(false);
        }

        private void Update() { UpdateFootprintParticles(); }

        private void OnConnectionChanged(bool connected) { Refresh(); }
        private void OnPointChanged(InteractionPoint point) { Refresh(); }

        private void OnPointUpdated(InteractionPoint point)
        {
            EmitFootprintParticles(point);
            Refresh();
        }

        private void Refresh()
        {
            if (statusText == null) return;
            var manager = InteractionManager.Instance;
            text.Length = 0;
            text.Append("IPC: ").Append(manager.IsConnected ? "CONNECTED" : "DISCONNECTED").AppendLine();
            text.Append("Provider: ").Append(manager.ActiveProvider == null ? "none" : manager.ActiveProvider.Id).AppendLine();
            var count = Mathf.Min(maximumVisiblePoints, manager.Points.Count);
            for (var index = 0; index < count; index++)
            {
                var point = manager.Points[index];
                text.Append(point.SurfaceId).Append(" / ").Append(point.Id).Append(" / ")
                    .Append(point.Phase).AppendLine();
            }
            statusText.text = text.ToString();
            UpdateCursor();
        }

        private void EnsureCursor()
        {
            if (cursor != null) return;
            if (statusText == null || statusText.canvas == null) return;

            var cursorObject = new GameObject(
                "Interaction Cursor",
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image));
            cursorObject.transform.SetParent(statusText.canvas.transform, false);
            cursor = cursorObject.GetComponent<RectTransform>();
            cursor.sizeDelta = new Vector2(cursorSize, cursorSize);
            cursor.pivot = new Vector2(.5f, .5f);
            cursorImage = cursorObject.GetComponent<Image>();
            cursorImage.color = new Color(1f, .85f, .1f, .9f);
            cursorImage.raycastTarget = false;
            cursorObject.SetActive(false);
        }

        private void UpdateCursor()
        {
            EnsureCursor();
            if (cursor == null) return;

            var points = InteractionManager.Instance.Points;
            if (points.Count == 0)
            {
                cursor.gameObject.SetActive(false);
                return;
            }

            var point = points[0];
            var anchor = new Vector2(
                Mathf.Clamp01(point.NormalizedPosition.X),
                Mathf.Clamp01(point.NormalizedPosition.Y));
            cursor.anchorMin = anchor;
            cursor.anchorMax = anchor;
            cursor.anchoredPosition = Vector2.zero;
            cursor.gameObject.SetActive(true);
        }

        private void EmitFootprintParticles(InteractionPoint point)
        {
            if (point == null || point.Fp == null || point.Fp.Count == 0) return;
            var surface = FindSurface(point.SurfaceId);
            if (surface == null || surface.LogicalWidth <= 0 || surface.LogicalHeight <= 0) return;

            EnsureFootprintParticleRoot();
            if (footprintParticleRoot == null) return;

            var now = Time.unscaledTime;
            var lifetime = Mathf.Max(0.01f, footprintLifetimeSeconds);
            for (var index = 0; index < point.Fp.Count; index++)
            {
                var value = point.Fp[index];
                if (value == null || !IsFinite(value.X) || !IsFinite(value.Y)) continue;

                var anchor = new Vector2(
                    Mathf.Clamp01(value.X / surface.LogicalWidth),
                    Mathf.Clamp01(value.Y / surface.LogicalHeight));
                var particle = AcquireFootprintParticle();
                particle.Rect.anchorMin = anchor;
                particle.Rect.anchorMax = anchor;
                particle.Rect.anchoredPosition = Vector2.zero;
                particle.Rect.localScale = Vector3.one;
                particle.Image.color = footprintParticleColor;
                particle.BornAt = now;
                particle.ExpiresAt = now + lifetime;
                activeFootprintParticles.Add(particle);
            }
        }

        private void UpdateFootprintParticles()
        {
            var now = Time.unscaledTime;
            for (var index = activeFootprintParticles.Count - 1; index >= 0; index--)
            {
                var particle = activeFootprintParticles[index];
                if (now >= particle.ExpiresAt)
                {
                    activeFootprintParticles.RemoveAt(index);
                    ReleaseFootprintParticle(particle);
                    continue;
                }

                var duration = Mathf.Max(0.01f, particle.ExpiresAt - particle.BornAt);
                var progress = Mathf.Clamp01((now - particle.BornAt) / duration);
                var color = footprintParticleColor;
                color.a *= 1f - progress;
                particle.Image.color = color;
                particle.Rect.localScale = Vector3.one * Mathf.Lerp(1f, 0.35f, progress);
            }
        }

        private InteractionSurface FindSurface(string surfaceId)
        {
            var surfaces = InteractionManager.Instance.Surfaces;
            for (var index = 0; index < surfaces.Count; index++)
            {
                var surface = surfaces[index];
                if (surface != null && string.Equals(surface.SurfaceId, surfaceId, System.StringComparison.OrdinalIgnoreCase))
                {
                    return surface;
                }
            }

            return null;
        }

        private void EnsureFootprintParticleRoot()
        {
            if (footprintParticleRoot != null) return;
            if (statusText == null || statusText.canvas == null) return;

            var rootObject = new GameObject("Footprint Particles", typeof(RectTransform));
            rootObject.transform.SetParent(statusText.canvas.transform, false);
            footprintParticleRoot = rootObject.GetComponent<RectTransform>();
            footprintParticleRoot.anchorMin = Vector2.zero;
            footprintParticleRoot.anchorMax = Vector2.one;
            footprintParticleRoot.offsetMin = Vector2.zero;
            footprintParticleRoot.offsetMax = Vector2.zero;
            footprintParticleRoot.SetAsLastSibling();
            if (cursor != null) cursor.SetAsLastSibling();
        }

        private FootprintParticle AcquireFootprintParticle()
        {
            FootprintParticle particle;
            if (footprintParticlePool.Count > 0)
            {
                particle = footprintParticlePool.Pop();
            }
            else
            {
                var particleObject = new GameObject(
                    "Footprint Particle",
                    typeof(RectTransform),
                    typeof(CanvasRenderer),
                    typeof(Image));
                particleObject.transform.SetParent(footprintParticleRoot, false);
                var rect = particleObject.GetComponent<RectTransform>();
                var image = particleObject.GetComponent<Image>();
                image.raycastTarget = false;
                particle = new FootprintParticle(rect, image);
            }

            particle.Rect.sizeDelta = new Vector2(footprintParticleSize, footprintParticleSize);
            particle.Rect.gameObject.SetActive(true);
            return particle;
        }

        private void ReleaseFootprintParticle(FootprintParticle particle)
        {
            particle.Rect.gameObject.SetActive(false);
            footprintParticlePool.Push(particle);
        }

        private void ReleaseAllFootprintParticles()
        {
            for (var index = activeFootprintParticles.Count - 1; index >= 0; index--)
            {
                ReleaseFootprintParticle(activeFootprintParticles[index]);
            }

            activeFootprintParticles.Clear();
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private sealed class FootprintParticle
        {
            public FootprintParticle(RectTransform rect, Image image)
            {
                Rect = rect;
                Image = image;
            }

            public RectTransform Rect { get; private set; }
            public Image Image { get; private set; }
            public float BornAt { get; set; }
            public float ExpiresAt { get; set; }
        }
    }
}
