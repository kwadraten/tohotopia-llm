using System.Net;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Settings;
using TKCode.Arc;
using Tohotopia;
using TohotopiaShared;
using static MigrationChecks;

static class AccountManagementChecks
{
    internal static async Task Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "tohotopia-account-check-" + Guid.NewGuid().ToString("N"));
        string? oldRoot = Environment.GetEnvironmentVariable("TOHOTOPIA_LLM_DATA");
        Environment.SetEnvironmentVariable("TOHOTOPIA_LLM_DATA", root);
        Files.Root = root;
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "bridge.token"), "fixture-token");
        RoomFiles.Write(root, "account-ui.json", new AccountUi("deepseek"));
        var handler = new AccountHost();
        bool denyPreference = false, failDialogConstruction = false, closeDialogBeforeOpen = false;
        var window = new SettingsWindow(new HttpClient(handler) { BaseAddress = new Uri("http://fixture") },
            accountPreferenceWriter: (path, value) =>
            {
                if (denyPreference) throw new UnauthorizedAccessException("fixture preference denied");
                RoomFiles.Write(path, "account-ui.json", value);
            },
            apiKeyDialogFactory: (name, method, credential, editing) =>
            {
                if (failDialogConstruction) throw new InvalidOperationException("fixture dialog constructor failed");
                var dialog = new ApiKeyDialog(name, method, credential, editing);
                if (closeDialogBeforeOpen) dialog.Close();
                return dialog;
            });
        try
        {
            window.Show();
            await Until(() => Selected(window, "deepseek") && Status(window).Contains("DeepSeek"));
            Check(Button(window, "编辑 API Key").IsVisible && Button(window, "重新登录订阅").IsVisible,
                "an existing DeepSeek API key offers editing and keeps subscription login separate");
            Check(!Named<Select>(window, "AuthProvider").IsVisible, "provider selection is removed from the main account panel");

            Click(window, "编辑 API Key");
            Click(window, "编辑 API Key");
            await Until(() => window.OwnedWindows.OfType<ApiKeyDialog>().Count() == 1);
            Check(handler.CredentialReads == 1, "repeated credential clicks create only one dialog and one metadata request");
            var dialog = window.OwnedWindows.OfType<ApiKeyDialog>().Single();
            Check(dialog.KeepSecret && Named<TextBox>(dialog, "Credential_apiKey").Text == ""
                && Named<TextBox>(dialog, "Credential_apiKey").PasswordChar != '\0'
                && dialog.Value("baseUrl") == "https://api.deepseek.com"
                && dialog.Value("workspaceId") == "workspace-old",
                "editing prefills only non-secret metadata and never reads the old secret into the UI");
            Click(dialog, "取消");
            await Until(() => !window.OwnedWindows.OfType<ApiKeyDialog>().Any());
            Check(handler.WriteAttempts.Count == 0, "canceling credential editing performs no mutation");

            dialog = await OpenEditor(window);
            var providerPicker = Named<Select>(dialog, "Credential_Provider");
            providerPicker.SelectedItem = providerPicker.ItemsSource!.Cast<CatalogProvider>().Single(provider => provider.Id == "google");
            await Until(() => dialog.ProviderId == "google");
            Check(dialog.Title?.Contains("Google") == true && Selected(window, "deepseek") && handler.WriteAttempts.Count == 0,
                "the dialog switches provider-specific forms without changing the main panel or writing credentials");
            Click(dialog, "取消");
            await Until(() => !window.OwnedWindows.OfType<ApiKeyDialog>().Any());

            dialog = await OpenEditor(window);
            Named<Select>(dialog, "Credential_baseUrl").SelectedItem = Named<Select>(dialog, "Credential_baseUrl").ItemsSource!
                .Cast<AuthPromptOption>().Single(option => option.Value == "https://api.deepseek.cn");
            Named<TextBox>(dialog, "Credential_workspaceId").Text = "workspace-new";
            Click(dialog, "保存");
            await Until(() => handler.WriteAttempts.Count == 1);
            Check(handler.WriteAttempts[0].Method == HttpMethod.Put && handler.WriteAttempts[0].Input.ApiKey == ""
                && handler.WriteAttempts[0].Input.BaseUrl == "https://api.deepseek.cn"
                && handler.WriteAttempts[0].Input.WorkspaceId == "workspace-new"
                && handler.Secrets["deepseek"] == "original-key",
                "editing with an empty key uses PUT, updates metadata and preserves the existing secret");

            dialog = await OpenEditor(window);
            Check(Named<TextBox>(dialog, "Credential_apiKey").Text == "", "a subsequent edit still does not reveal the preserved secret");
            Named<TextBox>(dialog, "Credential_apiKey").Text = "replacement-key";
            Click(dialog, "保存");
            await Until(() => handler.WriteAttempts.Count == 2);
            Check(handler.WriteAttempts[1].Method == HttpMethod.Put && handler.Secrets["deepseek"] == "replacement-key",
                "a non-empty replacement key updates the existing credential");

            int writesBeforeLogin = handler.WriteAttempts.Count;
            handler.SetAuthType("deepseek", "subscription");
            Click(window, "刷新账号状态");
            await Until(() => Button(window, "重新登录订阅").IsVisible);
            Click(window, "重新登录订阅");
            await Until(() => handler.Starts.Contains("deepseek"));
            Check(handler.WriteAttempts.Count == writesBeforeLogin && handler.Credentials["deepseek"].AuthType == "subscription",
                "relogin for an existing DeepSeek subscription does not force an API key overwrite");
            Select(window, "openai");
            await window.ProviderChange;
            Select(window, "deepseek");
            await window.ProviderChange;
            handler.SetAuthType("deepseek", "api_key");
            Click(window, "刷新账号状态");
            await Until(() => Button(window, "编辑 API Key").IsVisible);

            handler.FailWrites = true;
            dialog = await OpenEditor(window);
            Named<TextBox>(dialog, "Credential_apiKey").Text = "rejected-key";
            Click(dialog, "保存");
            await Until(() => Status(window).Contains("fixture write failed"));
            Check(!window.IsClosed && handler.Secrets["deepseek"] == "replacement-key" && StatusAlert(window).Tone == AlertTone.Danger,
                "an HTTP credential failure is shown in an Alert without killing the window or replacing the old secret");
            handler.FailWrites = false;

            failDialogConstruction = true;
            Click(window, "编辑 API Key");
            await Until(() => Status(window).Contains("fixture dialog constructor failed"));
            Check(!window.IsClosed && Button(window, "编辑 API Key").IsEnabled,
                "a dialog constructor failure is contained and credential actions become available again");
            failDialogConstruction = false;

            closeDialogBeforeOpen = true;
            int readsBeforeFailedOpen = handler.CredentialReads;
            Click(window, "编辑 API Key");
            await Until(() => handler.CredentialReads == readsBeforeFailedOpen + 1 && StatusAlert(window).Title == "凭据操作失败");
            Check(!window.IsClosed && Button(window, "编辑 API Key").IsEnabled,
                "a dialog opening failure is contained and does not leave reentrancy locked");
            closeDialogBeforeOpen = false;

            denyPreference = true;
            Select(window, "openai");
            await Until(() => Named<Alert>(window, "AccountPreferenceStatus").IsOpen);
            Check(Selected(window, "openai") && !window.IsClosed && Status(window).Contains("OpenAI"),
                "UnauthorizedAccessException while saving account-ui preference leaves provider selection and loading alive");
            Click(window, "重新登录订阅");
            await Until(() => handler.Starts.Contains("openai"));
            Check(!window.IsClosed && Named<Alert>(window, "AccountPreferenceStatus").IsOpen,
                "provider login continues after preference persistence is denied and keeps the independent warning visible");

            Select(window, "deepseek");
            await window.ProviderChange;
            var openAiActions = Actions(window, "openai");
            Check(Button(openAiActions, "重新授权").IsVisible
                && !Controls(openAiActions).OfType<TKCode.Arc.Button>().Single(button => button.Content?.ToString() == "编辑").IsVisible,
                "subscription rows expose reauthorization rather than API key editing");
            handler.FailStarts = true;
            Click(openAiActions, "重新授权");
            await Until(() => Status(window).Contains("fixture start failed"));
            Check(Selected(window, "openai") && handler.Accounts("openai").Configured
                && handler.Credentials["openai"].AuthType == "subscription",
                "row reauthorization selects its provider and a failed login preserves the old authorization");
            handler.FailStarts = false;

            Select(window, "deepseek");
            await window.ProviderChange;
            openAiActions = Actions(window, "openai");
            Click(openAiActions, "移除");
            Click(openAiActions, "移除");
            await Until(() => window.OwnedWindows.Count(item => item.Title == "移除账号") == 1);
            Check(handler.Deletes.Count == 0, "repeated remove clicks open one confirmation and send no premature delete");
            Click(window.OwnedWindows.Single(item => item.Title == "移除账号"), "取消");
            await Until(() => !window.OwnedWindows.Any(item => item.Title == "移除账号"));
            await Task.Delay(30);
            Check(handler.Deletes.Count == 0 && handler.Accounts("openai").Configured,
                "canceling removal sends no request and retains the account");

            openAiActions = Actions(window, "openai");
            int catalogReads = handler.CatalogReads;
            Click(openAiActions, "移除");
            await Until(() => window.OwnedWindows.Any(item => item.Title == "移除账号"));
            Click(window.OwnedWindows.Single(item => item.Title == "移除账号"), "确认移除");
            await Until(() => handler.Deletes.Contains("openai") && Rows(window).All(row => row.Id != "openai"));
            Check(handler.Deletes.SequenceEqual(["openai"]) && Rows(window).Any(row => row.Id == "deepseek")
                && handler.CatalogReads > catalogReads,
                "confirmed removal deletes only its provider and refreshes accounts and model availability");

            Select(window, "deepseek");
            await window.ProviderChange;
            var deepseekActions = Actions(window, "deepseek");
            Click(deepseekActions, "连通性测试");
            await Until(() => window.OwnedWindows.Any(item => item.Title?.StartsWith("连通性测试") == true));
            var pingDialog = window.OwnedWindows.Single(item => item.Title?.StartsWith("连通性测试") == true);
            var pingText = Named<TextBox>(pingDialog, "PingResultText");
            Check(pingText.Text?.Contains("deepseek-model") == true && pingText.Text.Contains("Pong!")
                && pingText.Text.Contains("Ping! Just answer me"), "connectivity test shows the exact probe, chosen model and reply");
            Click(pingDialog, "关闭");
            await Until(() => !window.OwnedWindows.Any(item => item.Title?.StartsWith("连通性测试") == true)
                && Status(window).Contains("连通性正常"));
            Check(handler.Pings.SequenceEqual(["deepseek"]),
                "connectivity test reports success through the account status area");
            handler.PingFailures["deepseek"] = "模型请求被服务拒绝：HTTP 429。quota exhausted";
            Click(Actions(window, "deepseek"), "连通性测试");
            await Until(() => window.OwnedWindows.Any(item => item.Title?.StartsWith("连通性测试") == true));
            pingDialog = window.OwnedWindows.Single(item => item.Title?.StartsWith("连通性测试") == true);
            pingText = Named<TextBox>(pingDialog, "PingResultText");
            Check(pingText.Text?.Contains("HTTP 429") == true && pingText.Text.Contains("quota exhausted"),
                "connectivity test displays complete server errors such as 429");
            Click(pingDialog, "关闭");
            await Until(() => Status(window).Contains("HTTP 429"));
            Check(StatusAlert(window).Tone == AlertTone.Danger,
                "connectivity failures surface in the account status area");

            Select(window, "deepseek");
            await window.ProviderChange;
            handler.StartGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            handler.StartRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Click(window, "重新登录订阅");
            await Until(() => handler.StartRequested.Task.IsCompleted);
            Select(window, "openai");
            await window.ProviderChange;
            handler.StartGate.SetResult();
            await Task.Delay(50);
            Check(!Status(window).Contains("DEVICE-deepseek") && Selected(window, "openai"),
                "a late login result cannot overwrite the state of a newly selected provider");

            Select(window, "deepseek");
            await window.ProviderChange;
            dialog = await OpenEditor(window);
            providerPicker = Named<Select>(dialog, "Credential_Provider");
            providerPicker.SelectedItem = providerPicker.ItemsSource!.Cast<CatalogProvider>().Single(provider => provider.Id == "google");
            await Until(() => dialog.ProviderId == "google");
            Named<TextBox>(dialog, "Credential_apiKey").Text = "google-dialog-key";
            int writesBeforeDialogSave = handler.WriteAttempts.Count;
            Click(dialog, "保存");
            await Until(() => handler.WriteAttempts.Count == writesBeforeDialogSave + 1);
            var dialogWrite = handler.WriteAttempts[^1];
            Check(dialogWrite.Method == HttpMethod.Post && dialogWrite.Provider == "google"
                && handler.Secrets["google"] == "google-dialog-key" && Selected(window, "google"),
                "saving uses the dialog's selected provider and synchronizes the hidden main selection");

            Select(window, "deepseek");
            await window.ProviderChange;
            int writesBeforeClose = handler.WriteAttempts.Count;
            Click(window, "编辑 API Key");
            await Until(() => window.OwnedWindows.OfType<ApiKeyDialog>().Any());
            window.StopServices();
            window.Close();
            await Task.Delay(50);
            Check(handler.WriteAttempts.Count == writesBeforeClose,
                "closing the owner while a credential dialog is open cannot save a late result");
        }
        finally
        {
            window.StopServices();
            window.Close();
            Environment.SetEnvironmentVariable("TOHOTOPIA_LLM_DATA", oldRoot);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    static async Task<ApiKeyDialog> OpenEditor(SettingsWindow window)
    {
        await Until(() => Button(window, "编辑 API Key").IsEnabled);
        Click(window, "编辑 API Key");
        await Until(() => window.OwnedWindows.OfType<ApiKeyDialog>().Any());
        return window.OwnedWindows.OfType<ApiKeyDialog>().Single();
    }
    static Alert StatusAlert(SettingsWindow window) => Named<Alert>(window, "AuthStatus");
    static string Status(SettingsWindow window) => StatusAlert(window).Content?.ToString() ?? "";
    static TKCode.Arc.Button Button(Control control, string content) => Controls(control).OfType<TKCode.Arc.Button>().First(button => button.Content?.ToString() == content && button.IsVisible);
    static bool Selected(SettingsWindow window, string id) => (Named<Select>(window, "AuthProvider").SelectedItem as CatalogProvider)?.Id == id;
    static void Select(SettingsWindow window, string id) => Named<Select>(window, "AuthProvider").SelectedItem = Named<Select>(window, "AuthProvider").ItemsSource!
        .Cast<CatalogProvider>().Single(provider => provider.Id == id);
    static Table Accounts(SettingsWindow window) => Controls(window).OfType<Table>().Single(table => table.Caption == "已保存提供商");
    static SettingsWindow.AccountRow[] Rows(SettingsWindow window) => Accounts(window).ItemsSource!.Cast<SettingsWindow.AccountRow>().ToArray();
    static Control Actions(SettingsWindow window, string provider)
    {
        var row = Rows(window).Single(item => item.Id == provider);
        return (Control)Accounts(window).Columns.Single(column => column.Header == "账号操作").CellTemplate!.Build(row)!;
    }

    sealed class AccountHost : HttpMessageHandler
    {
        internal readonly Dictionary<string, CredentialInfo> Credentials = new()
        {
            ["deepseek"] = new("deepseek", "api_key", "https://api.deepseek.com", "workspace-old", true),
            ["openai"] = new("openai", "subscription", "", "", true)
        };
        internal readonly Dictionary<string, string> Secrets = new() { ["deepseek"] = "original-key", ["openai"] = "openai-secret" };
        internal readonly List<(HttpMethod Method, string Provider, ApiKeyRequest Input)> WriteAttempts = new();
        internal readonly List<string> Deletes = new(), Starts = new(), Pings = new();
        internal readonly Dictionary<string, string> PingFailures = new();
        internal bool FailWrites, FailStarts;
        internal int CredentialReads, CatalogReads;
        internal TaskCompletionSource? StartGate, StartRequested;
        readonly List<AccountStatus> accounts = new()
        {
            new("deepseek", true, "connected", "DeepSeek 已连接", Configured: true, AuthType: "api_key"),
            new("openai", true, "connected", "OpenAI 已连接", Configured: true, AuthType: "subscription")
        };
        internal AccountStatus Accounts(string provider) => accounts.Single(item => item.Provider == provider);
        internal void SetAuthType(string provider, string authType)
        {
            var account = Accounts(provider);
            accounts[accounts.IndexOf(account)] = account with { AuthType = authType };
            var credential = Credentials[provider];
            Credentials[provider] = credential with { AuthType = authType };
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path == "/health") return EmptyJson();
            if (path == "/catalog/providers")
                return Json(new List<CatalogProvider>
                {
                    new("deepseek", "DeepSeek", true, [ApiMethod(), new("oauth", "订阅")]),
                    new("openai", "OpenAI", true, [new("oauth", "订阅")]),
                    new("google", "Google", true, [ApiMethod()])
                });
            if (path.StartsWith("/catalog/") && path.EndsWith("/models"))
            {
                CatalogReads++;
                string provider = path.Split('/')[2];
                return Json(new List<CatalogModel> { new(provider + "-model", provider + " model", false, []) });
            }
            if (path == "/auth/accounts") return Json(accounts);
            if (path.StartsWith("/auth/") && path.EndsWith("/credential"))
            {
                CredentialReads++;
                string provider = path.Split('/')[2];
                return Json(Credentials.GetValueOrDefault(provider) ?? new CredentialInfo(provider, "", "", "", false));
            }
            if (path.StartsWith("/auth/") && path.EndsWith("/ping") && request.Method == HttpMethod.Post)
            {
                string provider = path.Split('/')[2];
                Pings.Add(provider);
                return PingFailures.TryGetValue(provider, out string? failure)
                    ? Json(new PingResult(false, provider, provider + "-model", 12, "", failure))
                    : Json(new PingResult(true, provider, provider + "-model", 8, "Pong!", ""));
            }
            if (path.StartsWith("/auth/") && path.EndsWith("/start"))
            {
                string provider = path.Split('/')[2];
                StartRequested?.TrySetResult();
                if (StartGate is not null) await StartGate.Task.WaitAsync(cancellationToken);
                Starts.Add(provider);
                if (FailStarts) return Error("fixture start failed");
                return Json(new LoginState("login-" + provider, provider, "https://fixture/authorize", "DEVICE-" + provider, "pending", "授权"));
            }
            if (path.StartsWith("/auth/") && path.EndsWith("/api-key") && request.Method is not null && request.Method != HttpMethod.Get)
            {
                string provider = path.Split('/')[2];
                var input = JsonSerializer.Deserialize(await request.Content!.ReadAsStringAsync(cancellationToken), SettingsJson.Type<ApiKeyRequest>())!;
                WriteAttempts.Add((request.Method, provider, input));
                if (FailWrites) return Error("fixture write failed");
                if (!string.IsNullOrWhiteSpace(input.ApiKey)) Secrets[provider] = input.ApiKey;
                var old = Credentials.GetValueOrDefault(provider) ?? new CredentialInfo(provider, "", "", "", false);
                Credentials[provider] = new(provider, "api_key", input.BaseUrl, input.WorkspaceId, Secrets.ContainsKey(provider));
                if (accounts.Any(item => item.Provider == provider))
                {
                    var account = Accounts(provider);
                    accounts[accounts.IndexOf(account)] = account with { Configured = true, Connected = true, Status = "connected", AuthType = "api_key" };
                }
                else accounts.Add(new(provider, true, "connected", provider + " 已连接", Configured: true, AuthType: "api_key"));
                return EmptyJson();
            }
            if (path.StartsWith("/auth/") && request.Method == HttpMethod.Delete)
            {
                string provider = path.Split('/')[2];
                Deletes.Add(provider);
                accounts.RemoveAll(item => item.Provider == provider);
                Credentials.Remove(provider);
                Secrets.Remove(provider);
                return new(HttpStatusCode.NoContent);
            }
            if (path.StartsWith("/auth/login/"))
            {
                string provider = path.Split('/')[3].Replace("login-", "");
                return Json(new LoginState(path.Split('/')[3], provider, "https://fixture/authorize", "DEVICE-" + provider, "pending", "授权"));
            }
            return EmptyJson();
        }
        static ProviderAuthMethod ApiMethod() => new("api", "填写信息",
        [
            new("secret", "apiKey", "API Key", Required: true),
            new("select", "baseUrl", "API 地址", Options: [new("国际站", "https://api.deepseek.com"), new("中国站", "https://api.deepseek.cn")]),
            new("text", "workspaceId", "Workspace")
        ]);
        static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(value, SettingsJson.Type<T>()), Encoding.UTF8, "application/json")
        };
        static HttpResponseMessage EmptyJson() => new(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        static HttpResponseMessage Error(string message) => new(HttpStatusCode.UnprocessableEntity) { Content = new StringContent(message) };
    }
}
