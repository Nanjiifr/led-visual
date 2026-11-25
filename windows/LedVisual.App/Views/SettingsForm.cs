using System.Drawing;
using LedVisual.App.Controllers;
using LedVisual.Core.Models;

namespace LedVisual.App.Views;

public sealed class SettingsForm : Form
{
    private readonly AppCoordinator _coordinator;
    private readonly HotkeyRecorderButton _hotkeyRecorder;
    private readonly CheckBox _swallowCheckBox;
    private readonly NumericUpDown _holdThresholdNumeric;
    private readonly NumericUpDown _sampleIntervalNumeric;
    private readonly NumericUpDown _maxFramesNumeric;

    private readonly TextBox _apiKeyTextBox;
    private readonly Button _saveKeyButton;
    private readonly Button _testKeyButton;
    private readonly Label _keyStatusLabel;
    private readonly ComboBox _modelComboBox;
    private readonly CheckBox _autoFallbackCheckBox;

    private readonly NumericUpDown _shortOnNumeric;
    private readonly NumericUpDown _shortOffNumeric;
    private readonly NumericUpDown _interAnswerPauseNumeric;
    private readonly NumericUpDown _errorLongOnNumeric;

    private bool _isLoading;

    public SettingsForm(AppCoordinator coordinator)
    {
        _coordinator = coordinator;

        Text = "LedVisual Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(540, 625);
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;

        // --- Hotkey & Capture Group ---
        var hotkeyGroup = new GroupBox
        {
            Text = "Hotkey & Capture",
            Left = 14,
            Top = 12,
            Width = 512,
            Height = 205
        };

        hotkeyGroup.Controls.Add(new Label
        {
            Text = "Trigger Hotkey:",
            Left = 14,
            Top = 26,
            Width = 130,
            AutoSize = false
        });

        _hotkeyRecorder = new HotkeyRecorderButton
        {
            Left = 152,
            Top = 22,
            Width = 346,
            Height = 62
        };
        _hotkeyRecorder.RecordingChanged += isRecording => _coordinator.IsEditingHotkey = isRecording;
        _hotkeyRecorder.HotkeyChanged += hotkey =>
        {
            if (_isLoading) return;
            _coordinator.UpdateSettings(_coordinator.Settings with { Hotkey = hotkey });
        };
        hotkeyGroup.Controls.Add(_hotkeyRecorder);

        _swallowCheckBox = new CheckBox
        {
            Text = "Swallow hotkey keystroke",
            Left = 152,
            Top = 88,
            Width = 260,
            AutoSize = true
        };
        _swallowCheckBox.CheckedChanged += (_, _) => SaveNumericAndToggleSettings();
        hotkeyGroup.Controls.Add(_swallowCheckBox);

        _holdThresholdNumeric = AddNumericRow(hotkeyGroup, "Hold threshold (ms):", 116, 200, 1000, 50);
        _sampleIntervalNumeric = AddNumericRow(hotkeyGroup, "Recording frame interval (ms):", 144, 250, 1500, 50);
        _maxFramesNumeric = AddNumericRow(hotkeyGroup, "Max recording frames:", 172, 6, 40, 2);

        Controls.Add(hotkeyGroup);

        // --- Google AI Studio Group ---
        var apiGroup = new GroupBox
        {
            Text = "Google AI Studio",
            Left = 14,
            Top = 227,
            Width = 512,
            Height = 165
        };

        apiGroup.Controls.Add(new Label
        {
            Text = "API Key (DPAPI):",
            Left = 14,
            Top = 27,
            Width = 130,
            AutoSize = false
        });

        _apiKeyTextBox = new TextBox
        {
            Left = 152,
            Top = 24,
            Width = 204,
            UseSystemPasswordChar = true,
            PlaceholderText = "Encrypted with Windows DPAPI"
        };
        _apiKeyTextBox.TextChanged += (_, _) => UpdateApiKeyButtons();
        apiGroup.Controls.Add(_apiKeyTextBox);

        _saveKeyButton = new Button
        {
            Text = "Save",
            Left = 364,
            Top = 23,
            Width = 62,
            Height = 25
        };
        _saveKeyButton.Click += (_, _) => SaveApiKey();
        apiGroup.Controls.Add(_saveKeyButton);

        _testKeyButton = new Button
        {
            Text = "Test",
            Left = 432,
            Top = 23,
            Width = 64,
            Height = 25
        };
        _testKeyButton.Click += async (_, _) => await TestApiKeyAsync();
        apiGroup.Controls.Add(_testKeyButton);

        _keyStatusLabel = new Label
        {
            Left = 152,
            Top = 53,
            Width = 344,
            Height = 30,
            AutoSize = false,
            Font = new Font(Font.FontFamily, 8.2f)
        };
        apiGroup.Controls.Add(_keyStatusLabel);

        apiGroup.Controls.Add(new Label
        {
            Text = "Model:",
            Left = 14,
            Top = 92,
            Width = 130,
            AutoSize = false
        });

        _modelComboBox = new ComboBox
        {
            Left = 152,
            Top = 89,
            Width = 344,
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        foreach (var model in AIModelExtensions.AllModels)
        {
            _modelComboBox.Items.Add(new ModelItem(model));
        }
        _modelComboBox.SelectedIndexChanged += (_, _) => SaveNumericAndToggleSettings();
        apiGroup.Controls.Add(_modelComboBox);

        _autoFallbackCheckBox = new CheckBox
        {
            Text = "Fallback to alternate model on 503 overload",
            Left = 152,
            Top = 124,
            Width = 340,
            AutoSize = true
        };
        _autoFallbackCheckBox.CheckedChanged += (_, _) => SaveNumericAndToggleSettings();
        apiGroup.Controls.Add(_autoFallbackCheckBox);

        Controls.Add(apiGroup);

        // --- Caps Lock LED Blinks Group ---
        var blinkGroup = new GroupBox
        {
            Text = "Caps Lock LED Blinks",
            Left = 14,
            Top = 402,
            Width = 512,
            Height = 208
        };

        _shortOnNumeric = AddNumericRow(blinkGroup, "Short blink ON (ms):", 26, 80, 500, 20);
        _shortOffNumeric = AddNumericRow(blinkGroup, "Short blink OFF (ms):", 56, 80, 500, 20);
        _interAnswerPauseNumeric = AddNumericRow(blinkGroup, "Multi-select pause (ms):", 86, 400, 2000, 50);
        _errorLongOnNumeric = AddNumericRow(blinkGroup, "Failed request long blink (ms):", 116, 700, 3000, 100);

        var testSingleBtn = new Button
        {
            Text = "Test Single (3)",
            Left = 14,
            Top = 158,
            Width = 108,
            Height = 28
        };
        testSingleBtn.Click += (_, _) => _coordinator.TestBlink([3]);
        blinkGroup.Controls.Add(testSingleBtn);

        var testMultiBtn = new Button
        {
            Text = "Test Multi (1, 3)",
            Left = 128,
            Top = 158,
            Width = 114,
            Height = 28
        };
        testMultiBtn.Click += (_, _) => _coordinator.TestBlink([1, 3]);
        blinkGroup.Controls.Add(testMultiBtn);

        var testErrorBtn = new Button
        {
            Text = "Test Failed (Long)",
            Left = 248,
            Top = 158,
            Width = 122,
            Height = 28
        };
        testErrorBtn.Click += (_, _) => _coordinator.TestErrorBlink();
        blinkGroup.Controls.Add(testErrorBtn);

        var resetTimingsBtn = new Button
        {
            Text = "Reset Timings",
            Left = 394,
            Top = 158,
            Width = 102,
            Height = 28
        };
        resetTimingsBtn.Click += (_, _) =>
        {
            _coordinator.UpdateSettings(_coordinator.Settings with { BlinkTimings = BlinkTimings.Default });
            LoadFromCoordinator();
        };
        blinkGroup.Controls.Add(resetTimingsBtn);

        Controls.Add(blinkGroup);

        LoadFromCoordinator();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _hotkeyRecorder.StopRecording();
        _coordinator.IsEditingHotkey = false;
        base.OnFormClosing(e);
    }

    private NumericUpDown AddNumericRow(
        GroupBox group,
        string labelText,
        int top,
        int min,
        int max,
        int increment)
    {
        group.Controls.Add(new Label
        {
            Text = labelText,
            Left = 14,
            Top = top + 3,
            Width = 220,
            AutoSize = false
        });

        var numeric = new NumericUpDown
        {
            Left = 396,
            Top = top,
            Width = 100,
            Minimum = min,
            Maximum = max,
            Increment = increment
        };
        numeric.ValueChanged += (_, _) => SaveNumericAndToggleSettings();
        group.Controls.Add(numeric);
        return numeric;
    }

    private void LoadFromCoordinator()
    {
        _isLoading = true;
        try
        {
            var s = _coordinator.Settings;
            _hotkeyRecorder.Hotkey = s.Hotkey;
            _swallowCheckBox.Checked = s.SwallowHotkeyEvents;
            _holdThresholdNumeric.Value = Math.Clamp(s.HoldThresholdMs, (int)_holdThresholdNumeric.Minimum, (int)_holdThresholdNumeric.Maximum);
            _sampleIntervalNumeric.Value = Math.Clamp(s.RecordingSampleIntervalMs, (int)_sampleIntervalNumeric.Minimum, (int)_sampleIntervalNumeric.Maximum);
            _maxFramesNumeric.Value = Math.Clamp(s.MaxRecordingFrames, (int)_maxFramesNumeric.Minimum, (int)_maxFramesNumeric.Maximum);

            _apiKeyTextBox.Text = _coordinator.LoadApiKey();
            UpdateApiKeyButtons();

            for (var i = 0; i < _modelComboBox.Items.Count; i++)
            {
                if (_modelComboBox.Items[i] is ModelItem item && item.Model == s.Model)
                {
                    _modelComboBox.SelectedIndex = i;
                    break;
                }
            }

            _autoFallbackCheckBox.Checked = s.AutoFallbackOnServerError;

            _shortOnNumeric.Value = Math.Clamp(s.BlinkTimings.ShortOnMs, (int)_shortOnNumeric.Minimum, (int)_shortOnNumeric.Maximum);
            _shortOffNumeric.Value = Math.Clamp(s.BlinkTimings.ShortOffMs, (int)_shortOffNumeric.Minimum, (int)_shortOffNumeric.Maximum);
            _interAnswerPauseNumeric.Value = Math.Clamp(s.BlinkTimings.InterAnswerPauseMs, (int)_interAnswerPauseNumeric.Minimum, (int)_interAnswerPauseNumeric.Maximum);
            _errorLongOnNumeric.Value = Math.Clamp(s.BlinkTimings.ErrorLongOnMs, (int)_errorLongOnNumeric.Minimum, (int)_errorLongOnNumeric.Maximum);
        }
        finally
        {
            _isLoading = false;
        }
    }

    private void SaveNumericAndToggleSettings()
    {
        if (_isLoading)
        {
            return;
        }

        var selectedModel = (_modelComboBox.SelectedItem as ModelItem)?.Model ?? _coordinator.Settings.Model;
        var updatedTimings = new BlinkTimings(
            shortOnMs: (int)_shortOnNumeric.Value,
            shortOffMs: (int)_shortOffNumeric.Value,
            interAnswerPauseMs: (int)_interAnswerPauseNumeric.Value,
            errorLongOnMs: (int)_errorLongOnNumeric.Value,
            preBlinkDarkMs: _coordinator.Settings.BlinkTimings.PreBlinkDarkMs);

        var updated = _coordinator.Settings with
        {
            SwallowHotkeyEvents = _swallowCheckBox.Checked,
            HoldThresholdMs = (int)_holdThresholdNumeric.Value,
            RecordingSampleIntervalMs = (int)_sampleIntervalNumeric.Value,
            MaxRecordingFrames = (int)_maxFramesNumeric.Value,
            Model = selectedModel,
            AutoFallbackOnServerError = _autoFallbackCheckBox.Checked,
            BlinkTimings = updatedTimings
        };

        _coordinator.UpdateSettings(updated);
    }

    private void UpdateApiKeyButtons()
    {
        var hasText = !string.IsNullOrWhiteSpace(_apiKeyTextBox.Text);
        _saveKeyButton.Enabled = hasText;
        _testKeyButton.Enabled = hasText;
    }

    private void SaveApiKey()
    {
        try
        {
            _coordinator.SaveApiKey(_apiKeyTextBox.Text);
            _keyStatusLabel.ForeColor = Color.ForestGreen;
            _keyStatusLabel.Text = "Saved securely with Windows DPAPI.";
        }
        catch (Exception ex)
        {
            _keyStatusLabel.ForeColor = Color.Firebrick;
            _keyStatusLabel.Text = ex.Message;
        }
    }

    private async Task TestApiKeyAsync()
    {
        _testKeyButton.Enabled = false;
        _testKeyButton.Text = "Testing…";
        _keyStatusLabel.Text = string.Empty;

        var candidate = _apiKeyTextBox.Text;
        var (success, models, errorMessage) = await _coordinator.VerifyApiKeyAsync(candidate);

        if (IsDisposed)
        {
            return;
        }

        _testKeyButton.Text = "Test";
        UpdateApiKeyButtons();

        if (success)
        {
            try
            {
                _coordinator.SaveApiKey(candidate);
            }
            catch
            {
            }

            _keyStatusLabel.ForeColor = Color.ForestGreen;
            var list = models.Count == 0 ? "connected" : string.Join(", ", models);
            _keyStatusLabel.Text = $"Verified & saved. Available: {list}";
        }
        else
        {
            _keyStatusLabel.ForeColor = Color.Firebrick;
            _keyStatusLabel.Text = errorMessage ?? "Verification failed.";
        }
    }

    private sealed class ModelItem(AIModel model)
    {
        public AIModel Model { get; } = model;
        public override string ToString() => Model.GetDisplayName();
    }
}
