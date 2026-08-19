using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Blaze.Radar.Internal;
#if UNITY_EDITOR
using UnityEditor.PackageManager;
#endif

namespace Blaze.Radar
{
    [DefaultExecutionOrder(-1100)]
    public sealed class RadarBridgeLauncher : MonoBehaviour
    {
        [SerializeField] private RadarRuntimeSettings settings;
        [SerializeField] public bool AutoStart = true;
        [SerializeField] public bool ExitBridgeWithUnity = true;

        private Process _ownedProcess;
        private CancellationTokenSource _cancellation;

        public bool ReusedExistingBridge { get; private set; }
        public string LastError { get; private set; } = "";

        private async void Awake()
        {
            try
            {
                if (settings == null)
                {
                    settings = RadarRuntimeSettings.LoadOrCreateRuntimeDefaults();
                }

                AutoStart = settings.AutoStart;
                ExitBridgeWithUnity = settings.ExitBridgeWithUnity;
                _cancellation = new CancellationTokenSource();
                await EnsureBridgeRunningAsync(_cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                // Exiting Play Mode while the startup probe is active is a normal shutdown path.
            }
            catch (Exception exception)
            {
                LastError = $"RadarBridge startup failed: {exception.Message}";
                UnityEngine.Debug.LogError(LastError, this);
            }
        }

        public async Task EnsureBridgeRunningAsync(CancellationToken cancellationToken)
        {
            if (await RadarPipeClient.CanConnectAsync(
                    settings.PipeName,
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
                LastError = $"RadarBridge.exe not found: {executable}";
                UnityEngine.Debug.LogError(LastError, this);
                return;
            }

            var arguments = $"--parent-pid {Process.GetCurrentProcess().Id} --minimized";
            if (!string.IsNullOrWhiteSpace(settings.ProfilePath))
            {
                arguments += $" --profile \"{settings.ProfilePath}\"";
            }

            _ownedProcess = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                WorkingDirectory = Path.GetDirectoryName(executable),
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (_ownedProcess == null)
            {
                throw new InvalidOperationException("RadarBridge process could not be created.");
            }
        }

        private string ResolveBridgeExecutable()
        {
#if UNITY_EDITOR
            if (!string.IsNullOrWhiteSpace(settings.EditorBridgeExecutable))
            {
                return BridgePathResolver.ResolveEditorExecutable(settings.EditorBridgeExecutable, null);
            }

            var packageInfo = PackageInfo.FindForAssembly(typeof(RadarBridgeLauncher).Assembly);
            if (packageInfo != null && !string.IsNullOrWhiteSpace(packageInfo.resolvedPath))
            {
                return BridgePathResolver.ResolveEditorExecutable(string.Empty, packageInfo.resolvedPath);
            }
#endif
            return BridgePathResolver.ResolvePlayerExecutable(AppContext.BaseDirectory);
        }

        private void OnApplicationQuit()
        {
            _cancellation?.Cancel();
            if (ExitBridgeWithUnity && _ownedProcess != null && !_ownedProcess.HasExited)
            {
                try
                {
                    _ownedProcess.CloseMainWindow();
                }
                catch (InvalidOperationException exception)
                {
                    UnityEngine.Debug.LogWarning($"RadarBridge shutdown request failed: {exception.Message}", this);
                }
            }

            _ownedProcess?.Dispose();
            _ownedProcess = null;
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }
}
