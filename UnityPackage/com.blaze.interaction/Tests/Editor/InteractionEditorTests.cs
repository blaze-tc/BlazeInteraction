using System;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.PackageManager;
using UnityEngine;

namespace Blaze.Interaction.Editor.Tests
{
    public sealed class InteractionEditorTests
    {
        [Test]
        public void BasicInteractionSample_CreatesAndDrivesAProviderNeutralCursor()
        {
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(
                typeof(InteractionManager).Assembly);
            Assert.That(package, Is.Not.Null);
            var presenterPath = Path.Combine(
                package.resolvedPath,
                "Samples~",
                "BasicInteraction",
                "BasicInteractionPresenter.cs");

            var source = File.ReadAllText(presenterPath);

            StringAssert.Contains("EnsureCursor", source);
            StringAssert.Contains("UpdateCursor", source);
            StringAssert.Contains("InteractionManager.Instance.Points", source);
            StringAssert.Contains("PointUpdated += OnPointUpdated", source);
            StringAssert.Contains("EmitFootprintParticles(point)", source);
            StringAssert.Contains("footprintLifetimeSeconds = 0.5f", source);
            StringAssert.Contains("AcquireFootprintParticle", source);
            StringAssert.Contains("ReleaseFootprintParticle", source);
            StringAssert.Contains("surface.LogicalWidth", source);
            StringAssert.Contains("surface.LogicalHeight", source);
            StringAssert.DoesNotContain("Destroy(", source);
            StringAssert.DoesNotContain("CameraVision", source);
            StringAssert.DoesNotContain("NamedPipe", source);
        }

        [Test]
        public void SurfaceEditorOperations_AddDuplicateMoveAndPrimaryPreserveValidOrder()
        {
            var settings = ScriptableObject.CreateInstance<InteractionRuntimeSettings>();
            try
            {
                _ = settings.Surfaces;
                var serialized = new SerializedObject(settings);
                var surfaces = serialized.FindProperty("surfaces");

                var added = InteractionSurfaceSerializedEditorOperations.AddSurface(surfaces);
                surfaces.GetArrayElementAtIndex(added).FindPropertyRelative("surfaceId").stringValue = "MAIN-COPY";
                var duplicate = InteractionSurfaceSerializedEditorOperations.DuplicateSurface(surfaces, 0);
                Assert.That(InteractionSurfaceSerializedEditorOperations.MoveSurface(surfaces, duplicate, 1), Is.True);
                Assert.That(InteractionSurfaceSerializedEditorOperations.SetPrimary(surfaces, 1), Is.True);
                serialized.ApplyModifiedPropertiesWithoutUndo();

                Assert.That(settings.Surfaces, Has.Count.EqualTo(3));
                Assert.That(settings.Surfaces[1].SurfaceId, Is.EqualTo("main-copy-2"));
                Assert.That(settings.Surfaces[0].Order, Is.EqualTo(0));
                Assert.That(settings.Surfaces[1].Order, Is.EqualTo(1));
                Assert.That(settings.Surfaces[2].Order, Is.EqualTo(2));
                Assert.That(settings.PrimarySurface, Is.SameAs(settings.Surfaces[1]));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void PreprocessBuild_InvalidSettingsAssetThrowsBeforeCopy()
        {
            const string resourcesFolder = "Assets/Resources";
            const string settingsPath = resourcesFolder + "/InteractionRuntimeSettings.asset";
            const string backupPath = "Assets/InteractionRuntimeSettings.Task8Backup.asset";
            var createdResourcesFolder = !AssetDatabase.IsValidFolder(resourcesFolder);
            var existing = AssetDatabase.LoadAssetAtPath<InteractionRuntimeSettings>(settingsPath);
            var movedExisting = false;
            var createdTestAsset = false;
            if (existing != null && EditorUtility.IsDirty(existing))
            {
                Assert.Ignore("Pre-build validation will not move a dirty InteractionRuntimeSettings asset.");
            }

            if (createdResourcesFolder)
            {
                AssetDatabase.CreateFolder("Assets", "Resources");
            }

            try
            {
                if (existing != null)
                {
                    Assert.That(AssetDatabase.LoadMainAssetAtPath(backupPath), Is.Null);
                    Assert.That(AssetDatabase.MoveAsset(settingsPath, backupPath), Is.Empty);
                    movedExisting = true;
                }

                var settings = ScriptableObject.CreateInstance<InteractionRuntimeSettings>();
                _ = settings.Surfaces;
                var serialized = new SerializedObject(settings);
                serialized.FindProperty("surfaces").arraySize = 0;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                AssetDatabase.CreateAsset(settings, settingsPath);
                createdTestAsset = true;
                AssetDatabase.SaveAssetIfDirty(settings);

                Assert.Throws<BuildFailedException>(() =>
                    new InteractionBuildProcessor().OnPreprocessBuild(null));
            }
            finally
            {
                if (createdTestAsset)
                {
                    AssetDatabase.DeleteAsset(settingsPath);
                }

                if (movedExisting)
                {
                    Assert.That(AssetDatabase.MoveAsset(backupPath, settingsPath), Is.Empty);
                }

                if (createdResourcesFolder && AssetDatabase.IsValidFolder(resourcesFolder))
                {
                    AssetDatabase.DeleteAsset(resourcesFolder);
                }
            }
        }

        [Test]
        public void BuildCopy_DeletesStaleFilesAndVerifiesVersionManifestAndSha256()
        {
            var root = Path.Combine(Path.GetTempPath(), "BlazeInteractionBuildTests", Guid.NewGuid().ToString("N"));
            var source = Path.Combine(root, "source");
            var destination = Path.Combine(root, "player", "BlazeInteractionBridge");
            Directory.CreateDirectory(Path.Combine(source, "Providers", "Radar"));
            Directory.CreateDirectory(destination);
            File.WriteAllText(Path.Combine(destination, "stale.txt"), "stale");
            WritePayload(source, "1.0.0", "1.0.0");
            try
            {
                var sourceHash = InteractionBuildProcessor.CopyValidatedPayloadForTests(
                    source,
                    destination,
                    "1.0.0");

                Assert.That(File.Exists(Path.Combine(destination, "stale.txt")), Is.False);
                Assert.That(File.ReadAllText(Path.Combine(destination, "bridge-version.txt")).Trim(), Is.EqualTo("1.0.0"));
                Assert.That(File.Exists(Path.Combine(destination, "Providers", "Radar", "provider.json")), Is.True);
                Assert.That(File.Exists(Path.Combine(destination, "Providers", "CameraVision", "provider.json")), Is.True);
                Assert.That(InteractionBuildProcessor.ComputeSha256ForTests(
                    Path.Combine(destination, "BlazeInteractionBridge.exe")), Is.EqualTo(sourceHash));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [TestCase("0.9.0", "1.0.0")]
        [TestCase("1.0.0", "0.9.0")]
        public void PayloadValidation_RejectsBridgeOrProviderVersionMismatch(
            string bridgeVersion,
            string providerVersion)
        {
            var root = Path.Combine(Path.GetTempPath(), "BlazeInteractionPayloadTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "Providers", "Radar"));
            WritePayload(root, bridgeVersion, providerVersion);
            try
            {
                var error = InteractionBridgePayloadValidator.Validate(root, "1.0.0");
                Assert.That(error, Is.Not.Null.And.Not.Empty);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        private static void WritePayload(string root, string bridgeVersion, string providerVersion)
        {
            File.WriteAllBytes(Path.Combine(root, "BlazeInteractionBridge.exe"), new byte[] { 1, 2, 3, 4 });
            File.WriteAllText(Path.Combine(root, "BlazeInteractionBridge.dll"), "host");
            File.WriteAllText(Path.Combine(root, "BlazeInteractionBridge.deps.json"), "{}");
            File.WriteAllText(Path.Combine(root, "BlazeInteractionBridge.runtimeconfig.json"),
                "{\"runtimeOptions\":{\"includedFrameworks\":[{\"name\":\"Microsoft.NETCore.App\"},{\"name\":\"Microsoft.WindowsDesktop.App\"}]}}");
            File.WriteAllText(Path.Combine(root, "hostfxr.dll"), "hostfxr");
            File.WriteAllText(Path.Combine(root, "hostpolicy.dll"), "hostpolicy");
            File.WriteAllText(Path.Combine(root, "bridge-version.txt"), bridgeVersion);
            var radar = Path.Combine(root, "Providers", "Radar");
            Directory.CreateDirectory(radar);
            File.WriteAllText(Path.Combine(radar, "Blaze.Provider.Radar.dll"), "provider");
            File.WriteAllText(Path.Combine(radar, "provider.json"),
                "{\"id\":\"blaze.radar.f10f20\",\"version\":\"" + providerVersion +
                "\",\"providerApiVersion\":1,\"entryAssembly\":\"Blaze.Provider.Radar.dll\",\"entryType\":\"Blaze.Provider.Radar.RadarPlugin\"}");
            var camera = Path.Combine(root, "Providers", "CameraVision");
            Directory.CreateDirectory(Path.Combine(camera, "runtimes", "win-x64", "native"));
            File.WriteAllText(Path.Combine(camera, "Blaze.Provider.CameraVision.dll"), "provider");
            File.WriteAllText(
                Path.Combine(camera, "runtimes", "win-x64", "native", "OpenCvSharpExtern.dll"),
                "native");
            File.WriteAllText(Path.Combine(camera, "provider.json"),
                "{\"id\":\"blaze.camera.vision\",\"version\":\"" + providerVersion +
                "\",\"providerApiVersion\":1,\"entryAssembly\":\"Blaze.Provider.CameraVision.dll\",\"entryType\":\"Blaze.Provider.CameraVision.CameraVisionPlugin\"}");
        }
    }
}
