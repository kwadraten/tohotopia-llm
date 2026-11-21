using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Tohotopia;
using TohotopiaShared;

namespace Settings;

public sealed partial class SettingsWindow : Window
{
    readonly Select provider = new() { Name = "AuthProvider" }, mapFormat = new() { Name = "MapFormat" };
    readonly NumberField timeout = new() { Name = "Timeout", Minimum = 10, Maximum = 600, Value = 120, Step = 1, MaximumFractionDigits = 0, UseGrouping = false, MaxWidth = 220, HorizontalAlignment = HorizontalAlignment.Left };
    readonly NumberField budget = new() { Name = "ActionBudget", Minimum = 1, Maximum = 200, Value = 80, Step = 1, MaximumFractionDigits = 0, UseGrouping = false, MaxWidth = 220, HorizontalAlignment = HorizontalAlignment.Left };
    readonly TextBox callback = new Input { Name = "AuthCallback", PasswordChar = '●' };
    readonly TextBox rules = Ui.Text(multiline: true, height: 240);
    readonly Alert status = Ui.StatusAlert("AuthStatus"), globalStatus = Ui.StatusAlert("GlobalStatus"), accountPreference = Ui.StatusAlert("AccountPreferenceStatus");
    readonly Table accounts = Ui.Records("已保存提供商", 220);
    readonly Button browser = new() { Content = "打开授权网页", IsEnabled = false, Variant = ButtonVariant.Secondary }, login = new() { Content = "登录订阅账号" }, subscriptionLogin = new() { Content = "登录订阅账号", Variant = ButtonVariant.Secondary, IsVisible = false }, complete = new() { Content = "提交授权", Variant = ButtonVariant.Secondary };
    TextBlock? callbackCaption;
    readonly DispatcherTimer poll = new() { Interval = TimeSpan.FromSeconds(2) };
    readonly HttpClient http;
    readonly Action<string, AccountUi> accountPreferenceWriter;
    readonly Func<string, ProviderAuthMethod, CredentialInfo?, bool, ApiKeyDialog> apiKeyDialogFactory;
    readonly HashSet<string> removingAccounts = new(StringComparer.OrdinalIgnoreCase);
    List<AccountStatus> accountStates = new();
    AccountStatus? selectedAccount;
    Task providerChange = Task.CompletedTask;
    string loginId = "", authUrl = "";
    int accountsVersion, loginVersion;
    bool loading, closed, exiting, credentialDialogOpen, loginStarting, polling, completing;
    internal bool IsClosed => closed;
    internal Task ProviderChange => providerChange;

    public SettingsWindow() : this(null) { }
    internal SettingsWindow(HttpClient? client, bool initializeOnOpen = true, Action<string, AccountUi>? accountPreferenceWriter = null,
        Func<string, ProviderAuthMethod, CredentialInfo?, bool, ApiKeyDialog>? apiKeyDialogFactory = null)
    {
        http = client ?? new HttpClient { BaseAddress = new Uri("http://127.0.0.1:17861"), Timeout = TimeSpan.FromSeconds(35) };
        this.accountPreferenceWriter = accountPreferenceWriter ?? ((root, value) => TohotopiaShared.RoomFiles.Write(root, "account-ui.json", value));
        this.apiKeyDialogFactory = apiKeyDialogFactory ?? ((name, method, credential, editing) => new ApiKeyDialog(name, method, credential, editing));
        Title = "帝国幻想乡 · AI 接管设置";
        Width = 1000; Height = 760; MinWidth = 760; MinHeight = 480;
        FontFamily = new FontFamily(Ui.Fonts);
        FontSize = 14;
        rules.Name = "ExtraRules";
        accountPreference.IsOpen = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Files.Root = TohotopiaShared.RuntimePaths.PrepareRoot(Environment.GetEnvironmentVariable("TOHOTOPIA_LLM_DATA"));
        Directory.CreateDirectory(Files.Root);
        var auth = Ui.Form();
        provider.TextSelector = p => (p as CatalogProvider)?.Name;
        var providerRow = Ui.Row(auth, "提供商", provider);
        providerRow.IsVisible = false;
        provider.IsVisible = false;
        Ui.Column<AccountRow>(accounts, "提供商", a => a.Provider);
        Ui.Column<AccountRow>(accounts, "连接方式", a => a.Auth);
        Ui.Column<AccountRow>(accounts, "状态", a => a.State);
        Ui.Column<AccountRow>(accounts, "有效至", a => a.Expires, 1.4);
        Ui.Column<AccountRow>(accounts, "令牌刷新", a => a.Refresh);
        accounts.Columns.Add(new DataTableColumn("账号操作", _ => "") { Width = new GridLength(1.8, GridUnitType.Star), IsSortable = false,
            TextWrapping = TextWrapping.Wrap, CellTemplate = new FuncDataTemplate<AccountRow>((row, _) => AccountActions(row)) });
        Ui.Row(auth, "已保存提供商", accounts);
        Ui.Row(auth, "", accountPreference);
        complete.Click += (_, _) => AccountEvent(Complete);
        callbackCaption = Ui.Row(auth, "授权回调／代码", callback);
        var check = Ui.Button("检查／刷新凭据", () => AccountEvent(RefreshCredentials));
        var reload = Ui.Button("刷新账号状态", () => AccountEvent(RefreshAccounts));
        var global = Ui.Form();
        mapFormat.ItemsSource = new[] { "table", "json" };
        Ui.Row(global, "地图观测", mapFormat);
        Ui.Row(global, "请求超时（秒）", timeout);
        Ui.Row(global, "每回合动作上限", budget);
        Ui.Row(global, "追加游戏规则", rules);
        Ui.Row(global, "保存状态", globalStatus);
        var saveGlobal = Ui.Button("保存全局规则", () => AccountEvent(Save), ButtonVariant.Primary);
        var pages = new Control[] { BuildRoomPage(), Ui.Page(Ui.Section("连接提供商", auth), Ui.Section("授权与连接状态", status)), Ui.Page(Ui.Section("全局规则", global)), BuildLogPage() };
        var ribbon = new Ribbon { Name = "SettingsTabs", Margin = new Thickness(16, 8, 8, 0), IsMinimized = false };
        ribbon.Tabs.Add(Ui.RibbonPage("AI 接管", "接管控制", RoomCommands()));
        ribbon.Tabs.Add(Ui.RibbonPage("账号管理", "账号操作", login, subscriptionLogin, browser, complete, check, reload));
        ribbon.Tabs.Add(Ui.RibbonPage("全局规则", "规则", saveGlobal));
        ribbon.Tabs.Add(Ui.RibbonPage("日志", "日志操作", LogCommands()));
        ribbon.PropertyChanged += (_, e) =>
        {
            if (e.Property == Ribbon.IsMinimizedProperty && ribbon.IsMinimized) ribbon.IsMinimized = false;
        };
        var host = new Grid { Margin = new Thickness(16, 8, 8, 0), MaxWidth = Ui.PageWidth + 24, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var page in pages) host.Children.Add(page);
        void ShowPage(int index)
        {
            for (int i = 0; i < pages.Length; i++) pages[i].IsVisible = i == index;
        }
        ribbon.SelectionChanged += (_, _) => ShowPage(ribbon.SelectedIndex);
        ribbon.SelectedIndex = 0;
        ShowPage(0);
        Grid.SetRow(host, 1);
        Content = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Children = { ribbon, host } };
        provider.SelectionChanged += (_, _) =>
        {
            if (loading || closed) return;
            providerChange = RunAccountEvent(ProviderChanged);
        };
        login.Click += (_, _) => AccountEvent(() => ApiKeyMethod() is not null ? ConfigureApiKey() : Login());
        subscriptionLogin.Click += (_, _) => AccountEvent(() => Login());
        browser.Click += (_, _) => AccountEvent(async () =>
        {
            if (Uri.TryCreate(authUrl, UriKind.Absolute, out var uri) && uri.Scheme == "https") await Launcher.LaunchUriAsync(uri);
        });
        poll.Tick += (_, _) => AccountEvent(Poll);
        Opened += (_, _) => AccountEvent(async () =>
        {
            FitToScreen();
            if (initializeOnOpen)
            {
                await Initialize();
                if (!closed && Environment.GetCommandLineArgs().Contains("--login"))
                {
                    if (ApiKeyMethod() is not null) await ConfigureApiKey(); else await Login();
                }
            }
            if (!closed) ReadLogs();
        });
        Closed += (_, _) => StopServices();
        Program.Attach(this);
    }
    void AccountEvent(Func<Task> action) => _ = RunAccountEvent(action);
    async Task RunAccountEvent(Func<Task> action)
    {
        try
        {
            if (!closed) await action();
        }
        catch (Exception e)
        {
            if (!closed) SetAccountStatus("账号操作失败：" + e.Message, AlertTone.Danger, "账号操作失败");
        }
    }
    void SetAccountStatus(string message, AlertTone tone = AlertTone.Info, string title = "账号与操作状态")
    {
        if (closed) return;
        status.Title = title;
        Ui.Status(status, message, tone);
    }
    void FitToScreen()
    {
        if (Screens.ScreenFromWindow(this) is not { } screen) return;
        var area = screen.WorkingArea;
        double scale = screen.Scaling, w = area.Width / scale, h = area.Height / scale;
        MinWidth = Math.Min(MinWidth, w); MinHeight = Math.Min(MinHeight, h);
        if (Width > w || Height > h)
        {
            Width = Math.Min(Width, w); Height = Math.Min(Height, h);
            Position = new Avalonia.PixelPoint(area.X + (int)((w - Width) * scale / 2), area.Y + (int)((h - Height) * scale / 2));
        }
    }
    // Brings the single instance back to the foreground (pipe activation and
    // post-show fallback); kept under the former name for repeated launches.
    internal void Present()
    {
        if (closed) return;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }
    internal void Exit()
    {
        if (closed || exiting) return;
        exiting = true;
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
        else Close();
    }
    internal void StopServices()
    {
        if (closed) return;
        closed = true;
        loginVersion++;
        accountsVersion++;
        poll.Stop(); roomPoll.Stop(); logPoll.Stop();
        http.Dispose();
    }
    internal sealed record AccountRow(string Id, string Provider, string AuthType, string Auth, string State, string Expires, string Refresh);
    Control AccountActions(AccountRow? row) => new AccountActionCell(this) { DataContext = row };
    sealed class AccountActionCell : WrapPanel
    {
        readonly Button edit, reauthorize, ping, remove;
        internal AccountActionCell(SettingsWindow owner)
        {
            edit = Ui.Button("编辑", () => Invoke(owner, row => owner.ConfigureApiKey(row.Id)));
            reauthorize = Ui.Button("重新授权", () => Invoke(owner, row => owner.Login(row.Id)));
            ping = Ui.Button("连通性测试", () => Invoke(owner, owner.TestConnectivity));
            remove = Ui.Button("移除", () => Invoke(owner, owner.RemoveAccount), ButtonVariant.Danger);
            edit.Name = "AccountEdit"; reauthorize.Name = "AccountReauthorize"; ping.Name = "AccountPing"; remove.Name = "AccountRemove";
            foreach (var button in new[] { edit, reauthorize, ping, remove })
            {
                button.Size = ControlSize.Small;
                button.Margin = new Thickness(0, 0, 6, 4);
                Children.Add(button);
            }
            Update();
        }
        void Invoke(SettingsWindow owner, Func<AccountRow, Task> action)
        {
            if (DataContext is AccountRow row) owner.AccountEvent(() => action(row));
        }
        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            Update();
        }
        void Update()
        {
            var row = DataContext as AccountRow;
            edit.IsVisible = row?.AuthType == "api_key";
            reauthorize.IsVisible = row is not null && row.AuthType != "api_key";
            ping.IsVisible = row is not null;
            remove.IsVisible = row is not null;
        }
    }
    async Task TestConnectivity(AccountRow row)
    {
        if (closed) return;
        SetAccountStatus($"正在测试 {row.Provider} 的连接：向模型发送 “Ping! Just answer me ‘Pong!’”……", AlertTone.Info, "连通性测试");
        try
        {
            using var r = await http.PostAsync("/auth/" + Uri.EscapeDataString(row.Id) + "/ping", null);
            if (!r.IsSuccessStatusCode)
                throw new InvalidOperationException(await r.Content.ReadAsStringAsync());
            var result = await r.Content.ReadFromJsonAsync(SettingsJson.Type<PingResult>());
            if (closed || result is null) return;
            await ShowPingResult(row, result);
            SetAccountStatus(result.Success
                    ? $"{row.Provider} 连通性正常：模型 {result.Model} 在 {result.ElapsedMs}ms 内回复。"
                    : $"{row.Provider} 连通性测试失败：{result.Error}",
                result.Success ? AlertTone.Success : AlertTone.Danger, "连通性测试");
        }
        catch (Exception e)
        {
            if (!closed) SetAccountStatus($"{row.Provider} 连通性测试未能执行：{e.Message}", AlertTone.Danger, "连通性测试");
        }
    }
    async Task ShowPingResult(AccountRow row, PingResult result)
    {
        var dialog = new Window
        {
            Title = $"连通性测试 — {row.Provider}", Width = 640, SizeToContent = SizeToContent.Height,
            MinHeight = 280, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false
        };
        string text = result.Success
            ? $"提供商：{result.Provider}\r\n模型：{result.Model}\r\n耗时：{result.ElapsedMs}ms\r\n指令：Ping! Just answer me ‘Pong!’\r\n\r\n模型回复：\r\n{result.Reply}"
            : $"提供商：{result.Provider}\r\n模型：{result.Model}\r\n耗时：{result.ElapsedMs}ms\r\n指令：Ping! Just answer me ‘Pong!’\r\n\r\n错误详情：\r\n{result.Error}";
        var content = Ui.Text(readOnly: true, multiline: true);
        content.Name = "PingResultText";
        content.Text = text;
        var close = Ui.Button("关闭", dialog.Close, ButtonVariant.Primary);
        close.IsDefault = true;
        close.IsCancel = true;
        close.HorizontalAlignment = HorizontalAlignment.Right;
        dialog.Content = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            Margin = new Thickness(20),
            Children = { content, close }
        };
        Grid.SetRow(close, 1);
        await dialog.ShowDialog(this);
    }
    string SelectedProvider() => (provider.SelectedItem as CatalogProvider)?.Id ?? "openai";
    bool SelectProvider(string providerId)
    {
        string catalogId = CatalogProviderId(providerId);
        var selected = catalogProviders.FirstOrDefault(item => item.Id == catalogId);
        if (selected is null)
        {
            SetAccountStatus("模型目录中找不到该提供商，请刷新后重试。", AlertTone.Danger, "无法选择提供商");
            return false;
        }
        if (!ReferenceEquals(provider.SelectedItem, selected)) provider.SelectedItem = selected;
        return true;
    }
    void SelectProviderSilently(string providerId)
    {
        string catalogId = CatalogProviderId(providerId);
        var selected = catalogProviders.FirstOrDefault(item => item.Id == catalogId);
        if (selected is null) return;
        loading = true;
        try { provider.SelectedItem = selected; }
        finally { loading = false; }
        try
        {
            accountPreferenceWriter(Files.Root, new AccountUi(selected.Id));
            accountPreference.IsOpen = false;
        }
        catch (Exception e)
        {
            accountPreference.Title = "提供商选择未保存";
            Ui.Status(accountPreference, "凭据已保存，但下次启动可能恢复此前提供商。\r\n" + e.Message, AlertTone.Warning);
        }
    }
    AgentConfig Current() => Files.Config with { MapFormat = mapFormat.SelectedItem?.ToString() ?? "table", TimeoutSeconds = (int)timeout.Value, MaxDecisionsPerTurn = (int)budget.Value, ExtraRules = rules.Text ?? "" };
    internal async Task Initialize()
    {
        loading = true;
        try
        {
            var c = Files.Config;
            mapFormat.SelectedItem = c.MapFormat;
            timeout.Value = c.TimeoutSeconds;
            budget.Value = c.MaxDecisionsPerTurn;
            rules.Text = c.ExtraRules;
            if (!File.Exists(Path.Combine(Files.Root, "bridge.token")))
                await StartHost();
            else
            {
                try
                {
                    SetHeader();
                    using var health = await http.GetAsync("/health");
                    if (!health.IsSuccessStatusCode)
                        await StartHost();
                }
                catch { await StartHost(); }
            }
            SetHeader();
            await LoadCatalog();
            var ui = TohotopiaShared.RoomFiles.Read<AccountUi>(Files.Root, "account-ui.json");
            provider.SelectedItem = catalogProviders.FirstOrDefault(p => p.Id == CatalogProviderId(ui?.Provider ?? c.Provider));
            await RefreshAccounts();
        }
        catch (Exception e) { SetAccountStatus(e.Message, AlertTone.Danger, "设置初始化失败"); }
        finally { loading = false; }
    }
    void SetHeader() => http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", File.ReadAllText(Path.Combine(Files.Root, "bridge.token")).Trim());
    async Task StartHost()
    {
        string exe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "AgentHost", OperatingSystem.IsWindows() ? "AgentHost.exe" : "AgentHost"));
        if (!File.Exists(exe))
            throw new FileNotFoundException("找不到 AgentHost，请先运行 Build.ps1。", exe);
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        psi.Environment["TOHOTOPIA_LLM_DATA"] = Files.Root;
        // When opened independently, bind the host to this window instead.
        if (!int.TryParse(Environment.GetEnvironmentVariable("TOHOTOPIA_LLM_OWNER_PID"), out _) ||
            !long.TryParse(Environment.GetEnvironmentVariable("TOHOTOPIA_LLM_OWNER_START"), out _))
        {
            using var owner = Process.GetCurrentProcess();
            psi.Environment["TOHOTOPIA_LLM_OWNER_PID"] = owner.Id.ToString();
            psi.Environment["TOHOTOPIA_LLM_OWNER_START"] = owner.StartTime.ToUniversalTime().Ticks.ToString();
        }
        Process.Start(psi)?.Dispose();
        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(300);
            if (!File.Exists(Path.Combine(Files.Root, "bridge.token")))
                continue;
            SetHeader();
            try
            {
                using var response = await http.GetAsync("/health");
                if (response.IsSuccessStatusCode)
                    return;
            }
            catch { }
        }
        throw new TimeoutException("本地服务未启动");
    }
    async Task Save()
    {
        try
        {
            using var response = await http.PutAsJsonAsync("/settings", Current(), SettingsJson.Type<AgentConfig>());
            response.EnsureSuccessStatusCode();
            globalStatus.Title = "全局规则已保存";
            Ui.Status(globalStatus, "AI 模型和推理选项由各席位配置。", AlertTone.Success);
        }
        catch (Exception e)
        {
            globalStatus.Title = "全局规则保存失败";
            Ui.Status(globalStatus, e.Message, AlertTone.Danger);
        }
    }
    async Task ProviderChanged()
    {
        ResetAuthorization();
        try
        {
            accountPreferenceWriter(Files.Root, new AccountUi(SelectedProvider()));
            accountPreference.IsOpen = false;
        }
        catch (Exception e)
        {
            accountPreference.Title = "提供商选择未保存";
            Ui.Status(accountPreference, "本次选择和账号连接仍可继续；下次启动可能恢复此前选择。\r\n" + e.Message, AlertTone.Warning);
        }
        await RefreshAccounts();
    }
    void ResetAuthorization()
    {
        loginVersion++;
        authUrl = ""; loginId = ""; loginStarting = false; completing = false;
        poll.Stop(); browser.IsEnabled = false;
        SetAuthActionsEnabled(true);
    }
    async Task RefreshAccounts()
    {
        if (closed) return;
        int version = ++accountsVersion;
        try
        {
            var list = await http.GetFromJsonAsync("/auth/accounts", SettingsJson.Type<List<AccountStatus>>()) ?? new();
            if (closed || version != accountsVersion) return;
            accountStates = list;
            var rows = new List<AccountRow>();
            foreach (var a in list.Where(a => a.Configured))
                rows.Add(new AccountRow(a.Provider, AccountProviderName(a.Provider), a.AuthType, a.AuthType == "api_key" ? "API Key" : "订阅授权", AccountLabel(a.Status), a.Expires?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "—", a.RefreshAvailable ? "可自动刷新" : "—"));
            accounts.ItemsSource = rows;
            selectedAccount = list.FirstOrDefault(a => a.Provider == AuthDriver(SelectedProvider()));
            UpdateAuthControls(selectedAccount);
            SetAccountStatus(selectedAccount?.Configured != true
                ? MissingAuthMessage()
                : selectedAccount.Message + "\r\n凭据按提供商加密保存在当前 Windows 用户下，关闭窗口或重启服务后自动恢复。\r\n模型及推理选项请在 AI 席位中配置。",
                selectedAccount?.Configured == true ? AlertTone.Success : AlertTone.Info);
        }
        catch (Exception e)
        {
            if (closed || version != accountsVersion) return;
            selectedAccount = null;
            SetAccountStatus("账号状态读取失败：" + e.Message, AlertTone.Danger, "账号状态读取失败");
            UpdateAuthControls(null);
        }
    }
    static string AuthDriver(string p) => p switch { "xai" => "grok", "kimi-code-plan-global" => "kimi", _ => p };
    ProviderAuthMethod[] SelectedAuthMethods() => catalogProviders.FirstOrDefault(p => p.Id == SelectedProvider())?.AuthMethods ?? Array.Empty<ProviderAuthMethod>();
    ProviderAuthMethod? ApiKeyMethod() => SelectedAuthMethods().FirstOrDefault(method => method.Type == "api");
    bool SupportsSubscription() => SelectedAuthMethods().Any(method => method.Type == "oauth");
    string MissingAuthMessage()
    {
        bool apiKey = ApiKeyMethod() is not null, subscription = SupportsSubscription();
        if (apiKey && subscription) return "尚未配置连接凭据。可点击“填写信息”保存 API Key，或使用订阅登录。";
        if (apiKey && !catalogProviders.Any(p => p.Id == SelectedProvider() && p.ConnectionSupported))
            return "可填写 API Key 并加密保存。该提供商的专用请求协议尚未接入，暂不可用于 AI 席位。";
        if (apiKey) return "尚未配置 API Key。点击“填写信息”输入提供商凭据。";
        if (subscription) return "尚未登录该提供商的订阅账号。";
        return "该提供商当前没有可用的连接认证方式。";
    }
    static string AccountProviderName(string p) => p switch { "grok" => "xAI", "moonshotai" => "Moonshot AI / Kimi API", "kimi" => "Kimi Code Plan", "deepseek" => "DeepSeek", "openai" => "OpenAI", _ => p };
    void UpdateAuthControls(AccountStatus? selected)
    {
        bool apiKey = ApiKeyMethod() is not null, subscription = SupportsSubscription();
        bool editing = apiKey && selected?.Configured == true && selected.AuthType == "api_key";
        login.Content = apiKey ? editing ? "编辑 API Key" : "填写信息" : !subscription ? "暂不支持连接" : selected?.Connected == true ? "重新登录订阅" : "登录订阅账号";
        subscriptionLogin.IsVisible = apiKey && subscription;
        subscriptionLogin.Content = selected?.Connected == true ? "重新登录订阅" : "登录订阅账号";
        browser.IsVisible = subscription;
        browser.IsEnabled = subscription && authUrl.StartsWith("https://");
        callback.IsVisible = subscription;
        complete.IsVisible = subscription;
        if (callbackCaption != null)
            callbackCaption.IsVisible = subscription;
        SetAuthActionsEnabled(true);
    }
    static string AccountLabel(string s) => s switch { "connected" => "已连接", "configured" => "已保存", "refresh_pending" => "已连接，待自动刷新", "reauthorize" => "需要重新连接", "refresh_failed" => "刷新失败（保留凭据）", "credential_error" => "凭据不可读", _ => "未配置" };
    async Task RefreshCredentials()
    {
        string providerId = SelectedProvider();
        int version = loginVersion;
        try
        {
            using var r = await http.PostAsync("/auth/" + Uri.EscapeDataString(providerId) + "/refresh", null);
            if (!r.IsSuccessStatusCode)
                throw new InvalidOperationException(await r.Content.ReadAsStringAsync());
            if (closed || version != loginVersion) return;
            await RefreshAccounts();
        }
        catch (Exception e)
        {
            if (closed || version != loginVersion) return;
            await RefreshAccounts();
            if (!closed && version == loginVersion)
                SetAccountStatus("凭据刷新失败：" + e.Message, AlertTone.Danger, "凭据刷新失败");
        }
    }
    async Task Complete()
    {
        if (completing || closed) return;
        if (loginId.Length == 0)
        {
            SetAccountStatus("请先发起登录。", AlertTone.Warning);
            return;
        }
        completing = true;
        complete.IsEnabled = false;
        string id = loginId;
        int version = loginVersion;
        try
        {
            using var r = await http.PostAsJsonAsync("/auth/login/" + id + "/complete", new OAuthCompletion((callback.Text ?? "").Trim()), SettingsJson.Type<OAuthCompletion>());
            if (!r.IsSuccessStatusCode)
                throw new InvalidOperationException(await r.Content.ReadAsStringAsync());
            var state = await r.Content.ReadFromJsonAsync(SettingsJson.Type<LoginState>());
            if (closed || version != loginVersion || id != loginId || state is null) return;
            callback.Text = "";
            ShowState(state);
            if (state.Status != "pending")
            {
                poll.Stop();
                loginStarting = false;
                SetAuthActionsEnabled(true);
            }
            if (state.Status == "connected")
            {
                modelCache.Clear();
                await RefreshSeatModels();
                await RefreshAccounts();
            }
        }
        catch (Exception e)
        {
            if (!closed && version == loginVersion) SetAccountStatus(e.Message, AlertTone.Danger, "提交授权失败");
        }
        finally
        {
            completing = false;
            if (!closed && version == loginVersion) complete.IsEnabled = true;
        }
    }
    async Task Login(string? requestedProvider = null)
    {
        if (closed || loginStarting || credentialDialogOpen) return;
        if (requestedProvider is not null && !SelectProvider(requestedProvider)) return;
        if (!SupportsSubscription())
        {
            SetAccountStatus("该提供商已收录模型目录，但尚未实现订阅认证适配。", AlertTone.Warning);
            return;
        }
        string providerId = SelectedProvider();
        int version = loginVersion;
        bool pending = false;
        loginStarting = true;
        SetAuthActionsEnabled(false);
        try
        {
            using var r = await http.PostAsync($"/auth/{Uri.EscapeDataString(providerId)}/start", null);
            if (!r.IsSuccessStatusCode)
                throw new InvalidOperationException(await r.Content.ReadAsStringAsync());
            var s = await r.Content.ReadFromJsonAsync(SettingsJson.Type<LoginState>());
            if (closed || version != loginVersion || providerId != SelectedProvider() || s is null) return;
            loginId = s.Id;
            ShowState(s);
            pending = s.Status == "pending";
            if (pending) poll.Start();
            if (s.Status == "connected")
            {
                modelCache.Clear();
                await RefreshSeatModels();
                await RefreshAccounts();
            }
        }
        catch (Exception e)
        {
            if (!closed && version == loginVersion) SetAccountStatus(e.Message, AlertTone.Danger, "订阅登录失败");
        }
        finally
        {
            if (!closed && version == loginVersion && !pending)
            {
                loginStarting = false;
                SetAuthActionsEnabled(true);
            }
        }
    }
    void SetAuthActionsEnabled(bool enabled)
    {
        bool available = enabled && !closed && !loginStarting && !credentialDialogOpen;
        login.IsEnabled = available && (ApiKeyMethod() is not null || SupportsSubscription());
        subscriptionLogin.IsEnabled = available;
    }
    async Task ConfigureApiKey(string? requestedProvider = null)
    {
        if (closed || credentialDialogOpen || loginStarting) return;
        if (requestedProvider is not null && !SelectProvider(requestedProvider)) return;
        string providerId = SelectedProvider();
        string providerName = catalogProviders.FirstOrDefault(p => p.Id == providerId)?.Name ?? AccountProviderName(providerId);
        var method = ApiKeyMethod();
        if (method is null)
        {
            SetAccountStatus("该提供商没有可用的 API Key 连接方式。", AlertTone.Warning);
            return;
        }
        int version = loginVersion;
        credentialDialogOpen = true;
        SetAuthActionsEnabled(false);
        try
        {
            using var current = await http.GetAsync($"/auth/{Uri.EscapeDataString(providerId)}/credential");
            if (!current.IsSuccessStatusCode)
                throw new InvalidOperationException(await current.Content.ReadAsStringAsync());
            var credential = await current.Content.ReadFromJsonAsync(SettingsJson.Type<CredentialInfo>());
            if (closed || version != loginVersion || providerId != SelectedProvider()) return;
            bool editing = credential?.AuthType == "api_key";
            var dialog = apiKeyDialogFactory(providerName, method, credential, editing);
            dialog.SetProviders(catalogProviders, providerId);
            dialog.ProviderChangeRequested = async selectedId =>
            {
                using var current = await http.GetAsync($"/auth/{Uri.EscapeDataString(selectedId)}/credential");
                if (!current.IsSuccessStatusCode)
                    throw new InvalidOperationException(await current.Content.ReadAsStringAsync());
                var selectedCredential = await current.Content.ReadFromJsonAsync(SettingsJson.Type<CredentialInfo>());
                var selectedProvider = catalogProviders.Single(provider => provider.Id == selectedId);
                var selectedMethod = selectedProvider.AuthMethods.Single(authMethod => authMethod.Type == "api");
                dialog.SetProvider(selectedId, selectedProvider.Name, selectedMethod, selectedCredential, selectedCredential?.AuthType == "api_key");
            };
            if (!await dialog.ShowDialog<bool>(this)) return;
            if (closed || version != loginVersion) return;
            providerId = dialog.ProviderId.Length > 0 ? dialog.ProviderId : providerId;
            editing = dialog.Editing;
            var input = new ApiKeyRequest(dialog.Value("apiKey"), dialog.Value("baseUrl"), dialog.Value("workspaceId"));
            using var response = editing
                ? await http.PutAsJsonAsync($"/auth/{Uri.EscapeDataString(providerId)}/api-key", input, SettingsJson.Type<ApiKeyRequest>())
                : await http.PostAsJsonAsync($"/auth/{Uri.EscapeDataString(providerId)}/api-key", input, SettingsJson.Type<ApiKeyRequest>());
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(await response.Content.ReadAsStringAsync());
            if (closed) return;
            SelectProviderSilently(providerId);
            modelCache.Clear();
            await RefreshSeatModels();
            await RefreshAccounts();
            if (!closed && version == loginVersion)
                SetAccountStatus(editing
                    ? string.IsNullOrEmpty(input.ApiKey) ? "API Key 连接信息已更新；空密钥已保留原密钥。" : "API Key 连接信息及新密钥已更新。"
                    : "API Key 已加密保存。", AlertTone.Success, "凭据已保存");
        }
        catch (Exception e)
        {
            if (!closed && version == loginVersion) SetAccountStatus("保存 API Key 失败：" + e.Message, AlertTone.Danger, "凭据操作失败");
        }
        finally
        {
            credentialDialogOpen = false;
            if (!closed) SetAuthActionsEnabled(true);
        }
    }
    async Task RemoveAccount(AccountRow row)
    {
        if (closed || !removingAccounts.Add(row.Id)) return;
        try
        {
            bool confirmed = await Ui.Confirm(this, $"确定移除 {row.Provider} 的{row.Auth}凭据吗？其他提供商账号不会受影响。", "移除账号");
            if (!confirmed || closed) return;
            using var response = await http.DeleteAsync($"/auth/{Uri.EscapeDataString(row.Id)}");
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(await response.Content.ReadAsStringAsync());
            if (closed) return;
            if (AuthDriver(SelectedProvider()) == row.Id) ResetAuthorization();
            modelCache.Clear();
            await RefreshSeatModels();
            await RefreshAccounts();
            if (!closed) SetAccountStatus($"{row.Provider} 凭据已移除，其他提供商账号保持不变。", AlertTone.Success, "账号已移除");
        }
        catch (Exception e)
        {
            if (!closed) SetAccountStatus($"移除 {row.Provider} 失败：{e.Message}", AlertTone.Danger, "账号移除失败");
        }
        finally
        {
            removingAccounts.Remove(row.Id);
        }
    }
    void ShowState(LoginState s)
    {
        authUrl = s.Url;
        browser.IsEnabled = authUrl.StartsWith("https://");
        SetAccountStatus($"{s.Provider}：{s.Status}\r\n{s.Message}\r\n{s.Url}\r\n{(s.Code.Length > 0 ? "设备代码：" + s.Code : "")}", AlertTone.Info, "订阅授权进度");
    }
    async Task Poll()
    {
        if (polling || closed || loginId.Length == 0) return;
        polling = true;
        string id = loginId;
        int version = loginVersion;
        try
        {
            var s = await http.GetFromJsonAsync("/auth/login/" + id, SettingsJson.Type<LoginState>());
            if (closed || version != loginVersion || id != loginId || s is null) return;
            ShowState(s);
            if (s.Status == "connected")
            {
                modelCache.Clear();
                await RefreshSeatModels();
                await RefreshAccounts();
            }
            if (s.Status != "pending")
            {
                poll.Stop();
                loginStarting = false;
                SetAuthActionsEnabled(true);
            }
        }
        catch (Exception e)
        {
            if (closed || version != loginVersion) return;
            poll.Stop();
            loginStarting = false;
            SetAuthActionsEnabled(true);
            SetAccountStatus(e.Message, AlertTone.Danger, "授权状态读取失败");
        }
        finally
        {
            polling = false;
        }
    }
}
