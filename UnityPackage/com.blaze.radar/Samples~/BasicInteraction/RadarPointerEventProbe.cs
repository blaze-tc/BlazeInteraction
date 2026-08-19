using UnityEngine;
using UnityEngine.EventSystems;

namespace Blaze.Radar.Samples
{
    /// <summary>
    /// Makes the standard EventSystem callbacks on a UGUI control visible in RadarDemoLogger.
    /// </summary>
    public sealed class RadarPointerEventProbe : MonoBehaviour,
        IPointerEnterHandler,
        IPointerExitHandler,
        IPointerDownHandler,
        IPointerUpHandler,
        IPointerClickHandler
    {
        [SerializeField] private RadarDemoLogger demoLogger;
        [SerializeField] private string sourceLabel;

        public void OnPointerEnter(PointerEventData eventData) => Report("PointerEnter", eventData);
        public void OnPointerExit(PointerEventData eventData) => Report("PointerExit", eventData);
        public void OnPointerDown(PointerEventData eventData) => Report("PointerDown", eventData);
        public void OnPointerUp(PointerEventData eventData) => Report("PointerUp", eventData);
        public void OnPointerClick(PointerEventData eventData) => Report("PointerClick", eventData);

        private void Report(string eventName, PointerEventData eventData)
        {
            if (demoLogger != null)
            {
                demoLogger.LogPointerEvent(
                    string.IsNullOrWhiteSpace(sourceLabel) ? name : sourceLabel,
                    eventName,
                    eventData);
            }
        }
    }
}
