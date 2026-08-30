using UnityEngine;

namespace Blaze.Interaction.Samples
{
    public sealed class MultiSurfaceRoutingPresenter : MonoBehaviour
    {
        [SerializeField] private InteractionCameraRouter router;

        private void OnEnable()
        {
            InteractionManager.Instance.PointAdded += Route;
            InteractionManager.Instance.PointUpdated += Route;
            InteractionManager.Instance.PointRemoved += Route;
        }

        private void OnDisable()
        {
            InteractionManager.Instance.PointAdded -= Route;
            InteractionManager.Instance.PointUpdated -= Route;
            InteractionManager.Instance.PointRemoved -= Route;
        }

        private void Route(InteractionPoint point)
        {
            if (router == null || point == null || point.PixelPosition == null) return;
            InteractionSurface surface = null;
            var surfaces = InteractionManager.Instance.Surfaces;
            for (var index = 0; index < surfaces.Count; index++)
            {
                if (surfaces[index] != null && surfaces[index].SurfaceId == point.SurfaceId)
                {
                    surface = surfaces[index];
                    break;
                }
            }

            if (surface == null) return;
            Ray ray;
            if (router.TryCreateRay(surface, point, out ray))
            {
                Debug.DrawRay(ray.origin, ray.direction * 10f, Color.cyan, 0.1f);
            }
        }
    }
}
