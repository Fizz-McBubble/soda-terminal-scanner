using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Soda.Scanner.Core;

namespace Soda.Scanner.Setup;

public class InstallerForm : Form
{
    private readonly string _installRoot;
    private readonly string _publicOrigin;
    private readonly bool _testMode;
    private readonly bool _noLaunch;
    private readonly string? _overrideArchive;

    private Label _titleLabel = null!;
    private Label _subtitleLabel = null!;
    private Label _statusLabel = null!;
    private ProgressBar _progressBar = null!;
    private Button _actionButton = null!;
    private Button _uninstallButton = null!;
    private Button _cancelButton = null!;
    private Panel _cardPanel = null!;
    private bool _busy;

    public InstallerForm(string installRoot, string publicOrigin, bool testMode, bool noLaunch, string? overrideArchive = null)
    {
        _installRoot = installRoot;
        _publicOrigin = publicOrigin;
        _testMode = testMode;
        _noLaunch = noLaunch;
        _overrideArchive = overrideArchive;

        InitializeComponent();
        FormClosing += (_, e) => { if (_busy) e.Cancel = true; };
        CheckInitialState();
    }

    private void InitializeComponent()
    {
        Text = "安装扫描助手";
        ClientSize = new Size(500, 320);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(248, 249, 250);
        Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);

        _cardPanel = new Panel
        {
            Location = new Point(24, 20),
            Size = new Size(452, 220),
            BackColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        Controls.Add(_cardPanel);

        _titleLabel = new Label
        {
            Location = new Point(20, 18),
            Size = new Size(410, 28),
            Font = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(33, 37, 41),
            Text = "Soda Terminal 扫描助手"
        };
        _cardPanel.Controls.Add(_titleLabel);

        _subtitleLabel = new Label
        {
            Location = new Point(22, 50),
            Size = new Size(410, 20),
            ForeColor = Color.FromArgb(108, 117, 125),
            Text = "Windows 版《绝区零》驱动盘扫描"
        };
        _cardPanel.Controls.Add(_subtitleLabel);

        _statusLabel = new Label
        {
            Location = new Point(22, 90),
            Size = new Size(410, 50),
            ForeColor = Color.FromArgb(73, 80, 87),
            Text = "安装后即可在网页连接使用。"
        };
        _cardPanel.Controls.Add(_statusLabel);

        _progressBar = new ProgressBar
        {
            Location = new Point(22, 160),
            Size = new Size(408, 18),
            Visible = false,
            Minimum = 0,
            Maximum = 100
        };
        _cardPanel.Controls.Add(_progressBar);

        _actionButton = new Button
        {
            Location = new Point(356, 260),
            Size = new Size(120, 36),
            BackColor = Color.FromArgb(107, 89, 148),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold, GraphicsUnit.Point),
            Text = "开始安装",
            Cursor = Cursors.Hand
        };
        _actionButton.FlatAppearance.BorderSize = 0;
        _actionButton.Click += OnActionClick;
        Controls.Add(_actionButton);

        _uninstallButton = new Button
        {
            Location = new Point(226, 260),
            Size = new Size(120, 36),
            BackColor = Color.FromArgb(220, 53, 69),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Text = "卸载",
            Visible = false,
            Cursor = Cursors.Hand
        };
        _uninstallButton.FlatAppearance.BorderSize = 0;
        _uninstallButton.Click += OnUninstallClick;
        Controls.Add(_uninstallButton);

        _cancelButton = new Button
        {
            Location = new Point(24, 260),
            Size = new Size(90, 36),
            BackColor = Color.FromArgb(233, 236, 239),
            ForeColor = Color.FromArgb(33, 37, 41),
            FlatStyle = FlatStyle.Flat,
            Text = "取消",
            Cursor = Cursors.Hand
        };
        _cancelButton.FlatAppearance.BorderSize = 0;
        _cancelButton.Click += (s, e) => Close();
        Controls.Add(_cancelButton);
    }

    private void CheckInitialState()
    {
        if (Directory.Exists(Path.Combine(_installRoot, "helper")) || File.Exists(Path.Combine(_installRoot, "active.json")))
        {
            _statusLabel.Text = "已安装。可修复安装或卸载。";
            _actionButton.Text = "修复安装";
            _uninstallButton.Visible = true;
        }
    }

    private async void OnActionClick(object? sender, EventArgs e)
    {
        if (_actionButton.Text == "完成")
        {
            Close();
            return;
        }

        _actionButton.Enabled = false;
        _busy = true;
        _uninstallButton.Enabled = false;
        _cancelButton.Enabled = false;
        _progressBar.Visible = true;
        _progressBar.Value = 0;

        try
        {
            var result = await Task.Run(() =>
            {
                return InstallEngine.ExecuteInstall(
                    _installRoot,
                    _publicOrigin,
                    offlineArchiveStreamProvider: () =>
                    {
                        if (!string.IsNullOrEmpty(_overrideArchive) && File.Exists(_overrideArchive))
                        {
                            return File.OpenRead(_overrideArchive);
                        }
                        var asm = Assembly.GetExecutingAssembly();
                        var resourceName = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(ScannerConstants.LockedAssetName, StringComparison.OrdinalIgnoreCase));
                        if (resourceName == null) throw new InvalidOperationException("Embedded locked asset not found in setup executable.");
                        return asm.GetManifestResourceStream(resourceName) ?? throw new InvalidOperationException("Failed to open embedded asset stream.");
                    },
                    uninstallStubStreamProvider: () =>
                    {
                        var asm = Assembly.GetExecutingAssembly();
                        var resourceName = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("Soda-Scanner-Uninstall.exe", StringComparison.OrdinalIgnoreCase));
                        if (resourceName == null) throw new InvalidOperationException("Embedded uninstall stub not found in setup executable.");
                        return asm.GetManifestResourceStream(resourceName) ?? throw new InvalidOperationException("Failed to open embedded uninstall stub stream.");
                    },
                    testMode: _testMode,
                    noLaunch: _noLaunch,
                    progressCallback: (msg, percent) =>
                    {
                        BeginInvoke(() =>
                        {
                            _statusLabel.Text = msg;
                            _progressBar.Value = Math.Clamp(percent, 0, 100);
                        });
                    }
                );
            });

            _statusLabel.Text = result.Message;
            _busy = false;
            _progressBar.Value = 100;
            _actionButton.Text = "完成";
            _actionButton.BackColor = Color.FromArgb(25, 135, 84);
            _actionButton.Enabled = true;
        }
        catch (Exception ex)
        {
            _busy = false;
            _statusLabel.Text = ex.Message.Contains("asset") ? "安装包不完整，请重新下载。" : "安装未完成，请关闭扫描助手后重试。";
            _progressBar.Visible = false;
            _actionButton.Text = "重试";
            _actionButton.Enabled = true;
            _cancelButton.Enabled = true;
        }
    }

    private async void OnUninstallClick(object? sender, EventArgs e)
    {
        var confirm = MessageBox.Show(
            "卸载扫描助手？\n扫描结果会保留。",
            "确认卸载",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (confirm != DialogResult.Yes) return;
        _busy = true;

        _actionButton.Enabled = false;
        _uninstallButton.Enabled = false;
        _cancelButton.Enabled = false;
        _progressBar.Visible = true;
        _progressBar.Value = 50;

        try
        {
            var result = await Task.Run(() =>
            {
                return UninstallEngine.ExecuteUninstall(_installRoot, _testMode, (msg) =>
                {
                    BeginInvoke(() => { _statusLabel.Text = msg; });
                }, expectedUninstallerHash: SetupResources.UninstallerHash());
            });

            _statusLabel.Text = result.Message;
            _busy = false;
            _progressBar.Value = 100;
            _actionButton.Visible = false;
            _uninstallButton.Visible = false;
            _cancelButton.Text = "完成";
            _cancelButton.Enabled = true;
        }
        catch (Exception)
        {
            _busy = false;
            _statusLabel.Text = "卸载未完成，请关闭扫描助手后重试。";
            _progressBar.Visible = false;
            _actionButton.Enabled = true;
            _uninstallButton.Enabled = true;
            _cancelButton.Enabled = true;
        }
    }
}
