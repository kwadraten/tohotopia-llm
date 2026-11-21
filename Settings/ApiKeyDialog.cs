using Avalonia;
using Avalonia.Automation;
using Tohotopia;

namespace Settings;

internal sealed class ApiKeyDialog : Window
{
    readonly Dictionary<string, Control> fields = new(StringComparer.OrdinalIgnoreCase);
    IReadOnlyList<CatalogProvider> providers = [];
    string providerName;
    ProviderAuthMethod method;
    CredentialInfo? credential;
    Alert? validation;
    internal bool KeepSecret { get; private set; }
    internal bool Editing { get; private set; }
    internal string ProviderId { get; private set; } = "";
    internal Func<string, Task>? ProviderChangeRequested { get; set; }
    internal string Value(string key) => fields.TryGetValue(key, out var field) ? field switch
    {
        TextBox text => (text.Text ?? "").Trim(),
        Select combo => (combo.SelectedItem as AuthPromptOption)?.Value ?? "", _ => ""
    } : "";

    internal ApiKeyDialog(string providerName, ProviderAuthMethod method, CredentialInfo? credential = null, bool editing = false)
    {
        this.providerName = providerName;
        this.method = method;
        this.credential = credential;
        Width = 600; MinWidth = 600; CanResize = false;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Rebuild(editing);
    }

    internal void SetProviders(IEnumerable<CatalogProvider> catalogProviders, string selectedProvider)
    {
        providers = catalogProviders.Where(provider => provider.AuthMethods.Any(method => method.Type == "api")).ToArray();
        ProviderId = selectedProvider;
        Rebuild(Editing);
    }

    internal void SetProvider(string id, string name, ProviderAuthMethod selectedMethod, CredentialInfo? current, bool editing)
    {
        ProviderId = id;
        providerName = name;
        method = selectedMethod;
        credential = current;
        Rebuild(editing);
    }

    void Rebuild(bool editing)
    {
        Editing = editing;
        KeepSecret = editing && credential?.HasSecret == true;
        Title = (editing ? "编辑 API Key" : method.Label) + " — " + providerName;
        fields.Clear();
        validation = null;
        var panel = Ui.Form();
        if (providers.Count > 0)
        {
            var select = new Select
            {
                Name = "Credential_Provider",
                ItemsSource = providers,
                SelectedItem = providers.FirstOrDefault(provider => provider.Id == ProviderId) ?? providers[0],
                TextSelector = item => (item as CatalogProvider)?.Name
            };
            Ui.Row(panel, "提供商", select);
            select.SelectionChanged += async (_, _) =>
            {
                if (select.SelectedItem is not CatalogProvider selected || selected.Id == ProviderId || ProviderChangeRequested is null) return;
                select.IsEnabled = false;
                try { await ProviderChangeRequested(selected.Id); }
                catch (Exception e)
                {
                    if (validation is not null) Ui.Status(validation, "提供商切换失败：" + e.Message, AlertTone.Danger);
                    select.IsEnabled = true;
                }
            };
        }
        var prompts = method.Prompts ?? [];
        foreach (var prompt in prompts)
        {
            bool secret = prompt.Type == "secret" || prompt.Key.Equals("apiKey", StringComparison.OrdinalIgnoreCase);
            string initial = secret ? "" : prompt.Key.ToLowerInvariant() switch
            {
                "baseurl" when !string.IsNullOrEmpty(credential?.BaseUrl) => credential.BaseUrl,
                "workspaceid" when !string.IsNullOrEmpty(credential?.WorkspaceId) => credential.WorkspaceId,
                _ => prompt.DefaultValue ?? ""
            };
            Control field;
            if (prompt.Type == "select")
            {
                var options = prompt.Options ?? [];
                field = new Select
                {
                    ItemsSource = options,
                    SelectedItem = options.FirstOrDefault(option => option.Value == initial) ?? options.FirstOrDefault(),
                    TextSelector = option => (option as AuthPromptOption)?.Label
                };
            }
            else
                field = new Input { PasswordChar = secret ? '●' : '\0',
                    PlaceholderText = prompt.Placeholder ?? "", Text = initial };
            field.Name = "Credential_" + prompt.Key;
            AutomationProperties.SetName(field, prompt.Message);
            fields[prompt.Key] = field;
            Ui.Row(panel, prompt.Message, field);
        }
        string note = KeepSecret
            ? "凭据将使用当前 Windows 用户的 DPAPI 加密保存。旧 API Key 不会显示；留空保留原密钥，输入新密钥则替换。"
            : "凭据将使用当前 Windows 用户的 DPAPI 加密保存。";
        if (!editing && !string.IsNullOrEmpty(credential?.AuthType) && credential.AuthType != "api_key")
            note += "当前使用订阅授权；只有点击保存才会切换为 API Key。";
        Ui.Row(panel, "", Ui.Label(note));
        validation = Ui.StatusAlert("CredentialValidation");
        validation.Title = "无法保存凭据";
        validation.Tone = AlertTone.Danger;
        validation.IsOpen = false;
        Ui.Row(panel, "", validation);
        var save = Ui.Button("保存", () =>
        {
            var missing = prompts.FirstOrDefault(prompt => prompt.Required && string.IsNullOrWhiteSpace(Value(prompt.Key))
                && !(KeepSecret && prompt.Key.Equals("apiKey", StringComparison.OrdinalIgnoreCase)));
            if (missing is not null) { Ui.Status(validation, $"请输入{missing.Message}。", AlertTone.Danger); return; }
            Close(true);
        }, ButtonVariant.Primary);
        save.IsDefault = true;
        var cancel = Ui.Button("取消", () => Close(false));
        cancel.IsCancel = true;
        var buttons = Ui.Buttons(save, cancel);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        Ui.Row(panel, "", buttons);
        panel.Margin = new Thickness(24, 20, 16, 12);
        Content = panel;
    }
}
