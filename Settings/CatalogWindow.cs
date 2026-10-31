using System.Net.Http.Json;
using Tohotopia;
namespace Settings;

public sealed partial class SettingsWindow
{
    List<CatalogProvider> catalogProviders = new();
    readonly Dictionary<string, List<CatalogModel>> modelCache = new();
    bool refreshingSeats;
    static string[] Efforts(CatalogModel? model) => model?.Efforts.Length > 0 ? model.Efforts : new[] { "default" };
    static string CatalogProviderId(string p) => p switch { "grok" => "xai", "kimi" => "kimi-code-plan-global", _ => p };
    async Task<List<CatalogModel>> GetModels(string p)
    {
        p = CatalogProviderId(p);
        if (modelCache.TryGetValue(p, out var found))
            return found;
        using var response = await http.GetAsync("/catalog/" + Uri.EscapeDataString(p) + "/models");
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(await response.Content.ReadAsStringAsync());
        var list = await response.Content.ReadFromJsonAsync<List<CatalogModel>>(Files.Json) ?? new();
        modelCache[p] = list;
        return list;
    }
    async Task LoadCatalog()
    {
        catalogProviders = await http.GetFromJsonAsync<List<CatalogProvider>>("/catalog/providers", Files.Json) ?? new();
        provider.DisplayMember = "Name";
        provider.ValueMember = "Id";
        provider.DataSource = catalogProviders;
        var column = (DataGridViewComboBoxColumn)seats.Columns["provider"];
        column.DisplayMember = "Name";
        column.ValueMember = "Id";
        column.DataSource = catalogProviders.Where(p => p.ConnectionSupported).ToList();
        await RefreshSeatModels();
    }
    async Task RefreshSeatModels()
    {
        if (refreshingSeats)
            return;
        refreshingSeats = true;
        try
        {
            foreach (DataGridViewRow row in seats.Rows)
            {
                string p = CatalogProviderId(row.Cells["provider"].Value?.ToString() ?? "openai");
                if (row.Cells["provider"].Value?.ToString() != p)
                    row.Cells["provider"].Value = p;
                var list = await GetModels(p);
                var cell = (DataGridViewComboBoxCell)row.Cells["model"];
                string selected = cell.Value?.ToString() ?? "";
                cell.DisplayMember = "Id";
                cell.ValueMember = "Id";
                cell.Value = null;
                cell.DataSource = list;
                var model = list.FirstOrDefault(m => m.Id == selected) ?? list.FirstOrDefault();
                cell.Value = model?.Id;
                var effort = (DataGridViewComboBoxCell)row.Cells["reasoning"];
                string selectedEffort = effort.Value?.ToString() ?? "";
                string[] levels = Efforts(model);
                effort.Value = null;
                effort.DataSource = levels;
                effort.Value = levels.Contains(selectedEffort) ? selectedEffort : levels[0];
            }
        }
        catch (Exception e) { roomStatus.Text = "模型目录读取失败：" + e.Message; }
        finally { refreshingSeats = false; }
    }
}
