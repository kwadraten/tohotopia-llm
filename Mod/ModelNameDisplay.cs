using System;
using System.Collections.Generic;
using TMPro;
using N = TohotopiaMod.Native;

namespace TohotopiaMod;

// Only changes the local ranking label, never the synchronized player data.
internal sealed class ModelNameDisplay
{
    readonly Dictionary<IntPtr, (string Original, string Applied)> labels = new();
    bool warned;

    internal void Poll(NativeAiBridge room)
    {
        try
        {
            var controller = N.Static("TH1_UI.Core.ViewControllerManager", "_bottomRankingController");
            var view = N.Get<IntPtr>(controller, "_script");
            var rows = N.Items(N.Get<IntPtr>(view, "Rows"));
            var current = new HashSet<IntPtr>();
            foreach (var row in rows)
            {
                if (row == IntPtr.Zero) continue;
                var pointer = N.Get<IntPtr>(row, "ForceName");
                if (pointer == IntPtr.Zero) continue;
                current.Add(pointer);
                var label = new TextMeshProUGUI(pointer);
                string original = label.text ?? "";
                if (labels.TryGetValue(pointer, out var previous) && original == previous.Applied)
                {
                    original = previous.Original;
                    label.text = original;
                }
                labels.Remove(pointer);
                if (!room.ModelsEnabled || N.Get<int>(row, "_entryMode") != 0 ||
                    !label.gameObject.activeInHierarchy || string.IsNullOrWhiteSpace(original) || original == "???")
                    continue;
                var player = N.Get<IntPtr>(row, "_player");
                if (player == IntPtr.Zero) continue;
                string model = room.ModelName(N.Id(player));
                if (string.IsNullOrWhiteSpace(model)) continue;
                // Model IDs are untrusted text; do not allow TextMeshPro markup.
                model = model.Replace('<', '＜').Replace('>', '＞');
                string applied = model;
                label.text = applied;
                labels[pointer] = (original, applied);
            }
            foreach (var pointer in new List<IntPtr>(labels.Keys))
                if (!current.Contains(pointer)) labels.Remove(pointer);
        }
        catch (Exception e)
        {
            if (!warned) Battle.Log.LogWarning("无法更新本地模型名称：" + e.Message);
            warned = true;
        }
    }
}
