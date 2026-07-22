using NUnit.Framework;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace Blaze.Radar.Editor.Tests
{
    public sealed class RadarSettingsProviderEditorTests
    {
        [Test]
        public void AddScreen_UsesSafeDefaultsAndSynchronizesOrder()
        {
            WithSettings((settings, serialized, screens) =>
            {
                var addedIndex = RadarScreenSerializedEditorOperations.AddScreen(screens);
                serialized.ApplyModifiedPropertiesWithoutUndo();

                Assert.That(addedIndex, Is.EqualTo(1));
                Assert.That(settings.Screens, Has.Count.EqualTo(2));
                Assert.That(settings.Screens[1].ScreenId, Is.EqualTo("screen-copy"));
                Assert.That(settings.Screens[1].DefaultWidthPixels, Is.EqualTo(1920));
                Assert.That(settings.Screens[1].DefaultHeightPixels, Is.EqualTo(1080));
                Assert.That(settings.Screens[0].Order, Is.EqualTo(0));
                Assert.That(settings.Screens[1].Order, Is.EqualTo(1));
            });
        }

        [Test]
        public void DuplicateScreen_GeneratesCaseInsensitiveCollisionFreeId()
        {
            WithSettings((settings, serialized, screens) =>
            {
                var existingCopy = RadarScreenSerializedEditorOperations.AddScreen(screens);
                screens.GetArrayElementAtIndex(existingCopy)
                    .FindPropertyRelative("screenId").stringValue = "MAIN-COPY";

                var duplicate = RadarScreenSerializedEditorOperations.DuplicateScreen(screens, 0);
                serialized.ApplyModifiedPropertiesWithoutUndo();

                Assert.That(duplicate, Is.EqualTo(2));
                Assert.That(settings.Screens[2].ScreenId, Is.EqualTo("main-copy-2"));
                Assert.That(settings.Screens[2].IsPrimary, Is.False);
                Assert.That(settings.Screens[2].Order, Is.EqualTo(2));
            });
        }

        [Test]
        public void RemoveLastScreen_PreservesAuthoredEmptyTopologyForValidation()
        {
            WithSettings((settings, serialized, screens) =>
            {
                RadarScreenSerializedEditorOperations.RemoveScreen(screens, 0);
                serialized.ApplyModifiedPropertiesWithoutUndo();

                Assert.That(settings.Screens, Is.Empty);
                Assert.That(RadarScreenTopologyValidator.Validate(settings.Screens).IsValid, Is.False);
            });
        }

        [Test]
        public void MoveScreen_SynchronizesOrderToSerializedListOrder()
        {
            WithSettings((settings, serialized, screens) =>
            {
                RadarScreenSerializedEditorOperations.AddScreen(screens);
                RadarScreenSerializedEditorOperations.AddScreen(screens);
                var originalFirstId = screens.GetArrayElementAtIndex(0)
                    .FindPropertyRelative("screenId").stringValue;

                RadarScreenSerializedEditorOperations.MoveScreen(screens, 0, 2);
                serialized.ApplyModifiedPropertiesWithoutUndo();

                Assert.That(settings.Screens[2].ScreenId, Is.EqualTo(originalFirstId));
                Assert.That(settings.Screens[0].Order, Is.EqualTo(0));
                Assert.That(settings.Screens[1].Order, Is.EqualTo(1));
                Assert.That(settings.Screens[2].Order, Is.EqualTo(2));
            });
        }

        [Test]
        public void SetPrimary_ClearsEveryOtherPrimaryAtomically()
        {
            WithSettings((settings, serialized, screens) =>
            {
                var added = RadarScreenSerializedEditorOperations.AddScreen(screens);

                Assert.That(RadarScreenSerializedEditorOperations.SetPrimary(screens, added), Is.True);
                serialized.ApplyModifiedPropertiesWithoutUndo();

                Assert.That(settings.Screens[0].IsPrimary, Is.False);
                Assert.That(settings.Screens[added].IsPrimary, Is.True);
                Assert.That(settings.PrimaryScreen, Is.SameAs(settings.Screens[added]));
            });
        }

        [Test]
        public void PreprocessBuild_InvalidSettingsAssetThrowsBeforePostBuildWork()
        {
            const string resourcesFolder = "Assets/Resources";
            const string settingsPath = resourcesFolder + "/RadarRuntimeSettings.asset";
            const string backupPath = "Assets/RadarRuntimeSettings.Task8Backup.asset";
            var createdResourcesFolder = !AssetDatabase.IsValidFolder(resourcesFolder);
            var movedExistingAsset = false;
            var createdTestAsset = false;
            RadarRuntimeSettings testSettings = null;

            if (createdResourcesFolder)
            {
                AssetDatabase.CreateFolder("Assets", "Resources");
            }

            try
            {
                var existingSettings = AssetDatabase.LoadAssetAtPath<RadarRuntimeSettings>(settingsPath);
                if (existingSettings != null)
                {
                    Assert.That(AssetDatabase.LoadMainAssetAtPath(backupPath), Is.Null,
                        "Safe test backup path is already occupied: " + backupPath);
                    Assert.That(AssetDatabase.MoveAsset(settingsPath, backupPath), Is.Empty);
                    movedExistingAsset = true;
                }

                testSettings = ScriptableObject.CreateInstance<RadarRuntimeSettings>();
                AssetDatabase.CreateAsset(testSettings, settingsPath);
                createdTestAsset = true;
                _ = testSettings.Screens;
                var serialized = new SerializedObject(testSettings);
                serialized.FindProperty("screens").arraySize = 0;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(testSettings);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                Assert.Throws<BuildFailedException>(() =>
                    new RadarBuildProcessor().OnPreprocessBuild(null));
            }
            finally
            {
                if (createdTestAsset && AssetDatabase.LoadMainAssetAtPath(settingsPath) != null)
                {
                    AssetDatabase.DeleteAsset(settingsPath);
                }

                if (movedExistingAsset)
                {
                    Assert.That(AssetDatabase.MoveAsset(backupPath, settingsPath), Is.Empty);
                }

                if (createdResourcesFolder && AssetDatabase.IsValidFolder(resourcesFolder))
                {
                    AssetDatabase.DeleteAsset(resourcesFolder);
                }

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }
        }

        private static void WithSettings(
            System.Action<RadarRuntimeSettings, SerializedObject, SerializedProperty> action)
        {
            var settings = ScriptableObject.CreateInstance<RadarRuntimeSettings>();
            try
            {
                _ = settings.Screens;
                var serialized = new SerializedObject(settings);
                serialized.Update();
                action(settings, serialized, serialized.FindProperty("screens"));
            }
            finally
            {
                Object.DestroyImmediate(settings);
            }
        }
    }
}
