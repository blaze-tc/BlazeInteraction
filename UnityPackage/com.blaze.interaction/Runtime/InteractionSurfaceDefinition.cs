using System;
using UnityEngine;

namespace Blaze.Interaction
{
    [Serializable]
    public sealed class InteractionSurfaceDefinition
    {
        [SerializeField] private string surfaceId = "main";
        [SerializeField] private string displayName = "Main";
        [SerializeField] private int logicalWidth = 1920;
        [SerializeField] private int logicalHeight = 1080;
        [SerializeField] private bool enabled = true;
        [SerializeField] private bool isPrimary = true;
        [SerializeField] private int order;

        public InteractionSurfaceDefinition()
        {
        }

        public InteractionSurfaceDefinition(
            string surfaceId,
            string displayName,
            int logicalWidth,
            int logicalHeight,
            bool enabled,
            bool isPrimary,
            int order)
        {
            this.surfaceId = surfaceId;
            this.displayName = displayName;
            this.logicalWidth = logicalWidth;
            this.logicalHeight = logicalHeight;
            this.enabled = enabled;
            this.isPrimary = isPrimary;
            this.order = order;
        }

        public string SurfaceId { get { return surfaceId; } }
        public string DisplayName { get { return displayName; } }
        public int LogicalWidth { get { return Mathf.Max(1, logicalWidth); } }
        public int LogicalHeight { get { return Mathf.Max(1, logicalHeight); } }
        public bool Enabled { get { return enabled; } }
        public bool IsPrimary { get { return isPrimary; } }
        public int Order { get { return order; } }

        internal int AuthoredLogicalWidth { get { return logicalWidth; } }
        internal int AuthoredLogicalHeight { get { return logicalHeight; } }

        public InteractionSurface ToPayload()
        {
            return new InteractionSurface
            {
                SurfaceId = SurfaceId,
                Name = string.IsNullOrWhiteSpace(DisplayName) ? SurfaceId : DisplayName,
                LogicalWidth = LogicalWidth,
                LogicalHeight = LogicalHeight,
                IsPrimary = IsPrimary,
                Order = Order
            };
        }
    }
}
