using UnityEngine;
using UnityEngine.EventSystems;

namespace Blaze.Radar.Samples
{
    /// <summary>
    /// Uses standard EventSystem interfaces for both PhysicsRaycaster and Physics2DRaycaster targets.
    /// </summary>
    public sealed class SamplePointerTarget : MonoBehaviour,
        IPointerEnterHandler,
        IPointerExitHandler,
        IPointerDownHandler,
        IPointerUpHandler,
        IPointerClickHandler,
        IBeginDragHandler,
        IDragHandler,
        IEndDragHandler
    {
        [Header("Target")]
        [SerializeField] private Camera eventCamera;
        [SerializeField] private Renderer targetRenderer;
        [SerializeField] private RadarDemoLogger demoLogger;

        [Header("Feedback")]
        [SerializeField] private Color normalColor = new Color32(56, 211, 214, 255);
        [SerializeField] private Color hoverColor = new Color32(142, 246, 232, 255);
        [SerializeField] private Color pressedColor = new Color32(247, 184, 89, 255);

        private Vector3 _baseScale;
        private float _screenDepth;
        private Material _runtimeMaterial;

        private void Awake()
        {
            _baseScale = transform.localScale;
            eventCamera = eventCamera != null ? eventCamera : Camera.main;
            targetRenderer = targetRenderer != null ? targetRenderer : GetComponent<Renderer>();
            if (targetRenderer != null)
            {
                var unlitShader = Shader.Find("Unlit/Color");
                _runtimeMaterial = unlitShader != null
                    ? new Material(unlitShader)
                    : new Material(targetRenderer.sharedMaterial);
                targetRenderer.material = _runtimeMaterial;
            }

            _screenDepth = eventCamera != null
                ? Mathf.Abs(eventCamera.transform.position.z - transform.position.z)
                : 10f;
            SetColor(normalColor);
        }

        private void OnDestroy()
        {
            if (_runtimeMaterial != null)
            {
                Destroy(_runtimeMaterial);
            }
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            transform.localScale = _baseScale * 1.08f;
            SetColor(hoverColor);
            Report("PointerEnter", eventData);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            transform.localScale = _baseScale;
            SetColor(normalColor);
            Report("PointerExit", eventData);
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            SetColor(pressedColor);
            Report("PointerDown", eventData);
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            SetColor(hoverColor);
            Report("PointerUp", eventData);
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            Report("PointerClick", eventData);
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            Report("BeginDrag", eventData);
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (eventCamera != null)
            {
                var screenPoint = new Vector3(eventData.position.x, eventData.position.y, _screenDepth);
                var worldPoint = eventCamera.ScreenToWorldPoint(screenPoint);
                transform.position = new Vector3(worldPoint.x, worldPoint.y, transform.position.z);
            }

            Report("Drag", eventData, true);
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            Report("EndDrag", eventData);
        }

        private void SetColor(Color color)
        {
            if (_runtimeMaterial != null)
            {
                _runtimeMaterial.color = color;
            }
        }

        private void Report(string eventName, PointerEventData eventData, bool continuous = false)
        {
            if (demoLogger != null)
            {
                demoLogger.LogPointerEvent(name, eventName, eventData, continuous);
            }
        }
    }
}
