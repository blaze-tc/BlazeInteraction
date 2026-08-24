using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Blaze.Interaction.Internal;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Blaze.Interaction.Tests
{
    public sealed class InteractionBridgeLauncherTests
    {
        [UnityTest]
        public IEnumerator ExistingBridge_IsReusedWithoutStartingAnotherProcess()
        {
            var task = VerifyExistingBridgeIsReusedAsync();
            while (!task.IsCompleted)
            {
                yield return null;
            }

            if (task.IsFaulted)
            {
                throw task.Exception.InnerException;
            }
        }

        [UnityTest]
        public IEnumerator MissingBridge_StartsOnlyBlazeInteractionBridgeWithUnityParentPid()
        {
            var task = VerifyMissingBridgeStartsExpectedProcessAsync();
            while (!task.IsCompleted)
            {
                yield return null;
            }

            if (task.IsFaulted)
            {
                throw task.Exception.InnerException;
            }
        }

        [UnityTest]
        public IEnumerator ScopedBridgeArguments_CannotInjectAdditionalOptions()
        {
            var task = VerifyScopedBridgeArgumentsCannotInjectAdditionalOptionsAsync();
            while (!task.IsCompleted)
            {
                yield return null;
            }

            if (task.IsFaulted)
            {
                throw task.Exception.InnerException;
            }
        }

        [UnityTest]
        public IEnumerator EnsureBridgeRunning_ConfiguresScopedManagerBeforeProbeCompletes()
        {
            var task = VerifyScopedManagerIsConfiguredBeforeProbeCompletesAsync();
            while (!task.IsCompleted)
            {
                yield return null;
            }

            if (task.IsFaulted)
            {
                throw task.Exception.InnerException;
            }
        }

        [Test]
        public void HelloPayload_UsesConfiguredSurfaceTopologyAndSdkVersion()
        {
            var settings = Settings();
            SetField(settings, "surfaceTopologySchemaVersion", 1);
            SetField(settings, "surfaces", new List<InteractionSurfaceDefinition>
            {
                new InteractionSurfaceDefinition("front", "Front", 1920, 1080, true, true, 0),
                new InteractionSurfaceDefinition("left", "Left", 1024, 768, true, false, 1)
            });
            var gameObject = new GameObject("InteractionBridgeLauncherHelloTests");
            gameObject.SetActive(false);
            var launcher = gameObject.AddComponent<InteractionBridgeLauncher>();
            try
            {
                launcher.ConfigureForTests(
                    settings,
                    new ConstantProbe(true),
                    new RecordingProcessStarter(),
                    "missing.exe");

                var hello = launcher.CreateHelloPayloadForTests();

                Assert.That(hello.UnityPid, Is.EqualTo(Process.GetCurrentProcess().Id));
                Assert.That(hello.SdkVersion, Is.EqualTo("1.0.0"));
                Assert.That(hello.UnityVersion, Is.EqualTo(Application.unityVersion));
                Assert.That(hello.Surfaces, Has.Count.EqualTo(2));
                Assert.That(hello.Surfaces[0].SurfaceId, Is.EqualTo("front"));
                Assert.That(hello.Surfaces[1].LogicalWidth, Is.EqualTo(1024));
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
                Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void UnityLifecycleMethods_AreInheritedByRadarCompatibilityLauncher()
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;

            Assert.That(typeof(InteractionBridgeLauncher).GetMethod("Awake", flags).IsFamily, Is.True);
            Assert.That(typeof(InteractionBridgeLauncher).GetMethod("Update", flags).IsFamily, Is.True);
            Assert.That(typeof(InteractionBridgeLauncher).GetMethod("OnApplicationQuit", flags).IsFamily, Is.True);
        }

        [Test]
        public void QuoteArgument_RoundTripsWindowsArgumentsWithQuotesAndBackslashes()
        {
            AssertArgumentRoundTrips(@"C:\folder with spaces\");
            AssertArgumentRoundTrips("alpha\"beta");
            AssertArgumentRoundTrips("alpha\\\"beta");
            AssertArgumentRoundTrips("alpha\\\\\\\"beta");
            AssertArgumentRoundTrips(@"C:\folder with spaces\\");
        }

        [Test]
        public void ConfigureShared_ReplacesOnlyAnUnconnectedManager()
        {
            var first = InteractionManager.ConfigureShared("Blaze.InteractionBridge.First", 50, 25, 250);
            var replacement = InteractionManager.ConfigureShared("Blaze.InteractionBridge.Replacement", 50, 25, 250);
            var dispatcherField = typeof(InteractionManager).GetField(
                "_dispatcher",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var dispatcher = (InteractionFrameDispatcher)dispatcherField.GetValue(replacement);
            try
            {
                Assert.That(replacement, Is.Not.SameAs(first));
                Assert.That(InteractionManager.Instance, Is.SameAs(replacement));

                dispatcher.SetConnectionState(true);

                Assert.That(
                    () => InteractionManager.ConfigureShared("Blaze.InteractionBridge.AfterConnect", 50, 25, 250),
                    Throws.InvalidOperationException);
            }
            finally
            {
                dispatcher.SetConnectionState(false);
                InteractionManager.ConfigureShared("Blaze.InteractionBridge.Cleanup", 50, 25, 250);
            }
        }

        [Test]
        public void ConfigureShared_RejectsAcknowledgedClientBeforeDispatcherTick()
        {
            var manager = InteractionManager.ConfigureShared("Blaze.InteractionBridge.BeforeTick", 50, 25, 250);
            var dispatcher = GetPrivateField<InteractionFrameDispatcher>(manager, "_dispatcher");
            var client = GetPrivateField<InteractionPipeClient>(manager, "_client");
            var connectedField = typeof(InteractionPipeClient).GetField(
                "_connected",
                BindingFlags.Instance | BindingFlags.NonPublic);
            try
            {
                Assert.That(dispatcher.IsConnected, Is.False);
                connectedField.SetValue(client, 1);

                Assert.That(
                    () => InteractionManager.ConfigureShared("Blaze.InteractionBridge.AfterAck", 50, 25, 250),
                    Throws.InvalidOperationException);
            }
            finally
            {
                connectedField.SetValue(client, 0);
                InteractionManager.ConfigureShared("Blaze.InteractionBridge.Cleanup", 50, 25, 250);
            }
        }

        private static async Task VerifyExistingBridgeIsReusedAsync()
        {
            var settings = Settings();
            var gameObject = new GameObject("InteractionBridgeLauncherReuseTests");
            gameObject.SetActive(false);
            var launcher = gameObject.AddComponent<InteractionBridgeLauncher>();
            var starter = new RecordingProcessStarter();
            var probe = new ConstantProbe(true);
            try
            {
                launcher.ConfigureForTests(settings, probe, starter, "missing.exe");

                await launcher.EnsureBridgeRunningAsync(CancellationToken.None);

                Assert.That(launcher.ReusedExistingBridge, Is.True);
                Assert.That(starter.StartCount, Is.Zero);
                Assert.That(probe.LastPipeName, Is.EqualTo(CurrentScope(settings).PipeName));
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
                Object.DestroyImmediate(settings);
            }
        }

        private static async Task VerifyMissingBridgeStartsExpectedProcessAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), "BlazeInteractionLauncherTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var executable = Path.Combine(directory, "BlazeInteractionBridge.exe");
            File.WriteAllBytes(executable, new byte[] { 1 });
            var settings = Settings();
            SetField(settings, "profilePath", Path.Combine(directory, "profile with spaces.json"));
            var gameObject = new GameObject("InteractionBridgeLauncherStartTests");
            gameObject.SetActive(false);
            var launcher = gameObject.AddComponent<InteractionBridgeLauncher>();
            var starter = new RecordingProcessStarter();
            try
            {
                var probe = new ConstantProbe(false);
                launcher.ConfigureForTests(settings, probe, starter, executable);

                await launcher.EnsureBridgeRunningAsync(CancellationToken.None);

                var scope = CurrentScope(settings);

                Assert.That(launcher.ReusedExistingBridge, Is.False);
                Assert.That(starter.StartCount, Is.EqualTo(1));
                Assert.That(Path.GetFileName(starter.FileName), Is.EqualTo("BlazeInteractionBridge.exe"));
                Assert.That(starter.Arguments, Does.Contain("--parent-pid " + Process.GetCurrentProcess().Id));
                Assert.That(starter.Arguments, Does.Contain("--minimized"));
                Assert.That(probe.LastPipeName, Is.EqualTo(scope.PipeName));
                Assert.That(starter.Arguments, Does.Contain("--pipe-name \"" + scope.PipeName + "\""));
                Assert.That(starter.Arguments, Does.Contain("--data-root \"" + scope.DataRoot + "\""));
                Assert.That(starter.Arguments, Does.Contain("--profile \"" + scope.ProfilePath + "\""));
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
                Object.DestroyImmediate(settings);
                Directory.Delete(directory, true);
            }
        }

        private static async Task VerifyScopedBridgeArgumentsCannotInjectAdditionalOptionsAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), "BlazeInteractionLauncherTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var executable = Path.Combine(directory, "BlazeInteractionBridge.exe");
            File.WriteAllBytes(executable, new byte[] { 1 });
            var settings = Settings();
            SetField(settings, "pipeName", "Blaze\" --profile \"C:\\Injected\" --data-root \"C:\\Injected");
            SetField(settings, "profilePath", "");
            var gameObject = new GameObject("InteractionBridgeLauncherInjectionTests");
            gameObject.SetActive(false);
            var launcher = gameObject.AddComponent<InteractionBridgeLauncher>();
            var starter = new RecordingProcessStarter();
            var probe = new ConstantProbe(false);
            try
            {
                launcher.ConfigureForTests(settings, probe, starter, executable);

                await launcher.EnsureBridgeRunningAsync(CancellationToken.None);

                var scope = CurrentScope(settings);
                var arguments = ParseWindowsArguments(starter.Arguments);
                Assert.That(arguments, Has.Length.EqualTo(8));
                Assert.That(arguments[4], Is.EqualTo("--pipe-name"));
                Assert.That(arguments[5], Is.EqualTo(scope.PipeName));
                Assert.That(arguments[6], Is.EqualTo("--data-root"));
                Assert.That(arguments[7], Is.EqualTo(scope.DataRoot));
                Assert.That(arguments, Does.Not.Contain("--profile"));
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
                Object.DestroyImmediate(settings);
                Directory.Delete(directory, true);
            }
        }

        private static async Task VerifyScopedManagerIsConfiguredBeforeProbeCompletesAsync()
        {
            var previous = InteractionManager.ConfigureShared("Blaze.InteractionBridge.Previous", 50, 25, 250);
            var settings = Settings();
            SetField(settings, "autoStart", false);
            var gameObject = new GameObject("InteractionBridgeLauncherManagerIdentityTests");
            gameObject.SetActive(false);
            var launcher = gameObject.AddComponent<InteractionBridgeLauncher>();
            var probe = new DelayedProbe();
            try
            {
                launcher.ConfigureForTests(settings, probe, new RecordingProcessStarter(), "missing.exe");

                var task = launcher.EnsureBridgeRunningAsync(CancellationToken.None);
                var scopedManager = InteractionManager.Instance;

                Assert.That(task.IsCompleted, Is.False);
                Assert.That(scopedManager, Is.Not.SameAs(previous));
                Assert.That(GetSharedPipeName(scopedManager), Is.EqualTo(CurrentScope(settings).PipeName));

                probe.Complete(false);
                await task;

                Assert.That(InteractionManager.Instance, Is.SameAs(scopedManager));
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
                Object.DestroyImmediate(settings);
                InteractionManager.ConfigureShared("Blaze.InteractionBridge.Cleanup", 50, 25, 250);
            }
        }

        private static InteractionRuntimeSettings Settings()
        {
            var settings = ScriptableObject.CreateInstance<InteractionRuntimeSettings>();
            SetField(settings, "autoStart", true);
            SetField(settings, "pipeName", "Blaze.InteractionBridge.Tests");
            SetField(settings, "connectTimeoutMilliseconds", 50);
            return settings;
        }

        private static InteractionProjectScope CurrentScope(InteractionRuntimeSettings settings)
        {
            return InteractionProjectScopeResolver.Resolve(
                Application.isEditor,
                Application.dataPath,
                Application.persistentDataPath,
                settings.PipeName,
                settings.ProfilePath);
        }

        private static void AssertArgumentRoundTrips(string value)
        {
            var arguments = ParseWindowsArguments(InteractionBridgeLauncher.QuoteArgumentForTests(value));
            Assert.That(arguments, Has.Length.EqualTo(2));
            Assert.That(arguments[1], Is.EqualTo(value));
        }

        private static string[] ParseWindowsArguments(string arguments)
        {
            int count;
            var values = CommandLineToArgvW("BlazeInteractionBridge.exe " + arguments, out count);
            try
            {
                var parsed = new string[count];
                for (var index = 0; index < count; index++)
                {
                    var value = Marshal.ReadIntPtr(values, index * IntPtr.Size);
                    parsed[index] = Marshal.PtrToStringUni(value);
                }

                return parsed;
            }
            finally
            {
                LocalFree(values);
            }
        }

        private static T GetPrivateField<T>(object target, string name)
        {
            var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, name);
            return (T)field.GetValue(target);
        }

        private static string GetSharedPipeName(InteractionManager manager)
        {
            var client = GetPrivateField<InteractionPipeClient>(manager, "_client");
            var pipeName = client.GetType().GetField("_pipeName", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(pipeName, Is.Not.Null);
            return pipeName.GetValue(client).ToString();
        }

        private static void SetField(object target, string name, object value)
        {
            var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, name);
            field.SetValue(target, value);
        }

        private sealed class ConstantProbe : IInteractionBridgeProbe
        {
            private readonly bool result;

            public ConstantProbe(bool result)
            {
                this.result = result;
            }

            public string LastPipeName { get; private set; }

            public Task<bool> CanConnectAsync(
                string pipeName,
                int timeoutMilliseconds,
                CancellationToken cancellationToken)
            {
                LastPipeName = pipeName;
                return Task.FromResult(result);
            }
        }

        private sealed class DelayedProbe : IInteractionBridgeProbe
        {
            private readonly TaskCompletionSource<bool> result = new TaskCompletionSource<bool>();

            public Task<bool> CanConnectAsync(
                string pipeName,
                int timeoutMilliseconds,
                CancellationToken cancellationToken)
            {
                return result.Task;
            }

            public void Complete(bool canConnect)
            {
                result.SetResult(canConnect);
            }
        }

        private sealed class RecordingProcessStarter : IInteractionProcessStarter
        {
            public int StartCount { get; private set; }
            public string FileName { get; private set; }
            public string Arguments { get; private set; }

            public IInteractionOwnedProcess Start(string fileName, string arguments, string workingDirectory)
            {
                StartCount++;
                FileName = fileName;
                Arguments = arguments;
                return new RecordingOwnedProcess();
            }
        }

        private sealed class RecordingOwnedProcess : IInteractionOwnedProcess
        {
            public bool HasExited { get { return false; } }
            public bool CloseMainWindow() { return true; }
            public void Dispose() { }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CommandLineToArgvW(string commandLine, out int argumentCount);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);
    }
}
