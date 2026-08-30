using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Blaze.Interaction
{
    public sealed class InteractionSurfaceTopologyValidationResult
    {
        internal InteractionSurfaceTopologyValidationResult(List<string> errors)
        {
            Errors = errors.AsReadOnly();
        }

        public bool IsValid { get { return Errors.Count == 0; } }
        public IReadOnlyList<string> Errors { get; private set; }
    }

    public static class InteractionSurfaceTopologyValidator
    {
        private const int MaximumPixels = 32768;
        private static readonly Regex SurfaceIdPattern = new Regex(
            "^[a-z0-9_-]{1,64}$",
            RegexOptions.CultureInvariant);

        public static InteractionSurfaceTopologyValidationResult Validate(
            IReadOnlyList<InteractionSurfaceDefinition> surfaces)
        {
            var errors = new List<string>();
            if (surfaces == null)
            {
                errors.Add("Surface collection is required.");
                return new InteractionSurfaceTopologyValidationResult(errors);
            }

            var enabledCount = 0;
            var enabledPrimaryCount = 0;
            var enabledIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < surfaces.Count; index++)
            {
                var surface = surfaces[index];
                if (surface == null)
                {
                    errors.Add("surfaces[" + index + "] is required.");
                    continue;
                }

                if (!surface.Enabled)
                {
                    continue;
                }

                enabledCount++;
                if (surface.IsPrimary)
                {
                    enabledPrimaryCount++;
                }

                var id = surface.SurfaceId ?? string.Empty;
                var context = "surfaces[" + index + "](id='" +
                              (string.IsNullOrEmpty(id) ? "<missing>" : id) + "')";
                if (!SurfaceIdPattern.IsMatch(id))
                {
                    errors.Add(context + ".surfaceId must match ^[a-z0-9_-]{1,64}$.");
                }

                if (!enabledIds.Add(id))
                {
                    errors.Add(context + ".surfaceId is a duplicate enabled surface ID (case-insensitive).");
                }

                if (surface.AuthoredLogicalWidth < 1 || surface.AuthoredLogicalWidth > MaximumPixels)
                {
                    errors.Add(context + ".logicalWidth must be in 1..32768.");
                }

                if (surface.AuthoredLogicalHeight < 1 || surface.AuthoredLogicalHeight > MaximumPixels)
                {
                    errors.Add(context + ".logicalHeight must be in 1..32768.");
                }
            }

            if (enabledCount == 0)
            {
                errors.Add("At least one enabled surface is required.");
            }

            if (enabledPrimaryCount != 1)
            {
                errors.Add("Exactly one enabled surface must be primary; found " + enabledPrimaryCount + ".");
            }

            return new InteractionSurfaceTopologyValidationResult(errors);
        }
    }
}
