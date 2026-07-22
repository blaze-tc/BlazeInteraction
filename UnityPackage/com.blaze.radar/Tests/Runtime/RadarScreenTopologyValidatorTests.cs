using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blaze.Radar.Tests
{
    public sealed class RadarScreenTopologyValidatorTests
    {
        [Test]
        public void Validate_AcceptsOrderedFourScreenTopology()
        {
            var screens = new List<RadarScreenDefinition>
            {
                Screen("left", "Left", 1024, 768, true, false, 0),
                Screen("front", "Front", 2560, 960, true, true, 1),
                Screen("right", "Right", 1024, 768, true, false, 2),
                Screen("floor", "Floor", 1920, 1080, true, false, 3)
            };

            var result = RadarScreenTopologyValidator.Validate(screens);

            Assert.That(result.IsValid, Is.True, string.Join("\n", result.Errors));
            Assert.That(screens[0].ScreenId, Is.EqualTo("left"));
            Assert.That(screens[1].Order, Is.EqualTo(1));
            Assert.That(screens[3].ScreenId, Is.EqualTo("floor"));
        }

        [TestCase("")]
        [TestCase("Front")]
        [TestCase("front wall")]
        public void Validate_RejectsInvalidEnabledIdWithIndexAndRule(string screenId)
        {
            var result = RadarScreenTopologyValidator.Validate(new[]
            {
                Screen(screenId, "Front", 1920, 1080, true, true, 0)
            });

            Assert.That(result.IsValid, Is.False);
            Assert.That(string.Join("\n", result.Errors), Does.Contain("screens[0]").And.Contain("^[a-z0-9_-]{1,64}$"));
        }

        [Test]
        public void Validate_RejectsCaseInsensitiveDuplicateEnabledIds()
        {
            var result = RadarScreenTopologyValidator.Validate(new[]
            {
                Screen("front", "Front", 1920, 1080, true, true, 0),
                Screen("FRONT", "Duplicate", 1920, 1080, true, false, 1)
            });

            Assert.That(result.IsValid, Is.False);
            Assert.That(string.Join("\n", result.Errors), Does.Contain("screens[1]").And.Contain("duplicate").IgnoreCase);
        }

        [TestCase(0, 1080, "width")]
        [TestCase(1920, 32769, "height")]
        public void Validate_RejectsRawAuthoredDimensions(int width, int height, string dimension)
        {
            var screen = Screen("front", "Front", width, height, true, true, 0);
            Assert.That(screen.DefaultWidthPixels, Is.GreaterThanOrEqualTo(1));
            Assert.That(screen.DefaultHeightPixels, Is.GreaterThanOrEqualTo(1));

            var result = RadarScreenTopologyValidator.Validate(new[] { screen });

            Assert.That(result.IsValid, Is.False);
            Assert.That(string.Join("\n", result.Errors), Does.Contain("screens[0]").And.Contain(dimension).IgnoreCase.And.Contain("1..32768"));
        }

        [Test]
        public void Validate_RejectsZeroEnabledPrimaries()
        {
            var result = RadarScreenTopologyValidator.Validate(new[]
            {
                Screen("front", "Front", 1920, 1080, true, false, 0)
            });

            Assert.That(result.IsValid, Is.False);
            Assert.That(string.Join("\n", result.Errors), Does.Contain("exactly one enabled screen").And.Contain("primary").IgnoreCase);
        }

        [Test]
        public void Validate_RejectsTwoEnabledPrimaries()
        {
            var result = RadarScreenTopologyValidator.Validate(new[]
            {
                Screen("front", "Front", 1920, 1080, true, true, 0),
                Screen("right", "Right", 1024, 768, true, true, 1)
            });

            Assert.That(result.IsValid, Is.False);
            Assert.That(string.Join("\n", result.Errors), Does.Contain("exactly one enabled screen").And.Contain("2"));
        }

        [Test]
        public void Validate_IgnoresDisabledDuplicateAndPrimaryParticipants()
        {
            var result = RadarScreenTopologyValidator.Validate(new[]
            {
                Screen("front", "Front", 1920, 1080, true, true, 0),
                Screen("FRONT", "Disabled duplicate", 0, 50000, false, true, 1),
                Screen("invalid id", "Disabled invalid", 0, 0, false, false, 2)
            });

            Assert.That(result.IsValid, Is.True, string.Join("\n", result.Errors));
        }

        [Test]
        public void Validate_RequiresCollectionAndAtLeastOneEnabledScreen()
        {
            var missing = RadarScreenTopologyValidator.Validate(null);
            var empty = RadarScreenTopologyValidator.Validate(new RadarScreenDefinition[0]);
            var disabled = RadarScreenTopologyValidator.Validate(new[]
            {
                Screen("front", "Front", 1920, 1080, false, false, 0)
            });

            Assert.That(missing.IsValid, Is.False);
            Assert.That(string.Join("\n", missing.Errors), Does.Contain("required").IgnoreCase);
            Assert.That(string.Join("\n", empty.Errors), Does.Contain("enabled screen").IgnoreCase);
            Assert.That(string.Join("\n", disabled.Errors), Does.Contain("enabled screen").IgnoreCase);
        }

        [Test]
        public void RuntimeSettings_CreatesBackwardCompatiblePrimaryDefault()
        {
            var settings = ScriptableObject.CreateInstance<RadarRuntimeSettings>();
            try
            {
                SetScreens(settings, null);

                Assert.That(settings.Screens, Has.Count.EqualTo(1));
                Assert.That(settings.Screens[0].ScreenId, Is.EqualTo("main"));
                Assert.That(settings.PrimaryScreen, Is.SameAs(settings.Screens[0]));
            }
            finally
            {
                Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void RuntimeSettings_PreservesAuthoredListOrder()
        {
            var settings = ScriptableObject.CreateInstance<RadarRuntimeSettings>();
            var screens = new List<RadarScreenDefinition>
            {
                Screen("right", "Right", 1024, 768, true, false, 2),
                Screen("front", "Front", 1920, 1080, true, true, 0),
                Screen("left", "Left", 1024, 768, true, false, 1)
            };

            try
            {
                SetScreens(settings, screens);

                Assert.That(settings.Screens[0].ScreenId, Is.EqualTo("right"));
                Assert.That(settings.Screens[1].ScreenId, Is.EqualTo("front"));
                Assert.That(settings.Screens[2].ScreenId, Is.EqualTo("left"));
                Assert.That(settings.PrimaryScreen.ScreenId, Is.EqualTo("front"));
            }
            finally
            {
                Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void RuntimeSettings_ScreensCannotBeDowncastOrClearedExternally()
        {
            var settings = ScriptableObject.CreateInstance<RadarRuntimeSettings>();
            try
            {
                var screens = settings.Screens;

                Assert.That(screens, Is.Not.InstanceOf<List<RadarScreenDefinition>>());
                var collection = screens as ICollection<RadarScreenDefinition>;
                Assert.That(collection, Is.Not.Null);
                Assert.Throws<NotSupportedException>(() => collection.Clear());
                Assert.That(settings.Screens, Has.Count.EqualTo(1));
            }
            finally
            {
                Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void RuntimeSettings_MigratedAuthoredEmptyTopologyRemainsInvalidAndEmpty()
        {
            var settings = ScriptableObject.CreateInstance<RadarRuntimeSettings>();
            try
            {
                SetTopologySchemaVersion(settings, 1);
                SetScreens(settings, new List<RadarScreenDefinition>());

                Assert.That(settings.Screens, Is.Empty);
                Assert.That(settings.Screens, Is.Empty);
                Assert.That(settings.PrimaryScreen, Is.Null);
                Assert.That(RadarScreenTopologyValidator.Validate(settings.Screens).IsValid, Is.False);
            }
            finally
            {
                Object.DestroyImmediate(settings);
            }
        }

        private static RadarScreenDefinition Screen(
            string id,
            string displayName,
            int width,
            int height,
            bool enabled,
            bool isPrimary,
            int order)
        {
            return new RadarScreenDefinition(id, displayName, width, height, enabled, isPrimary, order);
        }

        private static void SetScreens(RadarRuntimeSettings settings, List<RadarScreenDefinition> screens)
        {
            var field = typeof(RadarRuntimeSettings).GetField("screens", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            field.SetValue(settings, screens);
        }

        private static void SetTopologySchemaVersion(RadarRuntimeSettings settings, int version)
        {
            var field = typeof(RadarRuntimeSettings).GetField(
                "screenTopologySchemaVersion",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            field.SetValue(settings, version);
        }
    }
}
