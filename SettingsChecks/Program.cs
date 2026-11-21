using System.Net;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Layout;
using Avalonia.Threading;
using Settings;
using TKCode.Arc;
using Tohotopia;
using TohotopiaShared;
using Button = TKCode.Arc.Button;
using TabItem = TKCode.Arc.TabItem;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Contains("--owner-helper")) { Console.ReadLine(); return; }
        if (args.Length == 2 && args[0] == "--lifecycle") { LifecycleChecks.Run(args[1]).GetAwaiter().GetResult(); return; }
        if (args.Length == 2 && args[0] == "--shell-launch") { LifecycleChecks.Run(args[1], throughShortcut: true).GetAwaiter().GetResult(); return; }
        Settings.Program.UseArcTheme(AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).UseHarfBuzz()).SetupWithoutStarting();
        using var stop = new CancellationTokenSource();
        var checks = Run();
        _ = checks.ContinueWith(_ => stop.Cancel(), TaskScheduler.Default);
        Dispatcher.UIThread.MainLoop(stop.Token);
        checks.GetAwaiter().GetResult();
    }
    static async Task Run()
    {
        await MigrationChecks.Run();
        await AccountManagementChecks.Run();
    }
}

static class MigrationChecks
{
    internal static void Check(bool condition, string text)
    {
        if (!condition) throw new Exception(text);
        Console.WriteLine("PASS " + text);
    }
    internal static IEnumerable<Control> Controls(Control control)
    {
        yield return control;
        if (control is Ribbon ribbon)
            foreach (var tab in ribbon.Tabs)
                foreach (var child in Controls(tab)) yield return child;
        if (control is RibbonTab ribbonTab)
            foreach (var group in ribbonTab.Groups)
                foreach (var child in Controls(group)) yield return child;
        if (control is RibbonGroup ribbonGroup)
            foreach (var item in ribbonGroup.Items.OfType<Control>())
                foreach (var child in Controls(item)) yield return child;
        if (control is Panel panel)
            foreach (var item in panel.Children)
                foreach (var child in Controls(item)) yield return child;
        if (control is Border { Child: Control inner })
            foreach (var child in Controls(inner)) yield return child;
        if (control is ContentControl { Content: Control content })
            foreach (var child in Controls(content)) yield return child;
        if (control is ItemsControl items)
            foreach (var item in items.Items.OfType<Control>())
                foreach (var child in Controls(item)) yield return child;
    }
    internal static T Named<T>(Control control, string name) where T : Control => Controls(control).OfType<T>().First(c => c.Name == name);
    internal static void Click(Control control, string content) => Controls(control).OfType<Button>().First(b => b.Content?.ToString() == content && b.IsVisible).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    internal static async Task Until(Func<bool> condition)
    {
        for (int i = 0; i < 100 && !condition(); i++) await Task.Delay(10);
        Check(condition(), "asynchronous UI operation completes");
    }

    internal static async Task Run()
    {
        Check(!JsonSerializer.IsReflectionEnabledByDefault, "Settings JSON reflection is disabled");
        string root = Path.Combine(Path.GetTempPath(), "tohotopia-avalonia-check-" + Guid.NewGuid().ToString("N"));
        var oldRoot = Environment.GetEnvironmentVariable("TOHOTOPIA_LLM_DATA");
        Environment.SetEnvironmentVariable("TOHOTOPIA_LLM_DATA", root);
        Files.Root = root;
        Directory.CreateDirectory(root);
        string legacyRoot = root + "-legacy";
        Directory.CreateDirectory(legacyRoot);
        RoomFiles.Write(legacyRoot, "settings.json", new AgentConfig { TimeoutSeconds = 111 });
        File.WriteAllText(Path.Combine(legacyRoot, "deepseek.token"), "legacy-credential");
        RuntimePaths.MigrateSettings(root, legacyRoot);
        Check(RoomFiles.Read<AgentConfig>(root, "settings.json")?.TimeoutSeconds == 111
            && !File.Exists(Path.Combine(root, "deepseek.token")), "runtime migration preserves ordinary settings without importing legacy credentials");
        File.WriteAllText(Path.Combine(root, "bridge.token"), "fixture-token");
        Files.Save(new AgentConfig { ExtraRules = "原有规则", TimeoutSeconds = 123, MaxDecisionsPerTurn = 77 });
        RoomFiles.Write(root, "account-ui.json", new AccountUi("grok"));
        RoomFiles.Write(root, "room.json", new RoomConfig { NativeLobby = true, Published = true, UseModels = false,
            AIs = [new() { Provider = "grok", Model = "xai-model", Reasoning = "high" }, new() { Model = "model-b" }] });
        var handler = new HostFixture();
        var window = new SettingsWindow(new HttpClient(handler) { BaseAddress = new Uri("http://fixture") });
        try
        {
            window.Show();
            Check(window.Icon != null, "main window receives the application icon");
            var iconDialog = new Window();
            iconDialog.Show(window);
            Check(iconDialog.Icon != null, "secondary windows inherit the application icon");
            iconDialog.Close();
            await Until(() => Named<Select>(window, "AuthProvider").SelectedItem is CatalogProvider);
            var ribbon = Named<Ribbon>(window, "SettingsTabs");
            Check(window.Title == "帝国幻想乡 · AI 接管设置" && ribbon.Tabs.Count == 4
                && ribbon.SelectedIndex == 0 && ribbon.Tabs.SelectMany(tab => tab.Groups).All(group => !group.CanCollapse), "all four pages and their commands use the fixed Ribbon");
            ribbon.IsMinimized = true;
            Check(!ribbon.IsMinimized, "the Ribbon cannot be minimized by a command or keyboard shortcut");
            var pages = Controls(window).OfType<StackPanel>().Where(panel => panel.MaxWidth == Ui.PageWidth).ToList();
            Check(pages.Count == 4 && pages.All(panel => panel.HorizontalAlignment == HorizontalAlignment.Center), "all page bodies have an explicit centered maximum-width layout");
            Check(Controls(window).OfType<Checkbox>().All(c => c.Name != "CloseToTray"), "the standalone panel offers no tray-residency option");
            var seatCount = Named<NumberField>(window, "SeatCount");
            var callbackInput = Named<TextBox>(window, "AuthCallback");
            seatCount.Measure(new Size(220, 100));
            callbackInput.Measure(new Size(220, 100));
            Check(seatCount.DesiredSize.Height > 0 && Math.Abs(seatCount.DesiredSize.Height - callbackInput.DesiredSize.Height) <= 1, "the number field matches the input height");
            var idle = new SettingsWindow(new HttpClient(new HostFixture()) { BaseAddress = new Uri("http://fixture") }, initializeOnOpen: false);
            idle.Show();
            Check(Controls(idle).OfType<Button>().Count(b => b.Content?.ToString() == "登录订阅账号" && b.IsVisible) == 1, "subscription login is hidden until a provider with both API key and subscription is selected");
            idle.StopServices();
            idle.Close();
            Check(Controls(window).OfType<Button>().Count(b => b.Content?.ToString() == "登录订阅账号" && b.IsVisible) == 1
                && Controls(window).OfType<Button>().Any(b => b.Content?.ToString() == "填写信息" && b.IsVisible), "a provider with both methods shows the API key button and the subscription login");
            Check(Named<NumberField>(window, "Timeout").Value == 123 && Named<TextBox>(window, "ExtraRules").Text == "原有规则", "existing global configuration is restored");
            Check(((CatalogProvider)Named<Select>(window, "AuthProvider").SelectedItem!).Id == "xai", "persisted account selection restores provider aliases");
            Check(((CatalogModel)Named<Select>(window, "Seat1Model").SelectedItem!).Id == "xai-model"
                && Named<Select>(window, "Seat1Reasoning").SelectedItem?.ToString() == "high"
                && ((CatalogModel)Named<Select>(window, "Seat2Model").SelectedItem!).Id == "model-b", "saved seat order, provider, model and reasoning survive migration");
            var firstModel = Named<Select>(window, "Seat1Model");
            firstModel.SelectedItem = firstModel.ItemsSource!.Cast<CatalogModel>().Single(m => m.Id == "xai-simple");
            await Until(() => Named<Select>(window, "Seat1Reasoning").SelectedItem?.ToString() == "default");
            Check(Named<Select>(window, "Seat1Reasoning").Items.Count == 1, "changing model refreshes supported reasoning levels");
            Named<NumberField>(window, "SeatCount").Value = 4;
            await Until(() => Named<Select>(window, "Seat4Model").SelectedItem is CatalogModel);
            Click(window, "保存接管配置");
            var room = RoomFiles.Read<RoomConfig>(root, "room.json")!;
            Check(room.AIs.Count == 4 && room.NativeLobby && room.Published && !room.UseModels && room.AIs[0].Model == "xai-simple", "saving four seats preserves native-lobby and automatic-control settings");
            Click(window, "启用模型接管");
            var enable = RoomFiles.Read<RoomCommand>(root, "room-command.json")!;
            Click(window, "恢复原生 AI");
            var restore = RoomFiles.Read<RoomCommand>(root, "room-command.json")!;
            Check(enable.Action == "models" && restore.Action == "native" && enable.Id != restore.Id, "takeover controls retain command protocol and unique IDs");
            RoomFiles.Write(root, "room-status.json", new RoomStatus("playing", "fixture", ModelsEnabled: true));
            Click(window, "保存接管配置");
            Check(RoomFiles.Read<RoomConfig>(root, "room.json")!.Revision == room.Revision && Named<Alert>(window, "ActionStatus").Content?.ToString()?.Contains("请先恢复") == true
                && Named<Alert>(window, "ActionStatus").Tone == AlertTone.Danger, "editing a live model-controlled match is blocked by a full-width Alert");
            Click(window, "停用接管配置");
            Check(RoomFiles.Read<RoomConfig>(root, "room.json") is { Published: false }, "stopping takeover preserves the room and unpublishes configuration");
            Named<NumberField>(window, "Timeout").Value = 60;
            Named<TextBox>(window, "ExtraRules").Text = "新的规则\n第二行";
            Click(window, "保存全局规则");
            await Until(() => handler.Saved is not null);
            Check(handler.Saved is { TimeoutSeconds: 60, ExtraRules: "新的规则\n第二行", MaxDecisionsPerTurn: 77 }, "global settings use the existing host API and preserve unchanged values");
            Click(window, "登录订阅账号");
            await Until(() => Named<Alert>(window, "AuthStatus").Content?.ToString()?.Contains("DEVICE-CODE") == true);
            Named<TextBox>(window, "AuthCallback").Text = "  fixture-callback  ";
            Click(window, "提交授权");
            await Until(() => handler.Callback is not null);
            Check(handler.Callback == "fixture-callback" && Named<TextBox>(window, "AuthCallback").Text == "", "subscription login and callback submission retain their HTTP contract");

            Click(window, "填写信息");
            await Until(() => window.OwnedWindows.OfType<ApiKeyDialog>().Any());
            var credentialDialog = window.OwnedWindows.OfType<ApiKeyDialog>().Single();
            Click(credentialDialog, "保存");
            await Until(() => Named<Alert>(credentialDialog, "CredentialValidation").IsOpen);
            Check(handler.ApiKey is null, "required credential validation blocks empty submissions");
            Named<TextBox>(credentialDialog, "Credential_apiKey").Text = " fixture-key ";
            Click(credentialDialog, "保存");
            await Until(() => handler.ApiKey is not null);
            Check(handler.ApiKey == "fixture-key", "API key dialog returns values to the existing host endpoint");

            var dialog = new ApiKeyDialog("Fixture", new("api", "填写信息", [new("secret", "apiKey", "API Key", Required: true),
                new("select", "baseUrl", "区域", Options: [new("国际站", "https://fixture/v1"), new("中国站", "https://other/v1")]),
                new("text", "workspaceId", "Workspace", DefaultValue: "default")]));
            Named<TextBox>(dialog, "Credential_apiKey").Text = " fixture-key ";
            Check(Named<TextBox>(dialog, "Credential_apiKey").PasswordChar != '\0' && dialog.Value("apiKey") == "fixture-key"
                && dialog.Value("baseUrl") == "https://fixture/v1" && dialog.Value("workspaceId") == "default", "API key dialog retains masking, select values, defaults and trimmed input");
            dialog.Close();

            ribbon.SelectedIndex = 3;
            await Task.Delay(30);
            AgentEvents.Write(root, new(DateTimeOffset.UtcNow, "host", "model_request", "request", AiSlot: 1, RequestId: "r1"));
            AgentEvents.Write(root, new(DateTimeOffset.UtcNow, "host", "reasoning_delta", "思考一", AiSlot: 1, RequestId: "r1"));
            AgentEvents.Write(root, new(DateTimeOffset.UtcNow, "host", "reasoning_delta", "思考二", AiSlot: 1, RequestId: "r1"));
            Click(window, "立即刷新");
            var logs = Named<Table>(window, "AgentLogs");
            Check(!logs.ItemsSource!.Cast<AgentEvent>().Any(e => e.Stage == "reasoning_delta"), "unfinished reasoning stays hidden");
            AgentEvents.Write(root, new(DateTimeOffset.UtcNow, "host", "model_decision", "decision", AiSlot: 1, RequestId: "r1"));
            AgentEvents.Write(root, new(DateTimeOffset.UtcNow, "game", "executed", "执行\n第二行", "error", AiSlot: 2, RequestId: "r2"));
            AgentEvents.Write(root, new(DateTimeOffset.UtcNow, "host", "model_failed", "HTTP 401: invalid_request_error\n完整错误第二行", "error", AiSlot: 1, RequestId: "r3"));
            Click(window, "立即刷新");
            Check(logs.ItemsSource!.Cast<AgentEvent>().Single(e => e.Stage == "reasoning_delta").Message == "思考一思考二", "completed reasoning fragments merge without losing correlation");
            var failed = logs.ItemsSource!.Cast<AgentEvent>().Single(e => e.Stage == "model_failed");
            var detail = (Button)logs.Columns.Single(column => column.Header == "详情").CellTemplate!.Build(failed)!;
            Check(detail.Content?.ToString() == "查看详情", "each Agent row exposes an explicit full-details button");
            Click(detail, "查看详情");
            await Until(() => window.OwnedWindows.Any(item => item.Title?.StartsWith("日志详情") == true));
            var detailDialog = window.OwnedWindows.Single(item => item.Title?.StartsWith("日志详情") == true);
            var detailText = Named<TextBox>(detailDialog, "AgentLogDetailText");
            Check(detailText.Text?.Contains("HTTP 401: invalid_request_error\n完整错误第二行") == true
                && detailText.Text.Contains("请求 ID：r3"), "the details dialog retains the complete multiline model error and request correlation");
            Click(detailDialog, "关闭");
            await Until(() => !window.OwnedWindows.Any(item => item.Title?.StartsWith("日志详情") == true));
            var decision = logs.ItemsSource!.Cast<AgentEvent>().Single(e => e.Stage == "model_decision");
            detail = (Button)logs.Columns.Single(column => column.Header == "详情").CellTemplate!.Build(decision)!;
            Click(detail, "查看详情");
            await Until(() => window.OwnedWindows.Any(item => item.Title?.StartsWith("日志详情") == true));
            detailDialog = window.OwnedWindows.Single(item => item.Title?.StartsWith("日志详情") == true);
            detailText = Named<TextBox>(detailDialog, "AgentLogDetailText");
            Check(detailText.Text?.Contains("decision") == true && detailText.Text.Contains("思考一思考二"),
                "the details dialog includes complete reasoning and model output related to the same request");
            Click(detailDialog, "关闭");
            await Until(() => !window.OwnedWindows.Any(item => item.Title?.StartsWith("日志详情") == true));
            Check(!Controls(window).OfType<TabItem>().Any(tab => tab.Header?.ToString() == "游戏运行日志"), "the obsolete BepInEx game-log tab is removed");
            Check(Named<Alert>(window, "LogStatus").IsOpen && Named<Alert>(window, "LogStatus").Content?.ToString()?.Contains("只有‘游戏执行’") == true, "log diagnostics use a wrapping Alert");
            Named<Checkbox>(window, "ErrorsOnly").IsChecked = true;
            var retained = logs.ItemsSource!.Cast<AgentEvent>().ToList();
            Check(retained.Any(e => e.Stage == "executed") && retained.Single(e => e.Stage == "model_failed").Message.Contains("HTTP 401: invalid_request_error\n完整错误第二行")
                && retained.All(e => e.Level == "error"), "warning/error filtering retains complete log entries");
            long logBytes = Directory.GetFiles(Path.Combine(root, "logs")).Sum(p => new FileInfo(p).Length);
            Click(window, "清空 Agent 行动");
            Check(!logs.ItemsSource!.Cast<AgentEvent>().Any() && Directory.GetFiles(Path.Combine(root, "logs")).Sum(p => new FileInfo(p).Length) == logBytes, "clearing the log view does not delete persisted diagnostics");
            Check(!typeof(SettingsWindow).Assembly.GetReferencedAssemblies().Any(a => a.Name == "System.Windows.Forms"), "Settings no longer depends on WinForms");
            window.Close();
            Check(window.IsClosed, "closing the window exits the standalone settings app");
            var restored = new SettingsWindow(new HttpClient(new HostFixture()) { BaseAddress = new Uri("http://fixture") }, initializeOnOpen: false);
            restored.Show();
            restored.Exit();
            Check(restored.IsClosed, "explicit exit really closes a restored window");
        }
        catch (Exception error)
        {
            Console.WriteLine(error);
            throw;
        }
        finally
        {
            window.StopServices();
            window.Close();
            Environment.SetEnvironmentVariable("TOHOTOPIA_LLM_DATA", oldRoot);
            Directory.Delete(root, true);
            Directory.Delete(legacyRoot, true);
        }
    }

    sealed class HostFixture : HttpMessageHandler
    {
        internal AgentConfig? Saved;
        internal string? Callback;
        internal string? ApiKey;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            string json = path switch
            {
                "/health" => "{}",
                "/catalog/providers" => JsonSerializer.Serialize(new List<CatalogProvider>
                {
                    new("openai", "OpenAI", true, [new("api", "填写信息", [new("secret", "apiKey", "API Key", Required: true)]), new("oauth", "订阅")]),
                    new("xai", "xAI", true, [new("api", "填写信息", [new("secret", "apiKey", "API Key", Required: true)]), new("oauth", "订阅")])
                }, SettingsJson.Type<List<CatalogProvider>>()),
                "/catalog/openai/models" => JsonSerializer.Serialize(new List<CatalogModel> { new("model-a", "A", true, ["default", "high"]), new("model-b", "B", false, []) }, SettingsJson.Type<List<CatalogModel>>()),
                "/catalog/xai/models" => JsonSerializer.Serialize(new List<CatalogModel> { new("xai-model", "X", true, ["low", "high"]), new("xai-simple", "S", false, []) }, SettingsJson.Type<List<CatalogModel>>()),
                "/auth/accounts" => "[]",
                "/auth/xai/credential" => JsonSerializer.Serialize(ApiKey is null ? new CredentialInfo("xai", "", "", "", false) : new CredentialInfo("xai", "api_key", "", "", true), SettingsJson.Type<CredentialInfo>()),
                "/auth/openai/credential" => JsonSerializer.Serialize(new CredentialInfo("openai", "", "", "", false), SettingsJson.Type<CredentialInfo>()),
                "/auth/xai/start" => JsonSerializer.Serialize(new LoginState("login-1", "xai", "https://fixture/authorize", "DEVICE-CODE", "pending", "授权"), SettingsJson.Type<LoginState>()),
                _ => "{}"
            };
            if (path == "/settings" && request.Method == HttpMethod.Put)
                Saved = JsonSerializer.Deserialize(await request.Content!.ReadAsStringAsync(cancellationToken), SettingsJson.Type<AgentConfig>());
            if (path == "/auth/login/login-1/complete")
            {
                Callback = JsonSerializer.Deserialize(await request.Content!.ReadAsStringAsync(cancellationToken), SettingsJson.Type<OAuthCompletion>())!.Input;
                json = JsonSerializer.Serialize(new LoginState("login-1", "xai", "", "", "connected", "已连接"), SettingsJson.Type<LoginState>());
            }
            if (path == "/auth/xai/api-key")
                ApiKey = JsonSerializer.Deserialize(await request.Content!.ReadAsStringAsync(cancellationToken), SettingsJson.Type<ApiKeyRequest>())!.ApiKey;
            return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
