using System.Net.Http.Json;
using Tohotopia;
namespace Settings;

public sealed partial class SettingsWindow
{
    List<CatalogProvider> catalogProviders = new();
    readonly Dictionary<string, List<CatalogModel>> modelCache = new();
    bool refreshingSeats, refreshPending;
    static string[] Efforts(CatalogModel? model) => model?.Efforts.Length > 0 ? model.Efforts : new[] { "default" };
    static string CatalogProviderId(string p) => p switch { "grok" => "xai", "kimi" => "kimi-code-plan-global", _ => p };
    async Task<List<CatalogModel>> GetModels(string p)
    {
        p = CatalogProviderId(p);
        if (modelCache.TryGetValue(p, out var found)) return found;
        using var response = await http.GetAsync("/catalog/" + Uri.EscapeDataString(p) + "/models");
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await response.Content.ReadAsStringAsync());
        var list = await response.Content.ReadFromJsonAsync(SettingsJson.Type<List<CatalogModel>>()) ?? new();
        modelCache[p] = list;
        return list;
    }
    async Task LoadCatalog()
    {
        catalogProviders = await http.GetFromJsonAsync("/catalog/providers", SettingsJson.Type<List<CatalogProvider>>()) ?? new();
        provider.ItemsSource = catalogProviders;
        await RefreshSeatModels();
    }
    async Task RefreshSeatModels()
    {
        refreshPending = true;
        if (refreshingSeats) return;
        refreshingSeats = true;
        try
        {
            do
            {
                refreshPending = false;
                foreach (var row in seatRows.ToArray())
                {
                    string p = CatalogProviderId(row.ProviderId);
                    var list = await GetModels(p);
                    if (!seatRows.Contains(row)) continue;
                    row.ProviderId = p;
                    row.Provider.ItemsSource = catalogProviders.Where(item => item.ConnectionSupported).ToArray();
                    row.Provider.SelectedItem = catalogProviders.FirstOrDefault(item => item.Id == p && item.ConnectionSupported);
                    var model = list.FirstOrDefault(m => m.Id == row.ModelId) ?? list.FirstOrDefault();
                    row.Model.ItemsSource = list;
                    row.Model.SelectedItem = model;
                    row.ModelId = model?.Id ?? "";
                    string[] levels = Efforts(model);
                    row.Reasoning = levels.Contains(row.Reasoning) ? row.Reasoning : levels[0];
                    row.Effort.ItemsSource = levels;
                    row.Effort.SelectedItem = row.Reasoning;
                }
            } while (refreshPending && !closed);
        }
        catch (Exception e)
        {
            roomStatus.Title = "模型目录读取失败";
            Ui.Status(roomStatus, e.Message, AlertTone.Danger);
        }
        finally { refreshingSeats = false; }
    }
}
