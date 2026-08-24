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
        private readonly StringBuilder text = new StringBuilder(256);

        private void OnEnable()
        {
            EnsureCursor();
            InteractionManager.Instance.ConnectionChanged += OnConnectionChanged;
            InteractionManager.Instance.PointAdded += OnPointChanged;
            InteractionManager.Instance.PointUpdated += OnPointChanged;
            InteractionManager.Instance.PointRemoved += OnPointChanged;
            Refresh();
        }

        private void OnDisable()
        {
            InteractionManager.Instance.ConnectionChanged -= OnConnectionChanged;
            InteractionManager.Instance.PointAdded -= OnPointChanged;
            InteractionManager.Instance.PointUpdated -= OnPointChanged;
            InteractionManager.Instance.PointRemoved -= OnPointChanged;
            if (cursor != null) cursor.gameObject.SetActive(false);
        }

        private void OnConnectionChanged(bool connected) { Refresh(); }
        private void OnPointChanged(InteractionPoint point) { Refresh(); }

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
    }
}
