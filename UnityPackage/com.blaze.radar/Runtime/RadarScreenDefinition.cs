using System;
using UnityEngine;

namespace Blaze.Radar
{
    [Serializable]
    public sealed class RadarScreenDefinition
    {
        [SerializeField] private string screenId = "main";
        [SerializeField] private string displayName = "Main";
        [SerializeField] private int defaultWidthPixels = 1920;
        [SerializeField] private int defaultHeightPixels = 1080;
        [SerializeField] private bool enabled = true;
        [SerializeField] private bool isPrimary = true;
        [SerializeField] private int order;

        public RadarScreenDefinition()
        {
        }

        public RadarScreenDefinition(
            string screenId,
            string displayName,
            int defaultWidthPixels,
            int defaultHeightPixels,
            bool enabled,
            bool isPrimary,
            int order)
        {
            this.screenId = screenId;
            this.displayName = displayName;
            this.defaultWidthPixels = defaultWidthPixels;
            this.defaultHeightPixels = defaultHeightPixels;
            this.enabled = enabled;
            this.isPrimary = isPrimary;
            this.order = order;
        }

        public string ScreenId => screenId;
        public string DisplayName => displayName;
        public int DefaultWidthPixels => Mathf.Max(1, defaultWidthPixels);
        public int DefaultHeightPixels => Mathf.Max(1, defaultHeightPixels);
        public bool Enabled => enabled;
        public bool IsPrimary => isPrimary;
        public int Order => order;

        internal int AuthoredDefaultWidthPixels => defaultWidthPixels;
        internal int AuthoredDefaultHeightPixels => defaultHeightPixels;
    }
}
