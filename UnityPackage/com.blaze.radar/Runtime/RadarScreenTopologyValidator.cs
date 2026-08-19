using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Blaze.Radar
{
    public sealed class RadarScreenTopologyValidationResult
    {
        internal RadarScreenTopologyValidationResult(List<string> errors)
        {
            Errors = errors.AsReadOnly();
        }

        public bool IsValid => Errors.Count == 0;
        public IReadOnlyList<string> Errors { get; }
    }

    public static class RadarScreenTopologyValidator
    {
        private const int MaximumPixels = 32768;
        private static readonly Regex ScreenIdPattern = new Regex(
            "^[a-z0-9_-]{1,64}$",
            RegexOptions.CultureInvariant);

        public static RadarScreenTopologyValidationResult Validate(
            IReadOnlyList<RadarScreenDefinition> screens)
        {
            var errors = new List<string>();
            if (screens == null)
            {
                errors.Add("Screen collection is required.");
                return new RadarScreenTopologyValidationResult(errors);
            }

            var enabledCount = 0;
            var enabledPrimaryCount = 0;
            var enabledIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < screens.Count; index++)
            {
                var screen = screens[index];
                if (screen == null)
                {
                    errors.Add("screens[" + index + "] is required.");
                    continue;
                }

                if (!screen.Enabled)
                {
                    continue;
                }

                enabledCount++;
                if (screen.IsPrimary)
                {
                    enabledPrimaryCount++;
                }

                var id = screen.ScreenId ?? string.Empty;
                var context = "screens[" + index + "](id='" +
                              (string.IsNullOrEmpty(id) ? "<missing>" : id) + "')";
                if (!ScreenIdPattern.IsMatch(id))
                {
                    errors.Add(context + ".screenId must match ^[a-z0-9_-]{1,64}$.");
                }

                if (!enabledIds.Add(id))
                {
                    errors.Add(context + ".screenId is a duplicate enabled screen ID (case-insensitive).");
                }

                if (screen.AuthoredDefaultWidthPixels < 1 ||
                    screen.AuthoredDefaultWidthPixels > MaximumPixels)
                {
                    errors.Add(context + ".defaultWidthPixels must be in 1..32768.");
                }

                if (screen.AuthoredDefaultHeightPixels < 1 ||
                    screen.AuthoredDefaultHeightPixels > MaximumPixels)
                {
                    errors.Add(context + ".defaultHeightPixels must be in 1..32768.");
                }
            }

            if (enabledCount == 0)
            {
                errors.Add("At least one enabled screen is required.");
            }

            if (enabledPrimaryCount != 1)
            {
                errors.Add(
                    "Exactly one enabled screen must be primary; found " + enabledPrimaryCount + ".");
            }

            return new RadarScreenTopologyValidationResult(errors);
        }
    }
}
