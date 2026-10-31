using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Tohotopia;
namespace Settings;

public sealed partial class SettingsWindow : Form
{
    readonly ComboBox provider = new() { DropDownStyle = ComboBoxStyle.DropDownList }, mapFormat = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly NumericUpDown timeout = new() { Minimum = 10, Maximum = 600, Value = 120 }, budget = new() { Minimum = 1, Maximum = 200, Value = 80 };
    readonly TextBox callback = new() { UseSystemPasswordChar = true }, rules = new() { Multiline = true, ScrollBars = ScrollBars.Vertical }, status = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical }, globalStatus = new() { ReadOnly = true };
    readonly DataGridView accounts = new() { ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowHeadersVisible = false };
    readonly Button browser = new() { Text = "打开授权网页", Enabled = false }, login = new() { Text = "登录订阅账号" }, subscriptionLogin = new() { Text = "登录订阅账号" }, complete = new() { Text = "提交授权" };
    Label? callbackCaption;
    readonly System.Windows.Forms.Timer poll = new() { Interval = 2000 }; readonly HttpClient http = new() { BaseAddress = new Uri("http://127.0.0.1:17861"), Timeout = TimeSpan.FromSeconds(35) };
    string loginId = "", authUrl = ""; bool loading;
    public SettingsWindow()
    {
        Text = "帝国幻想乡 · AI 接管设置";
        Width = 1050;
        Height = 900;
        MinimumSize = new(900, 800);
        Font = new Font("Microsoft YaHei UI", 10);
        StartPosition = FormStartPosition.CenterScreen;
        Files.Root = Environment.GetEnvironmentVariable("TOHOTOPIA_LLM_DATA") ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "runtime"));
        Directory.CreateDirectory(Files.Root);
        var tabs = new TabControl { Dock = DockStyle.Fill };
        Controls.Add(tabs);
        TableLayoutPanel Page(string title)
        {
            var tab = new TabPage(title);
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(20), AutoScroll = true };
            panel.ColumnStyles.Add(new(SizeType.Absolute, 150));
            panel.ColumnStyles.Add(new(SizeType.Percent, 100));
            panel.ColumnStyles.Add(new(SizeType.Absolute, 110));
            tab.Controls.Add(panel);
            tabs.TabPages.Add(tab);
            return panel;
        }
        Label Row(TableLayoutPanel panel, string title, Control control, Control? extra = null, int height = 42)
        {
            int row = panel.RowCount++;
            panel.RowStyles.Add(new(SizeType.Absolute, height));
            var caption = new Label { Text = title, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
            panel.Controls.Add(caption, 0, row);
            control.Dock = DockStyle.Fill;
            panel.Controls.Add(control, 1, row);
            if (extra != null)
            {
                extra.Dock = DockStyle.Fill;
                panel.Controls.Add(extra, 2, row);
            }
            else
                panel.SetColumnSpan(control, 2);
            return caption;
        }
        var roomTab = new TabPage("AI 接管");
        roomTab.Controls.Add(BuildRoomPage());
        tabs.TabPages.Add(roomTab);
        var auth = Page("账号管理");
        Row(auth, "提供商", provider);
        accounts.Columns.Add("provider", "提供商");
        accounts.Columns.Add("auth", "连接方式");
        accounts.Columns.Add("state", "状态");
        accounts.Columns.Add("expires", "有效至");
        accounts.Columns.Add("refresh", "令牌刷新");
        Row(auth, "已保存提供商", accounts, null, 160);
        complete.Click += async (_, _) => await Complete();
        callbackCaption = Row(auth, "授权回调／代码", callback, complete);
        var actions = new FlowLayoutPanel();
        login.AutoSize = true;
        browser.AutoSize = true;
        var check = new Button { Text = "检查／刷新凭据", AutoSize = true };
        check.Click += async (_, _) => await RefreshCredentials();
        var reload = new Button { Text = "刷新账号状态", AutoSize = true };
        reload.Click += async (_, _) => await RefreshAccounts();
        subscriptionLogin.AutoSize = true;
        actions.Controls.AddRange(new Control[] { login, subscriptionLogin, browser, check, reload });
        Row(auth, "账号操作", actions, null, 80);
        Row(auth, "授权与连接状态", status, null, 240);
        var global = Page("全局规则");
        mapFormat.Items.AddRange(new object[] { "table", "json" });
        Row(global, "地图观测", mapFormat);
        Row(global, "请求超时（秒）", timeout);
        Row(global, "每回合动作上限", budget);
        Row(global, "追加游戏规则", rules, null, 300);
        var save = new Button { Text = "保存全局规则" };
        save.Click += async (_, _) => await Save();
        Row(global, "", save);
        Row(global, "保存状态", globalStatus);
        var logTab = new TabPage("日志");
        logTab.Controls.Add(BuildLogPage());
        tabs.TabPages.Add(logTab);
        provider.SelectedIndexChanged += async (_, _) =>
        {
            if (loading) return;
            authUrl = "";
            loginId = "";
            poll.Stop();
            browser.Enabled = false;
            TohotopiaShared.RoomFiles.Write(Files.Root, "account-ui.json", new AccountUi(SelectedProvider()));
            await RefreshAccounts();
        };
        login.Click += async (_, _) => { if (ApiKeyMethod() is not null) await ConfigureApiKey(); else await Login(); };
        subscriptionLogin.Click += async (_, _) => await Login();
        browser.Click += (_, _) => { if (Uri.TryCreate(authUrl, UriKind.Absolute, out var u) && u.Scheme == "https") Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true }); };
        poll.Tick += async (_, _) => await Poll();
        Shown += async (_, _) =>
        {
            await Initialize();
            if (Environment.GetCommandLineArgs().Contains("--login"))
            {
                if (ApiKeyMethod() is not null) await ConfigureApiKey(); else await Login();
            }
        };
        FormClosed += (_, _) => { poll.Stop(); http.Dispose(); };
    }
    sealed record AccountUi(string Provider);
    string SelectedProvider() => provider.SelectedValue?.ToString() ?? "openai";
    AgentConfig Current() => Files.Config with { MapFormat = mapFormat.Text, TimeoutSeconds = (int)timeout.Value, MaxDecisionsPerTurn = (int)budget.Value, ExtraRules = rules.Text };
    async Task Initialize()
    {
        loading = true;
        try
        {
            var c = Files.Config;
            mapFormat.Text = c.MapFormat;
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
            provider.SelectedValue = CatalogProviderId(ui?.Provider ?? c.Provider);
            await RefreshAccounts();
        }
        catch (Exception e) { status.Text = e.Message; }
        finally { loading = false; }
    }
    void SetHeader() => http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", File.ReadAllText(Path.Combine(Files.Root, "bridge.token")).Trim());
    async Task StartHost()
    {
        string exe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "AgentHost", "AgentHost.exe"));
        if (!File.Exists(exe))
            throw new FileNotFoundException("找不到 AgentHost.exe，请先运行 Build.ps1。", exe);
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
            using var response = await http.PutAsJsonAsync("/settings", Current(), Files.Json);
            response.EnsureSuccessStatusCode();
            globalStatus.Text = "全局规则已保存；AI 模型和推理选项由各席位配置。";
        }
        catch (Exception e) { globalStatus.Text = e.Message; }
    }
    async Task RefreshAccounts()
    {
        try
        {
            var list = await http.GetFromJsonAsync<List<AccountStatus>>("/auth/accounts", Files.Json) ?? new();
            accounts.Rows.Clear();
            foreach (var a in list.Where(a => a.Configured))
                accounts.Rows.Add(AccountProviderName(a.Provider), a.AuthType == "api_key" ? "API Key" : "订阅授权", AccountLabel(a.Status), a.Expires?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "—", a.RefreshAvailable ? "可自动刷新" : "—");
            var selected = list.FirstOrDefault(a => a.Provider == AuthDriver(SelectedProvider()));
            UpdateAuthControls(selected);
            status.Text = selected?.Configured != true
                ? MissingAuthMessage()
                : selected.Message + "\r\n凭据按提供商加密保存在当前 Windows 用户下，关闭窗口或重启服务后自动恢复。\r\n模型及推理选项请在 AI 席位中配置。";
        }
        catch (Exception e) { status.Text = "账号状态读取失败：" + e.Message; UpdateAuthControls(null); }
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
    static string AccountProviderName(string p) => p switch { "grok" => "xAI", "moonshotai" => "Moonshot AI / Kimi API", "kimi" => "Kimi Code Plan", _ => p };
    void UpdateAuthControls(AccountStatus? selected)
    {
        bool apiKey = ApiKeyMethod() is not null, subscription = SupportsSubscription();
        login.Text = apiKey ? "填写信息" : !subscription ? "暂不支持连接" : selected?.Connected == true ? "重新登录订阅" : "登录订阅账号";
        login.Enabled = apiKey || subscription;
        subscriptionLogin.Visible = apiKey && subscription;
        subscriptionLogin.Text = selected?.Connected == true ? "重新登录订阅" : "登录订阅账号";
        browser.Visible = subscription;
        browser.Enabled = subscription && authUrl.StartsWith("https://");
        callback.Visible = subscription;
        complete.Visible = subscription;
        if (callbackCaption != null)
            callbackCaption.Visible = subscription;
    }
    static string AccountLabel(string s) => s switch { "connected" => "已连接", "configured" => "已保存", "refresh_pending" => "已连接，待自动刷新", "reauthorize" => "需要重新连接", "refresh_failed" => "刷新失败（保留凭据）", "credential_error" => "凭据不可读", _ => "未配置" };
    async Task RefreshCredentials()
    {
        try
        {
            using var r = await http.PostAsync("/auth/" + SelectedProvider() + "/refresh", null);
            if (!r.IsSuccessStatusCode)
                throw new InvalidOperationException(await r.Content.ReadAsStringAsync());
            await RefreshAccounts();
        }
        catch (Exception e) { await RefreshAccounts(); status.AppendText("\r\n" + e.Message); }
    }
    async Task Complete()
    {
        try
        {
            if (loginId.Length == 0)
                throw new InvalidOperationException("请先发起登录");
            using var r = await http.PostAsJsonAsync("/auth/login/" + loginId + "/complete", new OAuthCompletion(callback.Text.Trim()), Files.Json);
            if (!r.IsSuccessStatusCode)
                throw new InvalidOperationException(await r.Content.ReadAsStringAsync());
            callback.Clear();
            ShowState((await r.Content.ReadFromJsonAsync<LoginState>(Files.Json))!);
        }
        catch (Exception e) { status.Text = e.Message; }
    }
    async Task Login()
    {
        try
        {
            if (!SupportsSubscription())
                throw new InvalidOperationException("该提供商已收录模型目录，但尚未实现订阅认证适配。");
            login.Enabled = false;
            subscriptionLogin.Enabled = false;
            using var r = await http.PostAsync($"/auth/{SelectedProvider()}/start", null);
            if (!r.IsSuccessStatusCode)
            {
                status.Text = await r.Content.ReadAsStringAsync();
                SetAuthActionsEnabled(true);
                return;
            }
            var s = await r.Content.ReadFromJsonAsync<LoginState>(Files.Json);
            loginId = s!.Id;
            ShowState(s);
            poll.Start();
        }
        catch (Exception e) { status.Text = e.Message; SetAuthActionsEnabled(true); }
    }
    void SetAuthActionsEnabled(bool enabled)
    {
        login.Enabled = enabled && (ApiKeyMethod() is not null || SupportsSubscription());
        subscriptionLogin.Enabled = enabled;
    }
    async Task ConfigureApiKey()
    {
        string providerId = SelectedProvider();
        string providerName = catalogProviders.FirstOrDefault(p => p.Id == providerId)?.Name ?? AccountProviderName(providerId);
        var method = ApiKeyMethod();
        if (method is null)
            throw new InvalidOperationException("该提供商没有可用的 API Key 连接方式。");
        using var dialog = new ApiKeyDialog(providerName, method);
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        login.Enabled = false;
        try
        {
            using var response = await http.PostAsJsonAsync($"/auth/{Uri.EscapeDataString(providerId)}/api-key", new ApiKeyRequest(dialog.Value("apiKey"), dialog.Value("baseUrl"), dialog.Value("workspaceId")), Files.Json);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(await response.Content.ReadAsStringAsync());
            modelCache.Clear();
            await RefreshSeatModels();
            await RefreshAccounts();
        }
        catch (Exception e) { status.Text = "保存 API Key 失败：" + e.Message; }
        finally { SetAuthActionsEnabled(true); }
    }
    void ShowState(LoginState s)
    {
        authUrl = s.Url;
        browser.Enabled = authUrl.StartsWith("https://");
        status.Text = $"{s.Provider}：{s.Status}\r\n{s.Message}\r\n{s.Url}\r\n{(s.Code.Length > 0 ? "设备代码：" + s.Code : "")}";
    }
    async Task Poll()
    {
        try
        {
            var s = await http.GetFromJsonAsync<LoginState>("/auth/login/" + loginId, Files.Json);
            ShowState(s!);
            if (s!.Status == "connected")
            {
                modelCache.Clear();
                await RefreshSeatModels();
                await RefreshAccounts();
            }
            if (s!.Status != "pending")
            {
                poll.Stop();
                SetAuthActionsEnabled(true);
            }
        }
        catch (Exception e) { poll.Stop(); SetAuthActionsEnabled(true); status.Text = e.Message; }
    }
}

sealed class ApiKeyDialog : Form
{
    readonly Dictionary<string, Control> fields = new(StringComparer.OrdinalIgnoreCase);
    public string Value(string key) => fields.TryGetValue(key, out var field) ? field switch { TextBox text => text.Text.Trim(), ComboBox combo => (combo.SelectedItem as AuthPromptOption)?.Value ?? "", _ => "" } : "";
    public ApiKeyDialog(string providerName, ProviderAuthMethod method)
    {
        var prompts = method.Prompts ?? Array.Empty<ProviderAuthPrompt>();
        Text = method.Label + " — " + providerName;
        int height = 145 + prompts.Length * 42;
        Width = 560;
        Height = height;
        MinimumSize = new Size(560, height);
        MaximumSize = new Size(560, height);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MaximizeBox = false;
        MinimizeBox = false;
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 2, RowCount = prompts.Length + 2 };
        panel.ColumnStyles.Add(new(SizeType.Absolute, 110));
        panel.ColumnStyles.Add(new(SizeType.Percent, 100));
        foreach (var (prompt, index) in prompts.Select((item, index) => (item, index)))
        {
            panel.RowStyles.Add(new(SizeType.Absolute, 42));
            Control field;
            if (prompt.Type == "select")
            {
                var select = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, DisplayMember = nameof(AuthPromptOption.Label) };
                select.Items.AddRange((prompt.Options ?? Array.Empty<AuthPromptOption>()).Cast<object>().ToArray());
                if (select.Items.Count > 0) select.SelectedIndex = 0;
                field = select;
            }
            else
                field = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = prompt.Type == "secret" || prompt.Key.Equals("apiKey", StringComparison.OrdinalIgnoreCase), PlaceholderText = prompt.Placeholder ?? "", Text = prompt.DefaultValue ?? "" };
            fields[prompt.Key] = field;
            panel.Controls.Add(new Label { Text = prompt.Message, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, index);
            panel.Controls.Add(field, 1, index);
        }
        int noteRow = prompts.Length;
        panel.RowStyles.Add(new(SizeType.Percent, 100));
        panel.Controls.Add(new Label { Text = "凭据将使用当前 Windows 用户的 DPAPI 加密保存。", Dock = DockStyle.Fill, ForeColor = SystemColors.GrayText, TextAlign = ContentAlignment.MiddleLeft }, 0, noteRow);
        panel.SetColumnSpan(panel.GetControlFromPosition(0, noteRow)!, 2);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var save = new Button { Text = "保存", AutoSize = true };
        var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
        save.Click += (_, _) =>
        {
            var missing = prompts.FirstOrDefault(prompt => prompt.Required && string.IsNullOrWhiteSpace(Value(prompt.Key)));
            if (missing is not null)
            {
                MessageBox.Show(this, $"请输入{missing.Message}。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        };
        buttons.Controls.AddRange(new Control[] { cancel, save });
        int buttonRow = prompts.Length + 1;
        panel.RowStyles.Add(new(SizeType.Absolute, 42));
        panel.Controls.Add(buttons, 0, buttonRow);
        panel.SetColumnSpan(buttons, 2);
        Controls.Add(panel);
        AcceptButton = save;
        CancelButton = cancel;
    }
}
