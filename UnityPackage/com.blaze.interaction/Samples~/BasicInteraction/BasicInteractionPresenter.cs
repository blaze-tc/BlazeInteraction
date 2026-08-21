using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace Blaze.Interaction.Samples
{
    public sealed class BasicInteractionPresenter : MonoBehaviour
    {
        [SerializeField] private Text statusText;
        [SerializeField] private int maximumVisiblePoints = 8;
        private readonly StringBuilder text = new StringBuilder(256);

        private void OnEnable()
        {
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
        }
    }
}
