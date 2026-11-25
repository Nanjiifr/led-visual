using LedVisual.Core.Models;
using LedVisual.Core.Services;

namespace LedVisual.App.Controllers;

public enum AppStatusKind
{
    Idle,
    Recording,
    Thinking,
    Blinking
}

public readonly record struct AppStatus(AppStatusKind Kind, int FrameCount = 0)
{
    public string Label => Kind switch
    {
        AppStatusKind.Idle => "Ready",
        AppStatusKind.Recording => FrameCount > 0 ? $"Recording ({FrameCount} frames)…" : "Recording…",
        AppStatusKind.Thinking => "Analyzing…",
        AppStatusKind.Blinking => "Blinking LED…",
        _ => "Ready"
    };
}

/// <summary>
/// Orchestrates global hotkey -> silent screen capture -> GeminiClient -> Caps Lock LED BlinkEngine.
/// Never displays any on-screen notifications, toasts, or popups when triggered.
/// </summary>
public sealed class AppCoordinator : IDisposable
{
    private readonly SynchronizationContext? _uiContext;
    private readonly SettingsStore _settingsStore;
    private readonly IApiKeyStore _secretStore;
    private readonly GeminiClient _geminiClient;
    private readonly BlinkEngine _blinkEngine;
    private readonly ScreenCaptureService _screenCapture;
    private readonly GlobalHotkeyMonitor _hotkeyMonitor;

    private AppSettings _settings;
    private AppStatus _status = new(AppStatusKind.Idle);
    private string _lastSummary = "No captures yet";
    private string? _lastError;
    private bool _hasApiKey;
    private bool _isEditingHotkey;
    private bool _disposed;

    private CancellationTokenSource? _activeWorkflowCts;
    private Task? _activeWorkflowTask;

    public event Action? StateChanged;

    public AppSettings Settings => _settings;
    public AppStatus Status => _status;
    public string LastSummary => _lastSummary;
    public string? LastError => _lastError;
    public bool HasApiKey => _hasApiKey;
    public bool IsHotkeyMonitorActive => _hotkeyMonitor.IsRunning;

    public bool IsEditingHotkey
    {
        get => _isEditingHotkey;
        set
        {
            _isEditingHotkey = value;
            _hotkeyMonitor.IsSuspended = value;
        }
    }

    public AppCoordinator(
        SettingsStore? settingsStore = null,
        IApiKeyStore? secretStore = null,
        GeminiClient? geminiClient = null,
        BlinkEngine? blinkEngine = null,
        ScreenCaptureService? screenCapture = null)
    {
        _uiContext = SynchronizationContext.Current;
        _settingsStore = settingsStore ?? new SettingsStore();
        _secretStore = secretStore ?? new SecretStore();
        _geminiClient = geminiClient ?? new GeminiClient();
        _blinkEngine = blinkEngine ?? new BlinkEngine();
        _screenCapture = screenCapture ?? new ScreenCaptureService();

        _settings = _settingsStore.Load();
        _hotkeyMonitor = new GlobalHotkeyMonitor(
            hotkey: _settings.Hotkey,
            holdThresholdMs: _settings.HoldThresholdMs,
            swallowEvents: _settings.SwallowHotkeyEvents);

        _hotkeyMonitor.OnTap += HandleTapCapture;
        _hotkeyMonitor.OnHoldStarted += HandleHoldStarted;
        _hotkeyMonitor.OnHoldEnded += HandleHoldEnded;

        RefreshStatusFlags();
        _hotkeyMonitor.Start();
    }

    public void UpdateSettings(AppSettings updated)
    {
        _settings = updated;
        _settingsStore.Save(_settings);
        _hotkeyMonitor.UpdateConfiguration(
            _settings.Hotkey,
            _settings.HoldThresholdMs,
            _settings.SwallowHotkeyEvents);
        NotifyStateChanged();
    }

    public void RefreshStatusFlags()
    {
        try
        {
            _hasApiKey = !string.IsNullOrWhiteSpace(_secretStore.LoadApiKey());
        }
        catch
        {
            _hasApiKey = false;
        }

        if (!_hotkeyMonitor.IsRunning && !_disposed)
        {
            _hotkeyMonitor.Start();
        }

        NotifyStateChanged();
    }

    public string LoadApiKey()
    {
        try
        {
            return _secretStore.LoadApiKey() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public void SaveApiKey(string key)
    {
        var trimmed = key.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            _secretStore.DeleteApiKey();
            _hasApiKey = false;
        }
        else
        {
            _secretStore.SaveApiKey(trimmed);
            _hasApiKey = true;
        }

        NotifyStateChanged();
    }

    public async Task<(bool Success, IReadOnlyList<string> Models, string? ErrorMessage)> VerifyApiKeyAsync(
        string? candidateKey = null,
        CancellationToken cancellationToken = default)
    {
        var keyToTest = (candidateKey ?? LoadApiKey()).Trim();
        try
        {
            var models = await _geminiClient.ListAvailableModelsAsync(keyToTest, cancellationToken)
                .ConfigureAwait(false);
            return (true, models, null);
        }
        catch (Exception ex)
        {
            return (false, [], ex.Message);
        }
    }

    // MARK: - Manual & Test Actions

    public void TriggerManualScreenshot()
    {
        HandleTapCapture();
    }

    public void TestBlink(int[] answers)
    {
        var cts = ResetActiveWorkflow();
        var timings = _settings.BlinkTimings;
        _activeWorkflowTask = RunBlinkTestAsync(answers, timings, cts.Token);
    }

    public void TestErrorBlink()
    {
        var cts = ResetActiveWorkflow();
        var timings = _settings.BlinkTimings;
        _activeWorkflowTask = RunErrorBlinkTestAsync(timings, cts.Token);
    }

    public void Shutdown()
    {
        _hotkeyMonitor.Stop();
        CancelActiveWorkflow();
        _blinkEngine.Cancel();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Shutdown();
        _hotkeyMonitor.OnTap -= HandleTapCapture;
        _hotkeyMonitor.OnHoldStarted -= HandleHoldStarted;
        _hotkeyMonitor.OnHoldEnded -= HandleHoldEnded;
        _hotkeyMonitor.Dispose();
    }

    // MARK: - Hotkey Capture Workflow (Completely Discreet)

    private CancellationTokenSource ResetActiveWorkflow()
    {
        CancelActiveWorkflow();
        var cts = new CancellationTokenSource();
        _activeWorkflowCts = cts;
        return cts;
    }

    private void CancelActiveWorkflow()
    {
        var existing = _activeWorkflowCts;
        _activeWorkflowCts = null;
        if (existing is not null)
        {
            try
            {
                existing.Cancel();
                existing.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        _blinkEngine.Cancel();
        _ = _screenCapture.CancelRecordingAsync();
    }

    private void HandleTapCapture()
    {
        var cts = ResetActiveWorkflow();
        var currentSettings = _settings;
        _activeWorkflowTask = RunTapCaptureAsync(currentSettings, cts.Token);
    }

    private void HandleHoldStarted()
    {
        var cts = ResetActiveWorkflow();
        var currentSettings = _settings;
        SetStatus(new AppStatus(AppStatusKind.Recording, 0));
        _activeWorkflowTask = RunHoldStartAsync(currentSettings, cts.Token);
    }

    private void HandleHoldEnded()
    {
        var currentSettings = _settings;
        var startTask = _activeWorkflowTask;
        var cts = new CancellationTokenSource();

        // Replace CTS without cancelling the recording that is about to stop
        var previousCts = _activeWorkflowCts;
        _activeWorkflowCts = cts;
        previousCts?.Dispose();

        _activeWorkflowTask = RunHoldEndAsync(startTask, currentSettings, cts.Token);
    }

    private async Task RunBlinkTestAsync(int[] answers, BlinkTimings timings, CancellationToken ct)
    {
        try
        {
            SetStatus(new AppStatus(AppStatusKind.Blinking));
            await _blinkEngine.BlinkAnswersAsync(answers, timings, ct).ConfigureAwait(false);
            if (!ct.IsCancellationRequested)
            {
                SetStatus(new AppStatus(AppStatusKind.Idle));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RunErrorBlinkTestAsync(BlinkTimings timings, CancellationToken ct)
    {
        try
        {
            SetStatus(new AppStatus(AppStatusKind.Blinking));
            await _blinkEngine.BlinkErrorAsync(timings, ct).ConfigureAwait(false);
            if (!ct.IsCancellationRequested)
            {
                SetStatus(new AppStatus(AppStatusKind.Idle));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RunTapCaptureAsync(AppSettings currentSettings, CancellationToken ct)
    {
        SetStatus(new AppStatus(AppStatusKind.Thinking));
        try
        {
            var frames = await _screenCapture.CaptureScreenshotAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            await ResolveAndBlinkAsync(frames, "Screenshot", currentSettings, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            await HandleFailureAsync(ex.Message, currentSettings.BlinkTimings, ct).ConfigureAwait(false);
        }
    }

    private async Task RunHoldStartAsync(AppSettings currentSettings, CancellationToken ct)
    {
        try
        {
            await _screenCapture.StartRecordingAsync(
                sampleIntervalMs: currentSettings.RecordingSampleIntervalMs,
                maxFrames: currentSettings.MaxRecordingFrames,
                maxDurationSeconds: currentSettings.MaxRecordingSeconds,
                onFrameCountChanged: count =>
                {
                    PostToUi(() =>
                    {
                        if (_status.Kind == AppStatusKind.Recording)
                        {
                            _status = new AppStatus(AppStatusKind.Recording, count);
                            StateChanged?.Invoke();
                        }
                    });
                },
                cancellationToken: ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            await HandleFailureAsync(ex.Message, currentSettings.BlinkTimings, ct).ConfigureAwait(false);
        }
    }

    private async Task RunHoldEndAsync(Task? startTask, AppSettings currentSettings, CancellationToken ct)
    {
        try
        {
            if (startTask is not null)
            {
                await startTask.ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();
            SetStatus(new AppStatus(AppStatusKind.Thinking));

            var frames = await _screenCapture.StopRecordingAsync().ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            var label = $"Recording ({frames.Count} frame{(frames.Count == 1 ? string.Empty : "s")})";
            await ResolveAndBlinkAsync(frames, label, currentSettings, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            await HandleFailureAsync(ex.Message, currentSettings.BlinkTimings, ct).ConfigureAwait(false);
        }
    }

    private async Task ResolveAndBlinkAsync(
        IReadOnlyList<byte[]> frames,
        string captureLabel,
        AppSettings currentSettings,
        CancellationToken ct)
    {
        try
        {
            var apiKey = _secretStore.LoadApiKey();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw GeminiClientException.MissingApiKey();
            }

            var resolution = await _geminiClient.SolveMcqAsync(
                jpegFrames: frames,
                apiKey: apiKey,
                model: currentSettings.Model,
                autoFallback: currentSettings.AutoFallbackOnServerError,
                cancellationToken: ct).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();

            var answerText = string.Join(", ", resolution.Answers);
            PostToUi(() =>
            {
                _lastError = null;
                _lastSummary = $"{captureLabel} -> Answer: {answerText} ({resolution.ModelUsed.GetApiId()})";
                _status = new AppStatus(AppStatusKind.Blinking);
                StateChanged?.Invoke();
            });

            await _blinkEngine.BlinkAnswersAsync(resolution.Answers, currentSettings.BlinkTimings, ct)
                .ConfigureAwait(false);

            if (!ct.IsCancellationRequested)
            {
                SetStatus(new AppStatus(AppStatusKind.Idle));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            await HandleFailureAsync(ex.Message, currentSettings.BlinkTimings, ct).ConfigureAwait(false);
        }
    }

    private async Task HandleFailureAsync(string message, BlinkTimings timings, CancellationToken ct)
    {
        PostToUi(() =>
        {
            _lastError = message;
            _lastSummary = "Failed";
            _status = new AppStatus(AppStatusKind.Blinking);
            StateChanged?.Invoke();
        });

        await _blinkEngine.BlinkErrorAsync(timings, ct).ConfigureAwait(false);

        if (!ct.IsCancellationRequested)
        {
            SetStatus(new AppStatus(AppStatusKind.Idle));
        }
    }

    private void SetStatus(AppStatus newStatus)
    {
        PostToUi(() =>
        {
            _status = newStatus;
            StateChanged?.Invoke();
        });
    }

    private void NotifyStateChanged()
    {
        PostToUi(() => StateChanged?.Invoke());
    }

    private void PostToUi(Action action)
    {
        if (_uiContext is not null && SynchronizationContext.Current != _uiContext)
        {
            _uiContext.Post(_ => action(), null);
        }
        else
        {
            action();
        }
    }
}
