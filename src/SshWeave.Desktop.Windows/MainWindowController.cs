using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using SshWeave.Configuration;
using SshWeave.Processes;
using SshWeave.Ssh;
using SshWeave.Telemetry;
using SshWeave.Tun;

namespace SshWeave.Desktop.Windows;

internal sealed class MainWindowController : IDisposable
{
    private static readonly string[] AuthenticationLabels = ["自动 / SSH Agent", "密码", "私钥文件"];
    private readonly string _configurationPath = ConfigurationStore.GetDefaultPath();
    private readonly ObservableCollection<SshProfile> _profiles;
    private readonly List<string> _logLines = [];
    private readonly DispatcherTimer _metricsTimer;
    private readonly ListBox _profileList;
    private readonly TextBox _nameBox;
    private readonly TextBox _hostBox;
    private readonly NumericUpDown _portBox;
    private readonly TextBox _userBox;
    private readonly ComboBox _authenticationBox;
    private readonly PasswordBox _secretBox;
    private readonly TextBox _identityBox;
    private readonly ComboBox _hostKeyPolicyBox;
    private readonly ToggleSwitch _socksEnabledToggle;
    private readonly NumericUpDown _socksPortBox;
    private readonly ToggleSwitch _compressionToggle;
    private readonly ToggleSwitch _transparentTcpToggle;
    private readonly MultiLineTextBox _tcpForwardsBox;
    private readonly MultiLineTextBox _transparentCidrsBox;
    private readonly MultiLineTextBox _logBox;
    private readonly MultiLineTextBox _capabilitiesBox;
    private readonly Label _statusLabel;
    private readonly Label _sentLabel;
    private readonly Label _receivedLabel;
    private readonly Label _activeConnectionsLabel;
    private readonly Label _totalConnectionsLabel;
    private readonly Label _endpointSummaryLabel;
    private readonly Button _connectButton;
    private readonly Button _disconnectButton;
    private readonly Button _saveButton;
    private readonly Button _deleteButton;
    private SshWeaveConfiguration _configuration;
    private SshProfile _selectedProfile;
    private ObservedSshSession? _session;
    private WindowsTrayIcon? _trayIcon;
    private bool _exitRequested;
    private bool _operationInProgress;

    public MainWindowController()
    {
        _configuration = LoadConfiguration();
        _profiles = new ObservableCollection<SshProfile>(_configuration.Profiles);
        _selectedProfile = _profiles[0];

        _profileList = new ListBox()
            .ItemHeight(44)
            .Items(_profiles, profile => $"{profile.Name}\n{profile.User}@{profile.Host}", profile => profile.Name)
            .OnSelectionChanged(item =>
            {
                if (item is SshProfile profile && !ReferenceEquals(profile, _selectedProfile))
                {
                    _selectedProfile = profile;
                    LoadEditor(profile);
                    _ = ProbeCapabilitiesAsync();
                }
            });

        _nameBox = new TextBox().Placeholder("station");
        _hostBox = new TextBox().Placeholder("bastion.example.com");
        _portBox = CreatePortBox(22);
        _userBox = new TextBox().Placeholder("operator");
        _authenticationBox = new ComboBox().Items(AuthenticationLabels);
        _secretBox = new PasswordBox().Placeholder("本次连接的密码或密钥口令");
        _identityBox = new TextBox().Placeholder("C:\\Users\\operator\\.ssh\\id_ed25519");
        _hostKeyPolicyBox = new ComboBox().Items(["首次固定 (accept-new)", "严格校验 (strict)"]);
        _socksEnabledToggle = new ToggleSwitch().Content("SOCKS5");
        _socksPortBox = CreatePortBox(1080);
        _compressionToggle = new ToggleSwitch().Content("压缩");
        _transparentTcpToggle = new ToggleSwitch().Content("透明 TCP");
        _tcpForwardsBox = new MultiLineTextBox
        {
            Placeholder = "127.0.0.1:2222=10.20.0.10:22",
            Wrap = false,
        };
        _transparentCidrsBox = new MultiLineTextBox
        {
            Placeholder = "10.51.0.0/16",
            Wrap = false,
        };
        _logBox = new MultiLineTextBox { IsReadOnly = true, Wrap = false };
        _capabilitiesBox = new MultiLineTextBox { IsReadOnly = true, Wrap = true };
        _statusLabel = new Label().Text("未连接").Bold();
        _sentLabel = MetricValue();
        _receivedLabel = MetricValue();
        _activeConnectionsLabel = MetricValue();
        _totalConnectionsLabel = MetricValue();
        _endpointSummaryLabel = new Label();
        _connectButton = new Button().Content("连接").OnClick(async () => await ConnectAsync());
        _disconnectButton = new Button().Content("断开").OnClick(async () => await DisconnectAsync());
        _saveButton = new Button().Content("保存").OnClick(async () => _ = await SaveSelectedProfileAsync());
        _deleteButton = new Button().Content("删除").OnClick(async () => await DeleteSelectedProfileAsync());

        Window = new Window()
            .Resizable(1180, 760, minWidth: 980, minHeight: 680)
            .Title("SshWeave")
            .Padding(0)
            .Content(BuildRoot())
            .OnLoaded(OnLoaded)
            .OnClosed(OnClosed);
        Window.Closing += eventArgs =>
        {
            if (!_exitRequested)
            {
                eventArgs.Cancel = true;
                Window.Hide();
            }
        };

        _profileList.SelectedItem = _selectedProfile;
        LoadEditor(_selectedProfile);
        UpdateControls();
        _metricsTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500));
        _metricsTimer.Tick += UpdateMetrics;
    }

    public Window Window { get; }

    public void Dispose()
    {
        _metricsTimer.Dispose();
        _trayIcon?.Dispose();
        if (_session is not null)
        {
            _session.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _session = null;
        }
    }

    private Grid BuildRoot() => new Grid()
        .Columns("260,*")
        .Children(
            BuildSidebar().Column(0),
            BuildWorkspace().Column(1));

    private Border BuildSidebar() => new Border()
        .Padding(16)
        .WithTheme((theme, border) => border.Background(theme.Palette.ControlBackground))
        .Child(new DockPanel()
            .Spacing(12)
            .Children(
                new StackPanel()
                    .DockTop()
                    .Vertical()
                    .Spacing(4)
                    .Children(
                        new Label().Text("SshWeave").FontSize(22).Bold(),
                        _statusLabel),

                new StackPanel()
                    .DockBottom()
                    .Vertical()
                    .Spacing(8)
                    .Children(
                        new Label().Text("主题").Bold(),
                        new StackPanel()
                            .Horizontal()
                            .Spacing(8)
                            .Children(
                                new RadioButton()
                                    .Content("系统")
                                    .GroupName("theme")
                                    .IsChecked()
                                    .OnChecked(() => Application.Current.SetThemeMode(ThemeVariant.System)),
                                new RadioButton()
                                    .Content("浅色")
                                    .GroupName("theme")
                                    .OnChecked(() => Application.Current.SetThemeMode(ThemeVariant.Light)),
                                new RadioButton()
                                    .Content("深色")
                                    .GroupName("theme")
                                    .OnChecked(() => Application.Current.SetThemeMode(ThemeVariant.Dark)))),

                new DockPanel()
                    .Spacing(8)
                    .Children(
                        new StackPanel()
                            .DockBottom()
                            .Horizontal()
                            .Spacing(8)
                            .Children(
                                new Button().Content("新建").OnClick(AddProfile),
                                _deleteButton),
                        new Label().DockTop().Text("账户与连接").Bold(),
                        _profileList)));

    private TabControl BuildWorkspace() => new TabControl()
        .Padding(16)
        .TabItems(
            new TabItem().Header("概览").Content(BuildOverview()),
            new TabItem().Header("连接设置").Content(BuildSettings()),
            new TabItem().Header("实时日志").Content(BuildLogs()),
            new TabItem().Header("权限与功能").Content(BuildCapabilities()));

    private ScrollViewer BuildOverview() => new ScrollViewer()
        .VerticalScroll(ScrollMode.Auto)
        .Content(new StackPanel()
            .Vertical()
            .Spacing(16)
            .Children(
                new DockPanel()
                    .Children(
                        new StackPanel()
                            .DockRight()
                            .Horizontal()
                            .Spacing(8)
                            .Children(_connectButton, _disconnectButton),
                        new StackPanel()
                            .Vertical()
                            .Spacing(4)
                            .Children(
                                new Label().Text("连接监控").FontSize(20).Bold(),
                                new Label().Text("OpenSSH 通道"))),
                new UniformGrid()
                    .Columns(4)
                    .Spacing(12)
                    .Children(
                        MetricPanel("上行流量", _sentLabel),
                        MetricPanel("下行流量", _receivedLabel),
                        MetricPanel("当前连接", _activeConnectionsLabel),
                        MetricPanel("累计连接", _totalConnectionsLabel)),
                new GroupBox()
                    .Header("当前入口")
                    .Content(_endpointSummaryLabel),
                new GroupBox()
                    .Header("最近事件")
                    .Content(new MultiLineTextBox
                    {
                        IsReadOnly = true,
                        Text = "等待连接。",
                        Height = 260,
                        Wrap = true,
                    }
                    .Apply(recentBox => _recentEventsBox = recentBox))));

    private MultiLineTextBox _recentEventsBox = null!;

    private ScrollViewer BuildSettings() => new ScrollViewer()
        .VerticalScroll(ScrollMode.Auto)
        .Content(new StackPanel()
            .Vertical()
            .Spacing(14)
            .Children(
                new Label().Text("账户与认证").FontSize(18).Bold(),
                Field("配置名称", _nameBox),
                Field("SSH 主机", _hostBox),
                Field("SSH 端口", _portBox),
                Field("登录用户", _userBox),
                Field("认证方式", _authenticationBox),
                Field("密码 / 密钥口令", _secretBox),
                Field("私钥文件", new Grid()
                    .Columns("*,40")
                    .Spacing(8)
                    .Children(
                        _identityBox,
                        new Button()
                            .Column(1)
                            .Content("...")
                            .ToolTip("选择私钥文件")
                            .OnClick(SelectIdentityFile))),
                Field("主机密钥", _hostKeyPolicyBox),
                new Label().Text("网络功能").FontSize(18).Bold(),
                Field("功能开关", new StackPanel()
                    .Horizontal()
                    .Spacing(16)
                    .Children(_socksEnabledToggle, _transparentTcpToggle, _compressionToggle)),
                Field("SOCKS5 端口", _socksPortBox),
                Field("透明 TCP 网段", _transparentCidrsBox.Height(88)),
                Field("TCP 映射", _tcpForwardsBox.Height(140)),
                new StackPanel()
                    .Horizontal()
                    .Right()
                    .Spacing(8)
                    .Children(
                        new Button().Content("检测").OnClick(async () => await ProbeCapabilitiesAsync()),
                        _saveButton)));

    private DockPanel BuildLogs() => new DockPanel()
        .Spacing(12)
        .Children(
            new DockPanel()
                .DockTop()
                .Children(
                    new Button().DockRight().Content("清空").OnClick(ClearLogs),
                    new Label().Text("交互与网络日志").FontSize(18).Bold()),
            _logBox);

    private DockPanel BuildCapabilities() => new DockPanel()
        .Spacing(12)
        .Children(
            new DockPanel()
                .DockTop()
                .Children(
                    new Button().DockRight().Content("重新检测").OnClick(async () => await ProbeCapabilitiesAsync()),
                    new Label().Text("权限与可用功能").FontSize(18).Bold()),
            _capabilitiesBox);

    private static Grid Field(string label, FrameworkElement control) => new Grid()
        .Columns("170,*")
        .Spacing(12)
        .Children(
            new Label().Text(label).CenterVertical(),
            control.Column(1));

    private static GroupBox MetricPanel(string title, Label value) => new GroupBox()
        .Header(title)
        .Content(value);

    private static Label MetricValue() => new Label().Text("0").FontSize(22).Bold();

    private static NumericUpDown CreatePortBox(int value) => new()
    {
        Minimum = 1,
        Maximum = 65535,
        IsInteger = true,
        Step = 1,
        Value = value,
    };

    private SshWeaveConfiguration LoadConfiguration()
    {
        if (File.Exists(_configurationPath))
        {
            try
            {
                SshWeaveConfiguration loaded = ConfigurationStore.LoadAsync(_configurationPath)
                    .GetAwaiter()
                    .GetResult();
                ConfigurationValidator.Validate(loaded);
                if (loaded.Profiles.Count > 0)
                {
                    return loaded;
                }
            }
            catch (Exception exception) when (exception is ConfigurationException or IOException)
            {
                _logLines.Add($"[{DateTime.Now:HH:mm:ss}] [配置] {exception.Message}");
            }
        }

        SshProfile profile = CreateDefaultProfile("station");
        return new SshWeaveConfiguration
        {
            DefaultProfile = profile.Name,
            Profiles = [profile],
        };
    }

    private static SshProfile CreateDefaultProfile(string name) => new()
    {
        Name = name,
        Host = "bastion.example.com",
        User = "operator",
        AuthenticationMode = AuthenticationModes.Auto,
        BatchMode = true,
        Socks = new SocksForward(),
        TcpForwards = [],
    };

    private void LoadEditor(SshProfile profile)
    {
        _nameBox.Text = profile.Name;
        _hostBox.Text = profile.Host;
        _portBox.Value = profile.Port;
        _userBox.Text = profile.User;
        _authenticationBox.SelectedIndex = profile.AuthenticationMode switch
        {
            AuthenticationModes.Password => 1,
            AuthenticationModes.KeyFile => 2,
            _ => 0,
        };
        _secretBox.Password = string.Empty;
        _identityBox.Text = profile.IdentityFile ?? string.Empty;
        _hostKeyPolicyBox.SelectedIndex = profile.HostKeyPolicy == HostKeyPolicies.Strict ? 1 : 0;
        _socksEnabledToggle.IsChecked = profile.Socks is not null;
        _socksPortBox.Value = profile.Socks?.Port ?? 1080;
        _compressionToggle.IsChecked = profile.Compression;
        TransparentTcpRoute transparentTcp = profile.TransparentTcp ?? new TransparentTcpRoute();
        _transparentTcpToggle.IsChecked = transparentTcp.Enabled;
        _transparentCidrsBox.Text = transparentTcp.DestinationCidrs.Count == 0
            ? "10.51.0.0/16"
            : string.Join(Environment.NewLine, transparentTcp.DestinationCidrs);
        _tcpForwardsBox.Text = FormatTcpForwards(profile.TcpForwards);
        RefreshEndpointSummary();
    }

    private async Task<bool> SaveSelectedProfileAsync()
    {
        try
        {
            SshProfile updated = ReadEditor();
            int index = _profiles.IndexOf(_selectedProfile);
            if (_profiles.Where((_, itemIndex) => itemIndex != index)
                .Any(profile => string.Equals(profile.Name, updated.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ConfigurationException($"连接配置名称重复：{updated.Name}。");
            }

            _profiles[index] = updated;
            _selectedProfile = updated;
            _profileList.SelectedItem = updated;
            _configuration.Profiles = [.. _profiles];
            _configuration.DefaultProfile = updated.Name;
            await ConfigurationStore.SaveAsync(_configurationPath, _configuration);
            AppendLog(new SessionLogEntry(
                DateTimeOffset.Now,
                SessionLogCategory.Lifecycle,
                $"配置 {updated.Name} 已保存。"));
            RefreshEndpointSummary();
            await ProbeCapabilitiesAsync();
            return true;
        }
        catch (Exception exception) when (exception is ConfigurationException or IOException or UnauthorizedAccessException)
        {
            ShowError(exception.Message);
            return false;
        }
    }

    private SshProfile ReadEditor()
    {
        string authentication = _authenticationBox.SelectedIndex switch
        {
            1 => AuthenticationModes.Password,
            2 => AuthenticationModes.KeyFile,
            _ => AuthenticationModes.Auto,
        };
        SshProfile profile = new()
        {
            Name = _nameBox.Text.Trim(),
            Host = _hostBox.Text.Trim(),
            Port = (int)_portBox.Value,
            User = _userBox.Text.Trim(),
            AuthenticationMode = authentication,
            IdentityFile = authentication == AuthenticationModes.KeyFile
                ? NullIfWhiteSpace(_identityBox.Text)
                : null,
            KnownHostsFile = _selectedProfile.KnownHostsFile,
            HostKeyPolicy = _hostKeyPolicyBox.SelectedIndex == 1
                ? HostKeyPolicies.Strict
                : HostKeyPolicies.AcceptNew,
            BatchMode = authentication == AuthenticationModes.Auto,
            Compression = _compressionToggle.IsChecked,
            AllowRemoteClients = false,
            ConnectTimeoutSeconds = _selectedProfile.ConnectTimeoutSeconds,
            StartupTimeoutSeconds = _selectedProfile.StartupTimeoutSeconds,
            ServerAliveIntervalSeconds = _selectedProfile.ServerAliveIntervalSeconds,
            ServerAliveCountMax = _selectedProfile.ServerAliveCountMax,
            Socks = _socksEnabledToggle.IsChecked
                ? new SocksForward { ListenAddress = "127.0.0.1", Port = (int)_socksPortBox.Value }
                : null,
            TcpForwards = ParseTcpForwards(_tcpForwardsBox.Text),
            TransparentTcp = new TransparentTcpRoute
            {
                Enabled = _transparentTcpToggle.IsChecked,
                AdapterName = _selectedProfile.TransparentTcp?.AdapterName ?? "SshWeave",
                AdapterIpv4Cidr = _selectedProfile.TransparentTcp?.AdapterIpv4Cidr ?? "198.18.0.1/30",
                Mtu = _selectedProfile.TransparentTcp?.Mtu ?? 1500,
                RouteMetric = _selectedProfile.TransparentTcp?.RouteMetric ?? 5,
                DestinationCidrs = ParseCidrs(_transparentCidrsBox.Text),
            },
        };

        SshWeaveConfiguration validationConfiguration = new()
        {
            DefaultProfile = profile.Name,
            Profiles = [profile],
        };
        ConfigurationValidator.Validate(validationConfiguration);
        return profile;
    }

    private void AddProfile()
    {
        int suffix = 1;
        string name;
        do
        {
            name = $"connection-{suffix++}";
        }
        while (_profiles.Any(profile => string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase)));

        SshProfile profile = CreateDefaultProfile(name);
        _profiles.Add(profile);
        _selectedProfile = profile;
        _profileList.SelectedItem = profile;
        LoadEditor(profile);
        UpdateControls();
    }

    private async Task DeleteSelectedProfileAsync()
    {
        if (_profiles.Count == 1)
        {
            ShowError("至少保留一个连接配置。");
            return;
        }

        int index = _profiles.IndexOf(_selectedProfile);
        _profiles.RemoveAt(index);
        _selectedProfile = _profiles[Math.Min(index, _profiles.Count - 1)];
        _profileList.SelectedItem = _selectedProfile;
        LoadEditor(_selectedProfile);
        _configuration.Profiles = [.. _profiles];
        _configuration.DefaultProfile = _selectedProfile.Name;
        await ConfigurationStore.SaveAsync(_configurationPath, _configuration);
        UpdateControls();
    }

    private async Task ConnectAsync()
    {
        if (_session is not null || _operationInProgress)
        {
            return;
        }

        _operationInProgress = true;
        UpdateControls();
        try
        {
            if (!await SaveSelectedProfileAsync())
            {
                return;
            }
            string? secret = NullIfWhiteSpace(_secretBox.Password);
            if (_selectedProfile.AuthenticationMode == AuthenticationModes.Password && secret is null)
            {
                throw new ConfigurationException("密码认证需要输入本次连接密码。");
            }

            string executable = Environment.ProcessPath
                ?? throw new InvalidOperationException("无法确定 SSH_ASKPASS 可执行文件路径。");
            _statusLabel.Text = "正在连接";
            ObservedSshSession session = await ObservedSshSession.StartAsync(
                _configuration,
                _selectedProfile,
                executable,
                secret);
            _secretBox.Password = string.Empty;
            _session = session;
            foreach (SessionLogEntry entry in session.LogEntries)
            {
                AppendLog(entry);
            }
            session.LogReceived += OnSessionLog;
            session.StateChanged += OnSessionStateChanged;
            _statusLabel.Text = "已连接";
            _trayIcon?.SetTooltip($"SshWeave - {_selectedProfile.Name} 已连接");
            _metricsTimer.Start();
        }
        catch (Exception exception) when (exception is ConfigurationException or IOException or InvalidOperationException)
        {
            _statusLabel.Text = "连接失败";
            AppendLog(new SessionLogEntry(
                DateTimeOffset.Now,
                SessionLogCategory.Lifecycle,
                exception.Message,
                IsError: true));
            ShowError(exception.Message);
        }
        finally
        {
            _operationInProgress = false;
            UpdateControls();
        }
    }

    private async Task DisconnectAsync()
    {
        if (_session is null || _operationInProgress)
        {
            return;
        }

        _operationInProgress = true;
        UpdateControls();
        try
        {
            ObservedSshSession session = _session;
            _session = null;
            session.LogReceived -= OnSessionLog;
            session.StateChanged -= OnSessionStateChanged;
            await session.DisposeAsync();
            _statusLabel.Text = "未连接";
            _trayIcon?.SetTooltip("SshWeave - 未连接");
        }
        finally
        {
            _operationInProgress = false;
            UpdateControls();
            UpdateMetrics();
        }
    }

    private async Task ProbeCapabilitiesAsync()
    {
        try
        {
            SshProfile profile = ReadEditor();
            StringBuilder result = new();
            result.AppendLine("✓ Windows 桌面宿主");
            result.AppendLine("✓ Windows 右键托盘");

            ProcessResult version = await ProcessExecutor.RunCapturedAsync(_configuration.SshExecutable, ["-V"]);
            if (version.Succeeded)
            {
                string versionText = string.IsNullOrWhiteSpace(version.StandardError)
                    ? version.StandardOutput.Trim()
                    : version.StandardError.Trim();
                result.AppendLine($"✓ 系统 OpenSSH：{versionText}");
            }
            else
            {
                result.AppendLine("✗ 系统 OpenSSH 不可用");
            }

            try
            {
                bool transparentEnabled = profile.TransparentTcp.Enabled;
                try
                {
                    profile.TransparentTcp.Enabled = false;
                    await SshConnectionService.CheckAsync(_configuration, profile);
                }
                finally
                {
                    profile.TransparentTcp.Enabled = transparentEnabled;
                }
                result.AppendLine("✓ OpenSSH 参数与本地端口权限");
            }
            catch (Exception exception) when (exception is ConfigurationException or IOException)
            {
                result.AppendLine($"✗ OpenSSH 配置：{exception.Message}");
            }

            if (profile.TransparentTcp.Enabled)
            {
                try
                {
                    await TransparentTcpSession.CheckAsync(
                        _configuration,
                        profile,
                        requireElevation: false);
                    result.AppendLine(
                        $"✓ {TransparentTcpSession.SupportedTun2SocksVersion} / "
                        + $"Wintun {TransparentTcpSession.SupportedWintunVersion}");
                }
                catch (Exception exception) when (exception is ConfigurationException or IOException)
                {
                    result.AppendLine($"✗ 透明 TCP 数据面：{exception.Message}");
                }
                result.AppendLine(Environment.IsPrivilegedProcess
                    ? "✓ 透明 TCP 管理员权限"
                    : "✗ 透明 TCP 需要以管理员身份重新启动");
            }
            else
            {
                result.AppendLine("○ 透明 TCP 已关闭，可启用");
            }

            result.AppendLine(profile.AuthenticationMode switch
            {
                AuthenticationModes.Password => "✓ 密码登录已启用，口令不落盘",
                AuthenticationModes.KeyFile when File.Exists(profile.IdentityFile) => "✓ 私钥文件可读，口令不落盘",
                AuthenticationModes.KeyFile => "✗ 私钥文件不可读",
                _ => "✓ SSH Agent / 系统默认认证已启用",
            });
            result.AppendLine(profile.Socks is null ? "○ SOCKS5 已关闭，可启用" : "✓ SOCKS5 已启用");
            result.AppendLine(profile.TcpForwards.Count == 0
                ? "○ TCP 映射已关闭，可添加"
                : $"✓ TCP 映射已启用：{profile.TcpForwards.Count} 条");
            result.AppendLine(profile.Compression ? "✓ SSH 压缩已启用" : "○ SSH 压缩已关闭，可启用");
            result.AppendLine("○ 远端系统用户写操作未启用");
            _capabilitiesBox.Text = result.ToString().TrimEnd();
        }
        catch (Exception exception) when (exception is ConfigurationException or IOException or UnauthorizedAccessException)
        {
            _capabilitiesBox.Text = $"✗ {exception.Message}";
        }
    }

    private void OnLoaded()
    {
        _trayIcon = new WindowsTrayIcon(
            ShowWindow,
            () => _ = _session is null ? ConnectAsync() : DisconnectAsync(),
            () => _ = ExitAsync(),
            () => _session?.State == ObservedSessionState.Connected);
        _trayIcon.Install("SshWeave - 未连接");
        _metricsTimer.Start();
        _ = ProbeCapabilitiesAsync();
    }

    private void OnClosed()
    {
        _metricsTimer.Stop();
        _trayIcon?.Dispose();
        _trayIcon = null;
    }

    private void ShowWindow()
    {
        Window.Show();
        Window.Restore();
        Window.Activate();
    }

    private async Task ExitAsync()
    {
        _exitRequested = true;
        if (_session is not null)
        {
            await DisconnectAsync();
        }
        _trayIcon?.Dispose();
        Window.Close();
    }

    private void OnSessionLog(SessionLogEntry entry)
    {
        Application.Current.Dispatcher?.BeginInvoke(() => AppendLog(entry));
    }

    private void OnSessionStateChanged(ObservedSessionState state)
    {
        Application.Current.Dispatcher?.BeginInvoke(() =>
        {
            _statusLabel.Text = state switch
            {
                ObservedSessionState.Connected => "已连接",
                ObservedSessionState.Failed => "连接中断",
                ObservedSessionState.Stopping => "正在断开",
                _ => "未连接",
            };
            UpdateControls();
        });
    }

    private void AppendLog(SessionLogEntry entry)
    {
        string category = entry.Category switch
        {
            SessionLogCategory.Authentication => "认证",
            SessionLogCategory.Network => "网络",
            SessionLogCategory.OpenSsh => "SSH",
            _ => "会话",
        };
        string marker = entry.IsError ? "错误" : category;
        _logLines.Add($"[{entry.Timestamp:HH:mm:ss}] [{marker}] {entry.Message}");
        if (_logLines.Count > 500)
        {
            _logLines.RemoveRange(0, _logLines.Count - 500);
        }
        string text = string.Join(Environment.NewLine, _logLines);
        _logBox.Text = text;
        _recentEventsBox.Text = string.Join(Environment.NewLine, _logLines.TakeLast(8));
    }

    private void ClearLogs()
    {
        _logLines.Clear();
        _logBox.Text = string.Empty;
        _recentEventsBox.Text = "等待新事件。";
    }

    private void UpdateMetrics()
    {
        TrafficSnapshot snapshot = _session?.Metrics.Snapshot() ?? new TrafficSnapshot(0, 0, 0, 0);
        _sentLabel.Text = FormatBytes(snapshot.BytesSent);
        _receivedLabel.Text = FormatBytes(snapshot.BytesReceived);
        _activeConnectionsLabel.Text = snapshot.ActiveConnections.ToString(CultureInfo.InvariantCulture);
        _totalConnectionsLabel.Text = snapshot.TotalConnections.ToString(CultureInfo.InvariantCulture);
    }

    private void UpdateControls()
    {
        bool connected = _session is not null;
        bool editorEnabled = !connected && !_operationInProgress;
        _connectButton.IsEnabled = !connected && !_operationInProgress;
        _disconnectButton.IsEnabled = connected && !_operationInProgress;
        _saveButton.IsEnabled = editorEnabled;
        _deleteButton.IsEnabled = editorEnabled && _profiles.Count > 1;
        _profileList.IsEnabled = editorEnabled;
        _nameBox.IsEnabled = editorEnabled;
        _hostBox.IsEnabled = editorEnabled;
        _portBox.IsEnabled = editorEnabled;
        _userBox.IsEnabled = editorEnabled;
        _authenticationBox.IsEnabled = editorEnabled;
        _identityBox.IsEnabled = editorEnabled;
        _hostKeyPolicyBox.IsEnabled = editorEnabled;
        _socksEnabledToggle.IsEnabled = editorEnabled;
        _socksPortBox.IsEnabled = editorEnabled;
        _compressionToggle.IsEnabled = editorEnabled;
        _transparentTcpToggle.IsEnabled = editorEnabled;
        _transparentCidrsBox.IsEnabled = editorEnabled;
        _tcpForwardsBox.IsEnabled = editorEnabled;
    }

    private void RefreshEndpointSummary()
    {
        // 概览文本由连接编辑器派生，不读取或展示任何认证秘密。
        Window.Title = $"SshWeave - {_selectedProfile.Name}";
        _endpointSummaryLabel.Text = BuildEndpointSummary();
    }

    private string BuildEndpointSummary()
    {
        List<string> endpoints = [];
        if (_selectedProfile.Socks is not null)
        {
            endpoints.Add($"SOCKS5  {_selectedProfile.Socks.ListenAddress}:{_selectedProfile.Socks.Port}");
        }
        endpoints.AddRange(_selectedProfile.TcpForwards.Select(forward =>
            $"TCP  {forward.ListenAddress}:{forward.LocalPort} → {forward.DestinationHost}:{forward.DestinationPort}"));
        if (_selectedProfile.TransparentTcp?.Enabled == true)
        {
            endpoints.Add($"TUN  {string.Join(", ", _selectedProfile.TransparentTcp.DestinationCidrs)}");
        }
        return endpoints.Count == 0 ? "未配置" : string.Join(Environment.NewLine, endpoints);
    }

    private void SelectIdentityFile()
    {
        string? selected = FileDialog.OpenFile(new OpenFileDialogOptions
        {
            Owner = Window,
            Filters = FileFilter.Parse("SSH 私钥 (*.*)|*.*"),
        });
        if (selected is not null)
        {
            _identityBox.Text = selected;
            _authenticationBox.SelectedIndex = 2;
        }
    }

    private void ShowError(string message) => NativeMessageBox.Show(
        Window.Handle,
        message,
        "SshWeave",
        NativeMessageBoxButtons.Ok,
        NativeMessageBoxIcon.Error);

    private static List<TcpForward> ParseTcpForwards(string value)
    {
        List<TcpForward> forwards = [];
        foreach (string rawLine in value.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            string[] sides = line.Split('=', 2, StringSplitOptions.TrimEntries);
            if (sides.Length != 2)
            {
                throw new ConfigurationException($"TCP 映射格式无效：{line}");
            }

            (string listenHost, int listenPort) = ParseEndpoint(sides[0]);
            (string destinationHost, int destinationPort) = ParseEndpoint(sides[1]);
            forwards.Add(new TcpForward
            {
                ListenAddress = listenHost,
                LocalPort = listenPort,
                DestinationHost = destinationHost,
                DestinationPort = destinationPort,
            });
        }
        return forwards;
    }

    private static List<string> ParseCidrs(string value) => value
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToList();

    private static (string Host, int Port) ParseEndpoint(string value)
    {
        string host;
        string portText;
        if (value.StartsWith('['))
        {
            int closingBracket = value.IndexOf(']');
            if (closingBracket < 0 || closingBracket + 2 > value.Length || value[closingBracket + 1] != ':')
            {
                throw new ConfigurationException($"端点格式无效：{value}");
            }
            host = value[1..closingBracket];
            portText = value[(closingBracket + 2)..];
        }
        else
        {
            int separator = value.LastIndexOf(':');
            if (separator <= 0)
            {
                throw new ConfigurationException($"端点格式无效：{value}");
            }
            host = value[..separator];
            portText = value[(separator + 1)..];
        }

        if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out int port)
            || port is < 1 or > 65535)
        {
            throw new ConfigurationException($"端点端口无效：{value}");
        }
        return (host, port);
    }

    private static string FormatTcpForwards(IEnumerable<TcpForward> forwards) => string.Join(
        Environment.NewLine,
        forwards.Select(forward =>
            $"{FormatHost(forward.ListenAddress)}:{forward.LocalPort}={FormatHost(forward.DestinationHost)}:{forward.DestinationPort}"));

    private static string FormatHost(string host) => host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host;

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double amount = value;
        int unit = 0;
        while (amount >= 1024 && unit < units.Length - 1)
        {
            amount /= 1024;
            unit++;
        }
        return $"{amount:0.#} {units[unit]}";
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
