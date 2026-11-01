using System;
using System.Collections.Generic;
using TMPro;
using TohotopiaShared;
using N = TohotopiaMod.Native;

namespace TohotopiaMod;

// Only changes the local ranking label, never the synchronized player data.
internal sealed class ModelNameDisplay
{
    readonly Dictionary<IntPtr, (string Original, string Applied)> labels = new();
    readonly Dictionary<IntPtr, (string Original, string Applied)> lobbyLabels = new();
    bool warned;
    IntPtr turnLabel;
    string turnOriginal = "", turnApplied = "";
    bool turnWarned;

    internal void PollTurn(NativeAiBridge room, ModelTurnFallback fallback)
    {
        try
        {
            var ui = N.Static("TH1_Core.Managers.UIManager", "<Instance>k__BackingField");
            var pointer = N.Get<IntPtr>(ui, "_aiPlayingHintMessage");
            if (pointer == IntPtr.Zero) { turnLabel = IntPtr.Zero; return; }
            var label = new TextMeshProUGUI(pointer);
            string original = label.text ?? "";
            if (turnLabel == pointer && original == turnApplied && turnApplied.Length > 0)
            {
                original = turnOriginal;
                label.text = original;
            }
            turnLabel = pointer;
            turnOriginal = turnApplied = "";
            if (!room.ModelsEnabled || !label.gameObject.activeInHierarchy) return;
            var map = N.Static("TH1_Logic.Core.Main", "MapData");
            if (map == IntPtr.Zero) return;
            var player = N.Call(map, "get_ivn");
            if (player == IntPtr.Zero || fallback.IsNative(N.Id(player), N.Get<uint>(player, "Turn"))) return;
            string model = ChatActions.ModelLabel(room.ModelName(N.Id(player)));
            if (model.Length == 0 || string.IsNullOrWhiteSpace(original)) return;
            turnOriginal = original;
            turnApplied = original + "（" + model + "）";
            label.text = turnApplied;
        }
        catch (Exception e)
        {
            if (!turnWarned) Battle.Log.LogWarning("无法更新模型回合提示：" + e.Message);
            turnWarned = true;
        }
    }

    internal void Poll(NativeAiBridge room, IntPtr lobbyView)
    {
        try
        {
            PollLobby(room, lobbyView);
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

    void PollLobby(NativeAiBridge room, IntPtr view)
    {
        var data = N.Items(N.Get<IntPtr>(view, "_roomMemberRows"));
        var rows = N.Items(N.Get<IntPtr>(view, "_memberRowList"));
        var models = room.LobbyModelNames();
        var current = new HashSet<IntPtr>();
        int rowIndex = 0;
        foreach (var item in data)
        {
            if (item == IntPtr.Zero || N.Get<int>(item, "Type") == 3) continue;
            if (rowIndex >= rows.Count) break;
            var row = rows[rowIndex++];
            var pointer = N.Get<IntPtr>(row, "NameText");
            if (pointer == IntPtr.Zero) continue;
            current.Add(pointer);
            var label = new TextMeshProUGUI(pointer);
            string original = label.text ?? "";
            if (lobbyLabels.TryGetValue(pointer, out var previous) && original == previous.Applied)
            {
                original = previous.Original;
                label.text = original;
            }
            lobbyLabels.Remove(pointer);
            var slot = N.Get<IntPtr>(item, "MemberCiv");
            if (slot == IntPtr.Zero || N.Get<int>(item, "Type") != 4 ||
                !label.gameObject.activeInHierarchy ||
                !models.TryGetValue(N.Get<int>(slot, "Index"), out var model) ||
                string.IsNullOrWhiteSpace(model))
                continue;
            string applied = model.Replace('<', '＜').Replace('>', '＞');
            label.text = applied;
            lobbyLabels[pointer] = (original, applied);
        }
        foreach (var pointer in new List<IntPtr>(lobbyLabels.Keys))
            if (!current.Contains(pointer)) lobbyLabels.Remove(pointer);
    }
}
