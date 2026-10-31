using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Tohotopia;

/// <summary>Renders the explored portion of an observation as a compact, coordinate-stable map.</summary>
public static class SpatialTable
{
    public static string Format(JsonElement observation)
    {
        int width = ReadDimension(observation, "mapWidth");
        int height = ReadDimension(observation, "mapHeight");
        var byCoordinate = new Dictionary<(int X, int Y), JsonElement>();
        if (observation.TryGetProperty("grids", out var grids) && grids.ValueKind == JsonValueKind.Array)
        {
            foreach (var grid in grids.EnumerateArray())
            {
                if (!grid.TryGetProperty("x", out var xValue) || !xValue.TryGetInt32(out int x) ||
                    !grid.TryGetProperty("y", out var yValue) || !yValue.TryGetInt32(out int y))
                    continue;
                byCoordinate[(x, y)] = grid;
                width = Math.Max(width, x + 1);
                height = Math.Max(height, y + 1);
            }
        }
        if (width <= 0 || height <= 0)
            return "地图未知（观测中没有地图尺寸或坐标）";

        var unitsByGrid = new Dictionary<uint, List<string>>();
        if (observation.TryGetProperty("units", out var units) && units.ValueKind == JsonValueKind.Array)
        {
            foreach (var unit in units.EnumerateArray())
            {
                if (!unit.TryGetProperty("gridId", out var gidValue) || !gidValue.TryGetUInt32(out uint gid)) continue;
                if (!unit.TryGetProperty("id", out var idValue) || !idValue.TryGetUInt32(out uint id)) continue;
                string name = unit.TryGetProperty("name", out var nameValue) && nameValue.ValueKind == JsonValueKind.String
                    ? nameValue.GetString()! : "单位";
                if (!unitsByGrid.TryGetValue(gid, out var list)) unitsByGrid[gid] = list = new();
                list.Add(name + id.ToString(CultureInfo.InvariantCulture));
            }
        }

        var output = new StringBuilder();
        output.Append("x → ");
        for (int x = 0; x < width; x++) output.Append('|').Append(x);
        output.AppendLine("|");
        for (int y = 0; y < height; y++)
        {
            output.Append(y).Append(" | ");
            for (int x = 0; x < width; x++)
            {
                if (!byCoordinate.TryGetValue((x, y), out var grid))
                {
                    output.Append("未知");
                }
                else
                {
                    var parts = new List<string>();
                    var layers = new List<string>();
                    AddString(grid, "terrainName", layers);
                    AddString(grid, "landformName", layers);
                    AddString(grid, "vegetationName", layers);
                    AddString(grid, "buildingName", layers);
                    AddString(grid, "resourceName", layers);
                    AddString(grid, "roadName", layers);
                    // Compatibility with older observations that only have terrain/resource/building.
                    if (layers.Count == 0)
                    {
                        AddNonEmpty(grid, "terrain", layers);
                        AddNonEmpty(grid, "resource", layers);
                        AddNonEmpty(grid, "building", layers);
                    }
                    if (grid.TryGetProperty("id", out var cellId) && cellId.TryGetUInt32(out uint cellGridId))
                        parts.Add("格" + cellGridId.ToString(CultureInfo.InvariantCulture));
                    parts.AddRange(layers);
                    List<string>? occupants = null;
                    bool hasOccupants = grid.TryGetProperty("id", out var idValue) && idValue.TryGetUInt32(out uint gridId) && unitsByGrid.TryGetValue(gridId, out occupants);
                    if (hasOccupants)
                        parts.AddRange(occupants!);
                    if (layers.Count == 0 && !hasOccupants) parts.Add("空");
                    output.Append(parts.Count == 0 ? "空" : string.Join(' ', parts));
                }
                output.Append('|');
            }
            if (y + 1 < height) output.AppendLine();
        }
        return output.ToString();
    }

    static int ReadDimension(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out int dimension) && dimension > 0 ? dimension : 0;

    static void AddString(JsonElement grid, string name, List<string> parts)
    {
        if (grid.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value.GetString()) && value.GetString() is not ("None" or "无" or "0"))
            parts.Add(value.GetString()!);
    }

    static void AddNonEmpty(JsonElement grid, string name, List<string> parts)
    {
        if (grid.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value.GetString()) && value.GetString() is not ("None" or "无" or "0"))
            parts.Add(value.GetString()!);
    }
}
