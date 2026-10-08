using System.Diagnostics;
using ZZZScannerNext.Cleaning;
using ZZZScannerNext.Interop;
using ZZZScannerNext.Scanning;

namespace ZZZScannerNext.Ui;

public sealed partial class MainForm : Form
{
    private PostScrollPanelAcceptMode SelectedPostScrollPanelAcceptMode()
    {
        return string.Equals(_postScrollPanelAcceptModeCombo.SelectedItem?.ToString(), "adaptive-after-scroll", StringComparison.OrdinalIgnoreCase)
            ? PostScrollPanelAcceptMode.AdaptiveAfterScroll
            : PostScrollPanelAcceptMode.Safe;
    }

    private string ResolveProfileName(string? requestedProfileName = null)
    {
        var defaultProfileName = ScanOptions.DefaultProfileName;
        if (!string.IsNullOrWhiteSpace(requestedProfileName)
            && _profiles.Find(requestedProfileName) is not null)
        {
            return requestedProfileName;
        }

        if (_profiles.Find(defaultProfileName) is not null)
        {
            return defaultProfileName;
        }

        return _profiles.Profiles.FirstOrDefault()?.Name ?? defaultProfileName;
    }

    private ScanTraversalMode SelectedTraversalMode()
    {
        return _traversalModeCombo.SelectedIndex switch
        {
            1 => ScanTraversalMode.OverlapSignaturePage,
            2 => ScanTraversalMode.SafeBandViewport,
            3 => ScanTraversalMode.CalibratedPage,
            4 => ScanTraversalMode.LegacyThirdRow,
            _ => ScanTraversalMode.FromProfile
        };
    }

    private void DetectWindow()
    {
        try
        {
            var window = GameWindow.Find(_processBox.Text.Trim());
            if (_bringToFront.Checked)
            {
                window.BringToFront();
            }

            _statusLabel.Text = "窗口检测成功";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "窗口检测失败";
            MessageBox.Show(ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OpenOutputDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_lastOutputDirectory) && Directory.Exists(_lastOutputDirectory))
        {
            Process.Start(new ProcessStartInfo { FileName = _lastOutputDirectory, UseShellExecute = true });
        }
    }

    private void SetOutputDirectory(string? outputDirectory)
    {
        _lastOutputDirectory = outputDirectory;
        var canOpen = !string.IsNullOrWhiteSpace(outputDirectory) && Directory.Exists(outputDirectory);
        _openOutputButton.Enabled = canOpen;
        _outputLink.Enabled = canOpen;
        _outputLink.Text = canOpen ? outputDirectory! : "扫描完成后显示产物文件夹";
    }

    private void SetScanningState(bool scanning)
    {
        _startButton.Enabled = !scanning;
        _stopButton.Enabled = scanning;
        _detectButton.Enabled = !scanning;
        _advancedToggleButton.Enabled = !scanning;
    }

    private void ToggleAdvancedSettings()
    {
        _advancedExpanded = !_advancedExpanded;
        if (_advancedGroup is not null)
        {
            _advancedGroup.Visible = _advancedExpanded;
        }

        _advancedToggleButton.Text = _advancedExpanded ? "收起高级设置" : "展开高级设置";
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.RegisterHotKey(Handle, StopHotKeyId, NativeMethods.ModControl | NativeMethods.ModShift, NativeMethods.VkC);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        NativeMethods.UnregisterHotKey(Handle, StopHotKeyId);
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WmHotKey && m.WParam.ToInt32() == StopHotKeyId)
        {
            _scanCancellation?.Cancel();
            return;
        }

        base.WndProc(ref m);
    }

    private static TableLayoutPanel CreateGroup(Control parent, string title)
    {
        return CreateGroup(parent, title, out _);
    }

    private static TableLayoutPanel CreateGroup(Control parent, string title, out GroupBox group)
    {
        group = new GroupBox
        {
            Text = title,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            Padding = new Padding(10, 8, 10, 10),
            Margin = new Padding(0, 0, 0, 10)
        };

        var table = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Dock = DockStyle.Top,
            Margin = new Padding(0)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        group.Controls.Add(table);
        parent.Controls.Add(group);
        return table;
    }

    private static void AddLabel(Control parent, string text)
    {
        parent.Controls.Add(new Label
        {
            Text = text,
            AutoSize = true,
            Padding = new Padding(0, 8, 0, 2),
            Margin = new Padding(0)
        });
    }

    private static void AddNumericSetting(TableLayoutPanel parent, string label, NumericUpDown numeric, int minimum, int maximum)
    {
        var panel = new TableLayoutPanel
        {
            RowCount = 2,
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 8, 6)
        };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        panel.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            Padding = new Padding(0, 0, 0, 2),
            Margin = new Padding(0)
        });

        numeric.Minimum = minimum;
        numeric.Maximum = maximum;
        ConfigureSingleLine(numeric);
        panel.Controls.Add(numeric);

        var index = parent.Controls.Count;
        parent.Controls.Add(panel, index % 2, index / 2);
    }

    private static void ConfigureSingleLine(Control control)
    {
        control.Dock = DockStyle.Top;
        control.Margin = new Padding(0, 0, 0, 4);
        control.Height = 26;
    }

    private static void ConfigureButton(Button button)
    {
        button.Dock = DockStyle.Fill;
        button.Height = 34;
        button.Margin = new Padding(2, 0, 2, 0);
    }

    private static Size FitInitialSize(Size preferred)
    {
        var workArea = Screen.FromPoint(Cursor.Position).WorkingArea;
        return new Size(
            Math.Min(preferred.Width, Math.Max(430, workArea.Width - 80)),
            Math.Min(preferred.Height, Math.Max(620, workArea.Height - 80)));
    }

    private sealed class NoOpScanProgress : IProgress<ScanProgress>
    {
        public static readonly NoOpScanProgress Instance = new();

        private NoOpScanProgress()
        {
        }

        public void Report(ScanProgress value)
        {
            value.DebugImage?.Dispose();
        }
    }
}
