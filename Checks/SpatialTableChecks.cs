using System.Text.Json;
using Tohotopia;

static class SpatialTableChecks
{
    public static void Run()
    {
        using var observation = JsonDocument.Parse("""{"mapWidth":3,"mapHeight":2,"grids":[{"id":11,"x":0,"y":0,"terrainName":"陆地","vegetationName":"森林","buildingName":"村庄","resourceName":"","roadName":"道路"},{"id":12,"x":2,"y":0,"terrainName":"None","vegetationName":"None","buildingName":"","resourceName":"","roadName":""}],"units":[{"id":331,"gridId":11,"name":"单位"}]}""");
        var table = SpatialTable.Format(observation.RootElement);
        Check(table.Contains("|0|1|2|") && table.Contains("0 | 格11 陆地") && table.Contains("单位331") && table.Contains("陆地 森林 村庄 道路"), "spatial table uses x/y coordinates, grid IDs, and populated layers only");
        Check(table.Contains("格12 空|") && table.Contains("1 | 未知|未知|未知|"), "known empty cells differ from coordinates with unknown visibility");
        using var legacy = JsonDocument.Parse("""{"grids":[{"id":245,"x":2,"y":3,"terrain":"Land","resource":"Fruit","building":"None"}]}""");
        Check(SpatialTable.Format(legacy.RootElement).Contains("3 | 未知|未知|格245 Land Fruit|"), "legacy grid coordinates and fields remain readable");
    }

    static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
        Console.WriteLine("PASS " + message);
    }
}
