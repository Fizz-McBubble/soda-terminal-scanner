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
    private readonly bool _autoInstallEnabled;
    private readonly Func<Action<string, int>, Task<InstallResult>>? _testInstall;
    private readonly Func<Task<UninstallResult>>? _testUninstall;
    private readonly Func<bool>? _testConfirmUninstall;
    private readonly Func<bool>? _testOpenWorkspace;
    private readonly Func<string, bool>? _testOpenFeedback;
    private readonly Func<string, bool>? _testCopyFeedback;
    private readonly Func<string, bool>? _testSaveFeedback;
    private InstallerFeedbackReport? _feedbackReport;
    private bool _feedbackFallback;
    private Button _feedbackButton = null!;
    private readonly ContextMenuStrip _feedbackRecovery = new();

    private Label _titleLabel = null!;
    private Label _subtitleLabel = null!;
    private Label _statusLabel = null!;
    private SodaProgressBar _progressBar = null!;
    private Button _actionButton = null!;
    private Button _uninstallButton = null!;
    private Button _cancelButton = null!;
    private Panel _cardPanel = null!;
    private bool _busy;
    private bool _automaticAttemptConsumed;
    private bool _completed;

    public InstallerForm(string installRoot, string publicOrigin, bool testMode, bool noLaunch, string? overrideArchive = null)
        : this(installRoot, publicOrigin, testMode, noLaunch, overrideArchive, true) { }

    internal InstallerForm(string installRoot, string publicOrigin, bool testMode, bool noLaunch, string? overrideArchive,
        bool autoInstallEnabled, Func<Action<string, int>, Task<InstallResult>>? testInstall = null,
        Func<Task<UninstallResult>>? testUninstall = null, Func<bool>? testConfirmUninstall = null,
        Func<bool>? testOpenWorkspace = null, Func<string, bool>? testOpenFeedback = null,
        Func<string, bool>? testCopyFeedback = null, Func<string, bool>? testSaveFeedback = null)
    {
        if ((testInstall != null || testUninstall != null || testConfirmUninstall != null || testOpenWorkspace != null ||
             testOpenFeedback != null || testCopyFeedback != null || testSaveFeedback != null) && !testMode) throw new InvalidOperationException("test_install_override_forbidden");
        _installRoot = installRoot;
        _publicOrigin = publicOrigin;
        _testMode = testMode;
        _noLaunch = noLaunch;
        _overrideArchive = overrideArchive;
        _autoInstallEnabled = autoInstallEnabled;
        _testInstall = testInstall;
        _testUninstall = testUninstall; _testConfirmUninstall = testConfirmUninstall;
        _testOpenWorkspace = testOpenWorkspace;
        _testOpenFeedback = testOpenFeedback; _testCopyFeedback = testCopyFeedback; _testSaveFeedback = testSaveFeedback;

        InitializeComponent();
        FormClosing += (_, e) => { if (_busy) e.Cancel = true; };
        CheckInitialState();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (!_autoInstallEnabled || _automaticAttemptConsumed) return;
        _automaticAttemptConsumed = true;
        // Queue after the first paint so progress is visible before work begins.
        BeginInvoke(async () =>
        {
            if (IsDisposed || Disposing || _busy || _completed) return;
            if (HasInstallationTraces()) { CheckInitialState(); return; }
            await InstallAsync(firstRunOnly: true);
        });
    }

    private readonly Color _purple = Color.FromArgb(107, 89, 148);
    private TextBox _detailsLabel = null!;

    private void InitializeComponent()
    {
        SuspendLayout();
        Text = "Soda Terminal · 画面扫描";
        ClientSize = new Size(520, 348);

        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.White;
        Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
        Padding = new Padding(32);

        _cardPanel = new Panel { Location = new Point(32, 24), Size = new Size(456, 244), BackColor = Color.White };
        Controls.Add(_cardPanel);
        var brandName = Assembly.GetExecutingAssembly().GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("soda-brand-icon.png", StringComparison.Ordinal));
        using var brandStream = brandName == null ? null : Assembly.GetExecutingAssembly().GetManifestResourceStream(brandName);
        if (brandStream != null)
        {
            var image = new Bitmap(brandStream);
            _cardPanel.Controls.Add(new PictureBox { Location = new Point(0, 0), Size = new Size(44, 44),
                SizeMode = PictureBoxSizeMode.Zoom, Image = image, AccessibleName = "Soda Terminal" });
        }
        using var iconStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("soda-brand-icon.ico");
        if (iconStream != null) Icon = new Icon(iconStream);
        _titleLabel = new Label { Location = new Point(58, 0), Size = new Size(398, 30),
            Font = new Font("Microsoft YaHei UI", 17f, FontStyle.Bold), ForeColor = Color.FromArgb(34, 30, 43), Text = "画面扫描" };
        _subtitleLabel = new Label { Location = new Point(59, 32), Size = new Size(395, 22),
            ForeColor = Color.FromArgb(108, 101, 120), Text = "安装版本 " + ScannerConstants.DisplayVersion };
        _statusLabel = new Label { Location = new Point(0, 82), Size = new Size(454, 36),
            Font = new Font("Microsoft YaHei UI", 10.5f), ForeColor = Color.FromArgb(55, 48, 67),
            Text = "安装后，返回网页连接即可扫描。", AccessibleRole = AccessibleRole.StaticText };
        _progressBar = new SodaProgressBar { Location = new Point(0, 130), Size = new Size(456, 6),
            Minimum = 0, Maximum = 100, Visible = false, AccessibleName = "安装进度" };
        _detailsLabel = new TextBox { Location = new Point(0, 150), Size = new Size(456, 88),
            ForeColor = Color.FromArgb(108, 101, 120), BackColor = Color.White, BorderStyle = BorderStyle.None,
            ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.None, TabStop = false,
            AccessibleName = "安装详情", TabIndex = 4 };
        UpdateDetails("安装到当前用户目录，并添加开始菜单入口。无需管理员权限。\r\n安装完成后，返回 Soda Terminal 网页连接画面扫描。");
        _cardPanel.Controls.AddRange([_titleLabel, _subtitleLabel, _statusLabel, _progressBar, _detailsLabel]);

        _actionButton = MakeButton("开始安装", true);
        _actionButton.TabIndex = 0;
        _actionButton.Click += OnActionClick;
        _uninstallButton = MakeButton("卸载", false);
        _uninstallButton.TabIndex = 2;
        _uninstallButton.Visible = false;
        _uninstallButton.Click += OnUninstallClick;
        _cancelButton = MakeButton("关闭", false);
        _cancelButton.TabIndex = 1;
        _cancelButton.Click += (_, _) => Close();
        _feedbackButton = MakeButton("反馈安装问题", false);
        _feedbackButton.Width = 140; _feedbackButton.Visible = false; _feedbackButton.TabIndex = 3;
        _feedbackButton.Click += (_, _) => OpenFeedback();
        _feedbackRecovery.Items.Add("重新打开反馈页", null, (_, _) => { _feedbackFallback = false; OpenFeedback(); });
        _feedbackRecovery.Items.Add("复制反馈链接", null, (_, _) => CopyFeedback());
        _feedbackRecovery.Items.Add("下载反馈文件", null, (_, _) => ExportFeedback());
        Controls.AddRange([_actionButton, _uninstallButton, _cancelButton, _feedbackButton]);
        AcceptButton = _actionButton;
        CancelButton = _cancelButton;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ResumeLayout(false);
        PerformLayout();
        LayoutFooter();
        Shown += (_, _) =>
        {
            LayoutFooter(); _detailsLabel.Select(0, 0);
            ActiveControl = _actionButton.Visible && _actionButton.Enabled ? _actionButton : _cancelButton;
        };
        DpiChanged += (_, _) => BeginInvoke(LayoutFooter);
    }

    // The panel width reflects both normal WinForms DPI scaling and isolated
    // render scaling; dynamic dimensions must follow that same physical scale.
    private int Unit(int logical) => (int)Math.Round(logical * _cardPanel.Width / 456f);

    private void UpdateDetails(string text)
    {
        _detailsLabel.Text = text;
        _detailsLabel.Select(0, 0);
    }

    // Retain the existing render-preview switch; details are now always shown.
    internal void ToggleDetails() => _detailsLabel.Visible = true;

    private Button MakeButton(string text, bool primary)
    {
        var button = new Button { Size = new Size(primary ? 124 : 84, 42), Text = text,
            BackColor = primary ? _purple : Color.White, ForeColor = primary ? Color.White : Color.FromArgb(108, 101, 120),
            FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, UseVisualStyleBackColor = false };
        button.FlatAppearance.BorderSize = primary ? 0 : 1;
        button.FlatAppearance.BorderColor = primary ? _purple : Color.FromArgb(236, 233, 242);
        button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(91, 73, 132) : Color.FromArgb(248, 246, 252);
        return button;
    }

    private void LayoutFooter()
    {
        _actionButton.Location = new Point(ClientSize.Width - Unit(32) - _actionButton.Width, ClientSize.Height - Unit(58));
        _cancelButton.Location = new Point(Unit(32), ClientSize.Height - Unit(58));
        _uninstallButton.Location = new Point(Unit(126), ClientSize.Height - Unit(58));
        _feedbackButton.Location = new Point(_actionButton.Left - Unit(8) - _feedbackButton.Width, _actionButton.Top);
    }

    private void ClearFeedback()
    {
        _feedbackReport = null; _feedbackFallback = false; _feedbackButton.Visible = false;
        _feedbackButton.Text = "反馈安装问题"; _feedbackRecovery.Close();
    }
    internal void OpenFeedback()
    {
        if (_feedbackReport is null) return;
        if (!_feedbackFallback)
        {
            var url = InstallerFeedback.LaunchUrl(_feedbackReport);
            var opened = _testOpenFeedback?.Invoke(url) ?? (!_testMode && InstallerFeedback.OpenBrowser(url));
            if (opened) { UpdateDetails("反馈页已打开，请在网页核对并主动发送。\r\n本次反馈仅包含安装版本、阶段和有限错误码。"); return; }
            _feedbackFallback = true; _feedbackButton.Text = "复制 / 下载";
            UpdateDetails("反馈页未打开，可复制反馈链接或下载反馈文件。\r\n反馈内容保留在本机，尚未发送。");
        }
        if (Visible) _feedbackRecovery.Show(_feedbackButton, new Point(0, _feedbackButton.Height));
    }
    internal void CopyFeedback()
    {
        if (_feedbackReport is null) return;
        var url = InstallerFeedback.LaunchUrl(_feedbackReport);
        var copied = _testCopyFeedback?.Invoke(url) ?? (!_testMode && InstallerFeedback.Copy(url));
        UpdateDetails(copied ? "反馈链接已复制，可粘贴到浏览器打开后主动发送。" : "复制未完成，可下载反馈文件后重试。");
    }
    internal void ExportFeedback()
    {
        if (_feedbackReport is null) return;
        var json = InstallerFeedback.Serialize(_feedbackReport);
        if (_testSaveFeedback is not null)
        {
            UpdateDetails(_testSaveFeedback(json) ? "反馈文件已保存，尚未发送。" : "保存未完成，可复制反馈链接后重试。"); return;
        }
        if (_testMode) return;
        using var dialog = new SaveFileDialog { Title = "下载安装反馈", Filter = "JSON 文件|*.json",
            FileName = "Soda-画面扫描-安装反馈-" + _feedbackReport.InstallerVersion + ".json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { File.WriteAllText(dialog.FileName, json); UpdateDetails("反馈文件已保存，尚未发送。"); }
        catch { UpdateDetails("保存未完成，可复制反馈链接后重试。"); }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) _feedbackRecovery.Dispose();
        base.Dispose(disposing);
    }

    private sealed class SodaProgressBar : ProgressBar
    {
        private readonly System.Windows.Forms.Timer _animation = new() { Interval = 40 };
        private int _offset;
        private bool _indeterminate;
        public bool Indeterminate
        {
            get => _indeterminate;
            set { _indeterminate = value; _animation.Enabled = value; Invalidate(); }
        }
        public SodaProgressBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            _animation.Tick += (_, _) => { _offset = (_offset + 7) % Math.Max(1, Width + Width / 3); Invalidate(); };
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Color.FromArgb(239, 235, 247));
            using var fill = new SolidBrush(Color.FromArgb(107, 89, 148));
            if (_indeterminate) e.Graphics.FillRectangle(fill, _offset - Width / 3, 0, Width / 3, Height);
            else e.Graphics.FillRectangle(fill, 0, 0, Width * Value / Math.Max(1, Maximum), Height);
        }
        protected override void Dispose(bool disposing) { if (disposing) _animation.Dispose(); base.Dispose(disposing); }
        protected override void WndProc(ref Message m) { base.WndProc(ref m); if (m.Msg == 0x402) Invalidate(); }
    }

    internal void ApplyRenderFixture(string state)
    {
        if (!_testMode || _autoInstallEnabled) throw new InvalidOperationException("test_preview_required");
        switch (state)
        {
            case "initial": break;
            case "preparing":
                _statusLabel.Text = "正在准备组件…"; _actionButton.Text = "安装中"; _actionButton.Enabled = false;
                _progressBar.Indeterminate = true;
                _actionButton.Visible = false;
                _cancelButton.Visible = false; _uninstallButton.Visible = false;
                _progressBar.Visible = true; _progressBar.Value = 5; break;
            case "progress":
                _statusLabel.Text = "正在安装扫描组件…"; _actionButton.Text = "安装中"; _actionButton.Enabled = false;
                _actionButton.Visible = false;
                _cancelButton.Visible = false; _uninstallButton.Visible = false;
                _progressBar.Visible = true; _progressBar.Value = 47; break;
            case "completion":
                _statusLabel.Text = "安装完成，请返回网页连接。"; _actionButton.Text = "完成";
                _cancelButton.Visible = false; _uninstallButton.Visible = false; _progressBar.Visible = true; _progressBar.Value = 100; break;
            case "error":
                _statusLabel.Text = "安装未完成，请关闭画面扫描后重试。"; _actionButton.Text = "重试";
            _cancelButton.Visible = true;
                _feedbackReport = InstallerFeedback.Create("failed", "installer_local_operation_failed", 0);
                _feedbackButton.Visible = true;
                UpdateDetails("本图为错误状态渲染测试，不代表真实安装失败。"); break;
            case "repair":
                _statusLabel.Text = "已检测到画面扫描，可以修复安装。"; _actionButton.Text = "修复安装"; _uninstallButton.Visible = true; break;
            default: throw new InvalidOperationException("test_preview_state_invalid");
        }
    }

    private void CheckInitialState()
    {
        if (HasInstallationTraces())
        {
            var current = InstallerStartup.InstalledVersion(_installRoot);
            var upgrade = current != null && current != ScannerConstants.DisplayVersion;
            _subtitleLabel.Text = current == null ? "安装版本 " + ScannerConstants.DisplayVersion
                : "当前 " + current + "  →  安装 " + ScannerConstants.DisplayVersion;
            _statusLabel.Text = upgrade ? "已有画面扫描，可以升级到新版本。" : "已检测到画面扫描，可以修复安装。";
            _actionButton.Text = upgrade ? "升级安装" : "修复安装";
            _actionButton.Enabled = true;
            _actionButton.Visible = true;
            _uninstallButton.Visible = true;
        }
        else if (_autoInstallEnabled)
        {
            _statusLabel.Text = "正在准备组件…";
            _actionButton.Text = "正在准备";
            _actionButton.Enabled = false;
            _actionButton.Visible = false;
        }
    }

    private bool HasInstallationTraces() => InstallerStartup.HasInstallationTraces(
        _installRoot, RegistryHelper.HasInstallationRegistration(_testMode));

    private async void OnActionClick(object? sender, EventArgs e)
    {
        if (_busy) return;
        if (_completed)
        {
            if (_actionButton.Text == "启动助手")
            {
                if (ProcessHelper.StartManagedHelper(Path.Combine(_installRoot, ScannerConstants.HelperExecutableRelative), _publicOrigin, _testMode))
                { _statusLabel.Text = "助手已在后台启动，请返回网页连接。"; _actionButton.Text = "完成"; }
                else _statusLabel.Text = "助手未启动，请重试或修复安装。";
                return;
            }
            if (_actionButton.Text == "返回网页")
            {
                var opened = _testOpenWorkspace?.Invoke() ?? ProcessHelper.OpenWorkspace(_publicOrigin, _testMode);
                _statusLabel.Text = opened ? "扫描页已打开，请在网页连接画面扫描。" : "网页未打开，请重试，或打开 sodaterminal.com 的扫描页。";
                return;
            }
            Close();
            return;
        }

        await InstallAsync();
    }

    private async Task InstallAsync(bool firstRunOnly = false)
    {
        if (_busy || _completed) return;
        ClearFeedback();
        var diagnostics = new InstallerDiagnostics();
        _actionButton.Enabled = false;
        _actionButton.Visible = false;
        _busy = true;
        _uninstallButton.Enabled = false;
        _cancelButton.Enabled = false;
        _cancelButton.Visible = false;
        _actionButton.Text = "安装中";
        _progressBar.Visible = true;
        _progressBar.Value = 0;
        _statusLabel.Text = "正在准备组件…";
        _progressBar.Indeterminate = true;

        try
        {
            Action<string, int> progress = (msg, percent) =>
            {
                BeginInvoke(() =>
                {
                    if (!_busy) return;
                    _statusLabel.Text = msg;
                    _progressBar.Indeterminate = false;
                    _progressBar.Value = Math.Clamp(percent, 0, 100);
                    _progressBar.Invalidate();
                });
            };
            var result = _testInstall != null ? await _testInstall(progress) : await Task.Run(() =>
            {
                if (_overrideArchive == null) PayloadFiles.WaitUntilReady();
                return InstallEngine.ExecuteInstall(
                    _installRoot,
                    _publicOrigin,
                    offlineArchiveStreamProvider: () => SetupResources.OpenArchive(_overrideArchive),
                    uninstallStubStreamProvider: SetupResources.OpenUninstaller,
                    testMode: _testMode,
                    noLaunch: _noLaunch,
                    progressCallback: progress,
                    firstRunOnly: firstRunOnly,
                    phaseCallback: diagnostics.Phase
                );
            });

            _statusLabel.Text = result.Message;
            UpdateDetails("画面扫描已安装。返回 Soda Terminal 网页连接即可使用。");
            _busy = false;
            _progressBar.Indeterminate = false;
            _completed = true;
            _progressBar.Value = 100;
            _actionButton.Text = (!_testMode && !_noLaunch) || _testOpenWorkspace != null
                ? result.Started ? "完成" : "启动助手"
                : "完成";
            _actionButton.Visible = true;
            _cancelButton.Visible = false;
            _uninstallButton.Visible = false;
            _actionButton.Enabled = true;
            _cancelButton.Text = "关闭";
            _cancelButton.Enabled = true;
        }
        catch (Exception ex)
        {
            _busy = false;
            _progressBar.Indeterminate = false;
            _feedbackReport = diagnostics.Failure(ex);
            var tracesAfterFailure = HasInstallationTraces();
            var saved = InstallerDiagnostics.Save(_installRoot, _feedbackReport);
            _feedbackButton.Visible = true;
            UpdateDetails("安装未完成，可重试或反馈安装问题。\r\n" +
                (saved ? "有限诊断已保存在本机，尚未发送。" : "有限诊断仍保留在窗口中，尚未发送。"));
            _statusLabel.Text = "安装未完成，可以重试或反馈问题。";
            _progressBar.Visible = false;
            _actionButton.Text = "重试";
            _actionButton.Visible = true;
            _cancelButton.Visible = true;
            _actionButton.Enabled = true;
            _cancelButton.Enabled = true;
            _cancelButton.Text = "关闭";
            _uninstallButton.Visible = tracesAfterFailure;
            _uninstallButton.Enabled = true;
            if (ex.Message == "scanner_setup_existing_installation") CheckInitialState();
            LayoutFooter();
        }
    }

    private async void OnUninstallClick(object? sender, EventArgs e)
    {
        if (_busy || _completed) return;
        var confirm = _testConfirmUninstall != null ? (_testConfirmUninstall() ? DialogResult.Yes : DialogResult.No) : MessageBox.Show(
            "卸载画面扫描？\n扫描结果会保留。",
            "确认卸载",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (confirm != DialogResult.Yes) return;
        ClearFeedback();
        _busy = true;

        _actionButton.Enabled = false;
        _uninstallButton.Enabled = false;
        _cancelButton.Enabled = false;
        _actionButton.Visible = false;
        _uninstallButton.Visible = false;
        _cancelButton.Visible = false;
        _cancelButton.Text = "卸载中";
        _progressBar.Visible = true;
        _progressBar.Value = 50;

        try
        {
            var result = _testUninstall != null ? await _testUninstall() : await Task.Run(() =>
            {
                return UninstallEngine.ExecuteUninstall(_installRoot, _testMode, (msg) =>
                {
                    BeginInvoke(() => { _statusLabel.Text = msg; });
                }, expectedUninstallerHash: SetupResources.UninstallerHash());
            });

            _statusLabel.Text = result.Message;
            UpdateDetails("卸载完成。扫描结果和个人文件已保留。");
            _busy = false;
            _progressBar.Value = 100;
            _completed = true;
            _actionButton.Visible = true;
            _actionButton.Enabled = true;
            _actionButton.Text = "完成";
            _uninstallButton.Visible = false;
            _cancelButton.Enabled = true;
            _cancelButton.Text = "关闭";
        }
        catch (Exception)
        {
            _busy = false;
            UpdateDetails("卸载未完成，可以重试。扫描结果和个人文件继续保留。");
            _statusLabel.Text = "卸载未完成，请关闭画面扫描后重试。";
            _progressBar.Visible = false;
            _actionButton.Enabled = true;
            _actionButton.Visible = true;
            _uninstallButton.Enabled = true;
            _uninstallButton.Visible = true;
            _cancelButton.Enabled = true;
            _cancelButton.Visible = true;
            _cancelButton.Text = "关闭";
        }
    }
}
