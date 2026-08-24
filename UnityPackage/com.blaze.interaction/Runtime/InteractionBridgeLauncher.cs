using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Blaze.Interaction.Internal;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor.PackageManager;
#endif

namespace Blaze.Interaction
{
    internal interface IInteractionBridgeProbe
    {
        Task<bool> CanConnectAsync(
            string pipeName,
            int timeoutMilliseconds,
            CancellationToken cancellationToken);
    }

    internal interface IInteractionProcessStarter
    {
        IInteractionOwnedProcess Start(string fileName, string arguments, string workingDirectory);
    }

    internal interface IInteractionOwnedProcess : IDisposable
    {
        bool HasExited { get; }
        bool CloseMainWindow();
    }

    [DefaultExecutionOrder(-1100)]
    public class InteractionBridgeLauncher : MonoBehaviour
    {
        [SerializeField] private InteractionRuntimeSettings settings;
        [SerializeField] public bool AutoStart = true;
        [SerializeField] public bool ExitBridgeWithUnity = true;

        private IInteractionBridgeProbe probe = new InteractionBridgeProbe();
        private IInteractionProcessStarter processStarter = new InteractionProcessStarter();
        private IInteractionOwnedProcess ownedProcess;
        private CancellationTokenSource cancellation;
        private string executableOverride;
        private InteractionManager interactionManager;
        private InteractionProjectScope projectScope;
        private bool managerStarted;

        public bool ReusedExistingBridge { get; private set; }
        public string LastError { get; private set; } = string.Empty;

        protected virtual async void Awake()
        {
            try
            {
                if (settings == null)
                {
                    settings = InteractionRuntimeSettings.LoadOrCreateRuntimeDefaults();
                }

                AutoStart = settings.AutoStart;
                ExitBridgeWithUnity = settings.ExitBridgeWithUnity;
                cancellation = new CancellationTokenSource();
                await EnsureBridgeRunningAsync(cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                interactionManager = InteractionManager.ConfigureShared(
                    projectScope.PipeName,
                    settings.ConnectTimeoutMilliseconds,
                    settings.ReconnectDelayMilliseconds,
                    settings.ServerResponseTimeoutMilliseconds);
                interactionManager.Connect(CreateHelloPayload());
                managerStarted = true;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                LastError = "BlazeInteractionBridge startup failed: " + exception.Message;
                UnityEngine.Debug.LogError(LastError, this);
            }
        }

        public async Task EnsureBridgeRunningAsync(CancellationToken cancellationToken)
        {
            if (settings == null)
            {
                settings = InteractionRuntimeSettings.LoadOrCreateRuntimeDefaults();
            }

            projectScope = ResolveProjectScope();
            ReusedExistingBridge = false;
            if (await probe.CanConnectAsync(
                    projectScope.PipeName,
                    settings.ConnectTimeoutMilliseconds,
                    cancellationToken))
            {
                ReusedExistingBridge = true;
                return;
            }

            if (!AutoStart)
            {
                return;
            }

            var executable = ResolveBridgeExecutable();
            if (!File.Exists(executable))
            {
                LastError = "BlazeInteractionBridge.exe not found: " + executable;
                UnityEngine.Debug.LogError(LastError, this);
                return;
            }

            var arguments =
                "--parent-pid " + Process.GetCurrentProcess().Id +
                " --minimized --pipe-name " + QuoteArgument(projectScope.PipeName) +
                " --data-root " + QuoteArgument(projectScope.DataRoot);
            if (!string.IsNullOrWhiteSpace(projectScope.ProfilePath))
            {
                arguments += " --profile " + QuoteArgument(projectScope.ProfilePath);
            }

            ownedProcess = processStarter.Start(
                executable,
                arguments,
                Path.GetDirectoryName(executable));
            if (ownedProcess == null)
            {
                throw new InvalidOperationException("BlazeInteractionBridge process could not be created.");
            }
        }

        protected virtual void Update()
        {
            if (managerStarted && interactionManager != null)
            {
                interactionManager.Tick();
            }
        }

        internal HelloPayload CreateHelloPayloadForTests()
        {
            return CreateHelloPayload();
        }

        private HelloPayload CreateHelloPayload()
        {
            if (settings == null)
            {
                settings = InteractionRuntimeSettings.LoadOrCreateRuntimeDefaults();
            }

            var validation = InteractionSurfaceTopologyValidator.Validate(settings.Surfaces);
            if (!validation.IsValid)
            {
                throw new InvalidOperationException(
                    "Interaction surface topology is invalid: " + string.Join("; ", validation.Errors));
            }

            var enabled = new List<InteractionSurfaceDefinition>();
            for (var index = 0; index < settings.Surfaces.Count; index++)
            {
                var surface = settings.Surfaces[index];
                if (surface != null && surface.Enabled)
                {
                    enabled.Add(surface);
                }
            }

            enabled.Sort((left, right) =>
            {
                var order = left.Order.CompareTo(right.Order);
                return order != 0
                    ? order
                    : StringComparer.Ordinal.Compare(left.SurfaceId ?? string.Empty, right.SurfaceId ?? string.Empty);
            });

            var hello = new HelloPayload
            {
                UnityPid = Process.GetCurrentProcess().Id,
                UnityVersion = Application.unityVersion,
                SdkVersion = InteractionSdkVersion.Value
            };
            for (var index = 0; index < enabled.Count; index++)
            {
                hello.Surfaces.Add(enabled[index].ToPayload());
            }

            return hello;
        }

        internal void ConfigureForTests(
            InteractionRuntimeSettings testSettings,
            IInteractionBridgeProbe testProbe,
            IInteractionProcessStarter testProcessStarter,
            string testExecutable)
        {
            settings = testSettings ?? throw new ArgumentNullException(nameof(testSettings));
            probe = testProbe ?? throw new ArgumentNullException(nameof(testProbe));
            processStarter = testProcessStarter ?? throw new ArgumentNullException(nameof(testProcessStarter));
            executableOverride = testExecutable;
            AutoStart = settings.AutoStart;
            ExitBridgeWithUnity = settings.ExitBridgeWithUnity;
        }

        private InteractionProjectScope ResolveProjectScope()
        {
            return InteractionProjectScopeResolver.Resolve(
                Application.isEditor,
                Application.dataPath,
                Application.persistentDataPath,
                settings.PipeName,
                settings.ProfilePath);
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }

        private string ResolveBridgeExecutable()
        {
            if (!string.IsNullOrWhiteSpace(executableOverride))
            {
                return Path.GetFullPath(executableOverride);
            }

#if UNITY_EDITOR
            if (!string.IsNullOrWhiteSpace(settings.EditorBridgeExecutable))
            {
                return InteractionBridgePathResolver.ResolveEditorExecutable(
                    settings.EditorBridgeExecutable,
                    null);
            }

            var packageInfo = PackageInfo.FindForAssembly(typeof(InteractionBridgeLauncher).Assembly);
            if (packageInfo != null && !string.IsNullOrWhiteSpace(packageInfo.resolvedPath))
            {
                return InteractionBridgePathResolver.ResolveEditorExecutable(
                    string.Empty,
                    packageInfo.resolvedPath);
            }
#endif
            return InteractionBridgePathResolver.ResolvePlayerExecutable(AppContext.BaseDirectory);
        }

        protected virtual async void OnApplicationQuit()
        {
            cancellation?.Cancel();
            if (managerStarted && interactionManager != null)
            {
                try
                {
                    await interactionManager.DisconnectAsync();
                }
                catch (Exception exception)
                {
                    UnityEngine.Debug.LogWarning(
                        "Interaction IPC shutdown failed: " + exception.Message,
                        this);
                }

                managerStarted = false;
            }

            if (ExitBridgeWithUnity && ownedProcess != null && !ownedProcess.HasExited)
            {
                try
                {
                    ownedProcess.CloseMainWindow();
                }
                catch (InvalidOperationException exception)
                {
                    UnityEngine.Debug.LogWarning(
                        "BlazeInteractionBridge shutdown request failed: " + exception.Message,
                        this);
                }
            }

            ownedProcess?.Dispose();
            ownedProcess = null;
            cancellation?.Dispose();
            cancellation = null;
        }

        private sealed class InteractionBridgeProbe : IInteractionBridgeProbe
        {
            public Task<bool> CanConnectAsync(
                string pipeName,
                int timeoutMilliseconds,
                CancellationToken cancellationToken)
            {
                return InteractionPipeClient.CanConnectAsync(
                    pipeName,
                    timeoutMilliseconds,
                    cancellationToken);
            }
        }

        private sealed class InteractionProcessStarter : IInteractionProcessStarter
        {
            public IInteractionOwnedProcess Start(
                string fileName,
                string arguments,
                string workingDirectory)
            {
                var process = Process.Start(new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                return process == null ? null : new InteractionOwnedProcess(process);
            }
        }

        private sealed class InteractionOwnedProcess : IInteractionOwnedProcess
        {
            private readonly Process process;

            internal InteractionOwnedProcess(Process process)
            {
                this.process = process;
            }

            public bool HasExited { get { return process.HasExited; } }
            public bool CloseMainWindow() { return process.CloseMainWindow(); }
            public void Dispose() { process.Dispose(); }
        }
    }
}
