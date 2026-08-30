using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Blaze.Interaction.Samples
{
    internal sealed class HandSkeletonVisualPool
    {
        private readonly RectTransform parent;
        private readonly float jointSize;
        private readonly float boneThickness;
        private readonly Stack<HandSkeletonVisual> available = new Stack<HandSkeletonVisual>();

        internal HandSkeletonVisualPool(
            RectTransform parent,
            float jointSize,
            float boneThickness)
        {
            this.parent = parent;
            this.jointSize = Mathf.Max(1f, jointSize);
            this.boneThickness = Mathf.Max(1f, boneThickness);
        }

        internal int CreatedTrackCount { get; private set; }

        internal HandSkeletonVisual Acquire(HandSkeletonKey key, Color color)
        {
            HandSkeletonVisual visual;
            if (available.Count > 0)
            {
                visual = available.Pop();
            }
            else
            {
                visual = CreateVisual();
                CreatedTrackCount++;
            }

            visual.Root.name = "Hand Skeleton Track " + key.PointId;
            visual.Root.gameObject.SetActive(true);
            visual.SetColor(color);
            return visual;
        }

        internal void Release(HandSkeletonVisual visual)
        {
            if (visual == null)
            {
                return;
            }

            visual.Root.gameObject.SetActive(false);
            available.Push(visual);
        }

        private HandSkeletonVisual CreateVisual()
        {
            var rootObject = new GameObject("Hand Skeleton Track", typeof(RectTransform));
            rootObject.transform.SetParent(parent, false);
            var root = rootObject.GetComponent<RectTransform>();
            root.anchorMin = Vector2.zero;
            root.anchorMax = Vector2.one;
            root.offsetMin = Vector2.zero;
            root.offsetMax = Vector2.zero;

            var boneRects = new RectTransform[HandSkeletonConnections.All.Count];
            var boneImages = new Image[HandSkeletonConnections.All.Count];
            for (var index = 0; index < boneRects.Length; index++)
            {
                var boneObject = new GameObject(
                    "Bone " + index,
                    typeof(RectTransform),
                    typeof(CanvasRenderer),
                    typeof(Image));
                boneObject.transform.SetParent(root, false);
                boneRects[index] = boneObject.GetComponent<RectTransform>();
                boneRects[index].anchorMin = new Vector2(.5f, .5f);
                boneRects[index].anchorMax = new Vector2(.5f, .5f);
                boneRects[index].pivot = new Vector2(.5f, .5f);
                boneImages[index] = boneObject.GetComponent<Image>();
                boneImages[index].raycastTarget = false;
            }

            var jointRects = new RectTransform[HandInteractionExtension.LandmarkCount];
            var jointImages = new Image[HandInteractionExtension.LandmarkCount];
            for (var index = 0; index < jointRects.Length; index++)
            {
                var jointObject = new GameObject(
                    "Joint " + index,
                    typeof(RectTransform),
                    typeof(CanvasRenderer),
                    typeof(Image));
                jointObject.transform.SetParent(root, false);
                jointRects[index] = jointObject.GetComponent<RectTransform>();
                jointRects[index].sizeDelta = new Vector2(jointSize, jointSize);
                jointRects[index].pivot = new Vector2(.5f, .5f);
                jointImages[index] = jointObject.GetComponent<Image>();
                jointImages[index].raycastTarget = false;
            }

            return new HandSkeletonVisual(
                root,
                jointRects,
                jointImages,
                boneRects,
                boneImages,
                boneThickness);
        }
    }

    internal sealed class HandSkeletonVisual
    {
        private readonly RectTransform[] jointRects;
        private readonly Image[] jointImages;
        private readonly RectTransform[] boneRects;
        private readonly Image[] boneImages;
        private readonly float boneThickness;

        internal HandSkeletonVisual(
            RectTransform root,
            RectTransform[] jointRects,
            Image[] jointImages,
            RectTransform[] boneRects,
            Image[] boneImages,
            float boneThickness)
        {
            Root = root;
            this.jointRects = jointRects;
            this.jointImages = jointImages;
            this.boneRects = boneRects;
            this.boneImages = boneImages;
            this.boneThickness = boneThickness;
        }

        internal RectTransform Root { get; private set; }

        internal void SetColor(Color color)
        {
            for (var index = 0; index < jointImages.Length; index++)
            {
                jointImages[index].color = color;
            }

            var boneColor = color;
            boneColor.a *= .65f;
            for (var index = 0; index < boneImages.Length; index++)
            {
                boneImages[index].color = boneColor;
            }
        }

        internal void UpdateGeometry(HandSkeletonTrackGeometry geometry)
        {
            for (var index = 0; index < geometry.Joints.Count; index++)
            {
                var normalized = geometry.Joints[index].NormalizedPosition;
                var anchor = new Vector2(normalized.X, normalized.Y);
                jointRects[index].anchorMin = anchor;
                jointRects[index].anchorMax = anchor;
                jointRects[index].anchoredPosition = Vector2.zero;
            }

            var size = Root.rect.size;
            for (var index = 0; index < geometry.Bones.Count; index++)
            {
                var bone = geometry.Bones[index];
                var start = new Vector2(
                    (bone.StartPosition.X - .5f) * size.x,
                    (bone.StartPosition.Y - .5f) * size.y);
                var end = new Vector2(
                    (bone.EndPosition.X - .5f) * size.x,
                    (bone.EndPosition.Y - .5f) * size.y);
                var delta = end - start;
                boneRects[index].anchoredPosition = (start + end) * .5f;
                boneRects[index].sizeDelta = new Vector2(delta.magnitude, boneThickness);
                boneRects[index].localRotation = Quaternion.Euler(
                    0f,
                    0f,
                    Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
            }
        }
    }
}
