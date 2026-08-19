using UnityEngine;
using UnityEngine.EventSystems;

namespace Blaze.Radar.Samples
{
    /// <summary>
    /// Logs drag and scroll callbacks only on controls that already own those interactions.
    /// Keeping this separate prevents a normal Button or Toggle from becoming a drag handler.
    /// </summary>
    public sealed class RadarDragEventProbe : MonoBehaviour,
        IInitializePotentialDragHandler,
        IBeginDragHandler,
        IDragHandler,
        IEndDragHandler,
        IScrollHandler,
        IDropHandler
    {
        [SerializeField] private RadarDemoLogger demoLogger;
        [SerializeField] private string sourceLabel;

        public void OnInitializePotentialDrag(PointerEventData eventData) => Report("InitializeDrag", eventData);
        public void OnBeginDrag(PointerEventData eventData) => Report("BeginDrag", eventData);
        public void OnDrag(PointerEventData eventData) => Report("Drag", eventData, true);
        public void OnEndDrag(PointerEventData eventData) => Report("EndDrag", eventData);
        public void OnScroll(PointerEventData eventData) => Report("Scroll", eventData, true);
        public void OnDrop(PointerEventData eventData) => Report("Drop", eventData);

        private void Report(string eventName, PointerEventData eventData, bool continuous = false)
        {
            if (demoLogger != null)
            {
                demoLogger.LogPointerEvent(
                    string.IsNullOrWhiteSpace(sourceLabel) ? name : sourceLabel,
                    eventName,
                    eventData,
                    continuous);
            }
        }
    }
}
