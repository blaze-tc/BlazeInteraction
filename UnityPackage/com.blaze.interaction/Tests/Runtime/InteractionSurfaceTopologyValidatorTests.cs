using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blaze.Interaction.Tests
{
    public sealed class InteractionSurfaceTopologyValidatorTests
    {
        [Test]
        public void Validate_AcceptsOrderedFourSurfaceTopology()
        {
            var surfaces = new List<InteractionSurfaceDefinition>
            {
                Surface("left", "Left", 1024, 768, true, false, 0),
                Surface("front", "Front", 2560, 960, true, true, 1),
                Surface("right", "Right", 1024, 768, true, false, 2),
                Surface("floor", "Floor", 1920, 1080, true, false, 3)
            };

            var result = InteractionSurfaceTopologyValidator.Validate(surfaces);

            Assert.That(result.IsValid, Is.True, string.Join("\n", result.Errors));
            Assert.That(surfaces[0].SurfaceId, Is.EqualTo("left"));
            Assert.That(surfaces[3].Order, Is.EqualTo(3));
        }

        [TestCase("")]
        [TestCase("Front")]
        [TestCase("front wall")]
        public void Validate_RejectsInvalidEnabledId(string surfaceId)
        {
            var result = InteractionSurfaceTopologyValidator.Validate(new[]
            {
                Surface(surfaceId, "Front", 1920, 1080, true, true, 0)
            });

            Assert.That(result.IsValid, Is.False);
            Assert.That(string.Join("\n", result.Errors),
                Does.Contain("surfaces[0]").And.Contain("^[a-z0-9_-]{1,64}$"));
        }

        [Test]
        public void Validate_RejectsCaseInsensitiveDuplicateEnabledIds()
        {
            var result = InteractionSurfaceTopologyValidator.Validate(new[]
            {
                Surface("front", "Front", 1920, 1080, true, true, 0),
                Surface("FRONT", "Duplicate", 1920, 1080, true, false, 1)
            });

            Assert.That(result.IsValid, Is.False);
            Assert.That(string.Join("\n", result.Errors),
                Does.Contain("surfaces[1]").And.Contain("duplicate").IgnoreCase);
        }

        [TestCase(0, 1080, "width")]
        [TestCase(1920, 32769, "height")]
        public void Validate_RejectsRawAuthoredDimensions(int width, int height, string dimension)
        {
            var surface = Surface("front", "Front", width, height, true, true, 0);
            Assert.That(surface.LogicalWidth, Is.GreaterThanOrEqualTo(1));
            Assert.That(surface.LogicalHeight, Is.GreaterThanOrEqualTo(1));

            var result = InteractionSurfaceTopologyValidator.Validate(new[] { surface });

            Assert.That(result.IsValid, Is.False);
            Assert.That(string.Join("\n", result.Errors),
                Does.Contain("surfaces[0]").And.Contain(dimension).IgnoreCase.And.Contain("1..32768"));
        }

        [Test]
        public void Validate_RequiresOneEnabledPrimaryAndIgnoresDisabledDuplicates()
        {
            var valid = InteractionSurfaceTopologyValidator.Validate(new[]
            {
                Surface("front", "Front", 1920, 1080, true, true, 0),
                Surface("FRONT", "Disabled", 0, 50000, false, true, 1)
            });
            var missingPrimary = InteractionSurfaceTopologyValidator.Validate(new[]
            {
                Surface("front", "Front", 1920, 1080, true, false, 0)
            });
            var twoPrimaries = InteractionSurfaceTopologyValidator.Validate(new[]
            {
                Surface("front", "Front", 1920, 1080, true, true, 0),
                Surface("right", "Right", 1024, 768, true, true, 1)
            });

            Assert.That(valid.IsValid, Is.True, string.Join("\n", valid.Errors));
            Assert.That(string.Join("\n", missingPrimary.Errors), Does.Contain("primary").IgnoreCase);
            Assert.That(string.Join("\n", twoPrimaries.Errors), Does.Contain("found 2"));
        }

        [Test]
        public void RuntimeSettings_CreatesProviderNeutralPrimaryDefault()
        {
            var settings = ScriptableObject.CreateInstance<InteractionRuntimeSettings>();
            try
            {
                SetSurfaces(settings, null);

                Assert.That(settings.Surfaces, Has.Count.EqualTo(1));
                Assert.That(settings.Surfaces[0].SurfaceId, Is.EqualTo("main"));
                Assert.That(settings.PrimarySurface, Is.SameAs(settings.Surfaces[0]));
                Assert.That(settings.PipeName, Is.EqualTo(InteractionIpcProtocol.PipeName));
            }
            finally
            {
                Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void RuntimeSettings_PreservesAuthoredOrderAndReadOnlyView()
        {
            var settings = ScriptableObject.CreateInstance<InteractionRuntimeSettings>();
            var surfaces = new List<InteractionSurfaceDefinition>
            {
                Surface("right", "Right", 1024, 768, true, false, 2),
                Surface("front", "Front", 1920, 1080, true, true, 0),
                Surface("left", "Left", 1024, 768, true, false, 1)
            };

            try
            {
                SetSurfaces(settings, surfaces);

                Assert.That(settings.Surfaces[0].SurfaceId, Is.EqualTo("right"));
                Assert.That(settings.PrimarySurface.SurfaceId, Is.EqualTo("front"));
                Assert.That(settings.Surfaces, Is.Not.InstanceOf<List<InteractionSurfaceDefinition>>());
                var collection = settings.Surfaces as ICollection<InteractionSurfaceDefinition>;
                Assert.That(collection, Is.Not.Null);
                Assert.Throws<NotSupportedException>(() => collection.Clear());
            }
            finally
            {
                Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void RuntimeSettings_AuthoredEmptyMigratedTopologyRemainsInvalid()
        {
            var settings = ScriptableObject.CreateInstance<InteractionRuntimeSettings>();
            try
            {
                SetTopologySchemaVersion(settings, 1);
                SetSurfaces(settings, new List<InteractionSurfaceDefinition>());

                Assert.That(settings.Surfaces, Is.Empty);
                Assert.That(settings.PrimarySurface, Is.Null);
                Assert.That(InteractionSurfaceTopologyValidator.Validate(settings.Surfaces).IsValid, Is.False);
            }
            finally
            {
                Object.DestroyImmediate(settings);
            }
        }

        private static InteractionSurfaceDefinition Surface(
            string id,
            string displayName,
            int width,
            int height,
            bool enabled,
            bool isPrimary,
            int order)
        {
            return new InteractionSurfaceDefinition(
                id,
                displayName,
                width,
                height,
                enabled,
                isPrimary,
                order);
        }

        private static void SetSurfaces(
            InteractionRuntimeSettings settings,
            List<InteractionSurfaceDefinition> surfaces)
        {
            var field = typeof(InteractionRuntimeSettings).GetField(
                "surfaces",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            field.SetValue(settings, surfaces);
        }

        private static void SetTopologySchemaVersion(InteractionRuntimeSettings settings, int version)
        {
            var field = typeof(InteractionRuntimeSettings).GetField(
                "surfaceTopologySchemaVersion",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            field.SetValue(settings, version);
        }
    }
}
