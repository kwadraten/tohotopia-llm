using System;
using System.Collections.Generic;
using TohotopiaShared;
using N = TohotopiaMod.Native;

namespace TohotopiaMod;

// Only changes the local ranking label, never the synchronized player data.
internal sealed class ModelNameDisplay
{
    readonly Dictionary<object, (string Original, string Applied)> labels = new();
    readonly Dictionary<object, (string Original, string Applied)> lobbyLabels = new();
    bool warned;
    object turnLabel;
    string turnOriginal = "", turnApplied = "";
    bool turnWarned;

    internal void PollTurn(NativeAiBridge room, ModelTurnFallback fallback)
    {
        try
        {
            var ui = N.Static("TH1_Core.Managers.UIManager", "<Instance>k__BackingField");
            var pointer = N.Get<object>(ui, "_aiPlayingHintMessage");
            if (pointer == null) { turnLabel = null; return; }
            var label = new GameLabel(pointer);
            string original = label.Text ?? "";
            if (turnLabel == pointer && original == turnApplied && turnApplied.Length > 0)
            {
                original = turnOriginal;
                label.Text = original;
            }
            turnLabel = pointer;
            turnOriginal = turnApplied = "";
            if (!room.ModelsEnabled || !label.Active) return;
            var map = N.Static("TH1_Logic.Core.Main", "MapData");
            if (map == null) return;
            var player = N.Call(map, "get_ivn");
            if (player == null || fallback.IsNative(N.Id(player), N.Get<uint>(player, "Turn"))) return;
            string model = ChatActions.ModelLabel(room.ModelName(N.Id(player)));
            if (model.Length == 0 || string.IsNullOrWhiteSpace(original)) return;
            turnOriginal = original;
            turnApplied = original + "（" + model + "）";
            label.Text = turnApplied;
        }
        catch (Exception e)
        {
            if (!turnWarned) Battle.Log.LogWarning("无法更新模型回合提示：" + e.Message);
            turnWarned = true;
        }
    }

    internal void Poll(NativeAiBridge room, object lobbyView)
    {
        try
        {
            PollLobby(room, lobbyView);
            var controller = N.Static("TH1_UI.Core.ViewControllerManager", "_bottomRankingController");
            var view = N.Get<object>(controller, "_script");
            var rows = N.Items(N.Get<object>(view, "Rows"));
            var current = new HashSet<object>();
            foreach (var row in rows)
            {
                if (row == null) continue;
                var pointer = N.Get<object>(row, "ForceName");
                if (pointer == null) continue;
                current.Add(pointer);
                var label = new GameLabel(pointer);
                string original = label.Text ?? "";
                if (labels.TryGetValue(pointer, out var previous) && original == previous.Applied)
                {
                    original = previous.Original;
                    label.Text = original;
                }
                labels.Remove(pointer);
                if (!room.ModelsEnabled || N.Get<int>(row, "_entryMode") != 0 ||
                    !label.Active || string.IsNullOrWhiteSpace(original) || original == "???")
                    continue;
                var player = N.Get<object>(row, "_player");
                if (player == null) continue;
                string model = room.ModelName(N.Id(player));
                if (string.IsNullOrWhiteSpace(model)) continue;
                // Model IDs are untrusted text; do not allow TextMeshPro markup.
                model = model.Replace('<', '＜').Replace('>', '＞');
                string applied = model;
                label.Text = applied;
                labels[pointer] = (original, applied);
            }
            foreach (var pointer in new List<object>(labels.Keys))
                if (!current.Contains(pointer)) labels.Remove(pointer);
        }
        catch (Exception e)
        {
            if (!warned) Battle.Log.LogWarning("无法更新本地模型名称：" + e.Message);
            warned = true;
        }
    }

    void PollLobby(NativeAiBridge room, object view)
    {
        var data = N.Items(N.Get<object>(view, "_roomMemberRows"));
        var rows = N.Items(N.Get<object>(view, "_memberRowList"));
        var models = room.LobbyModelNames();
        var current = new HashSet<object>();
        int rowIndex = 0;
        foreach (var item in data)
        {
            if (item == null || N.Get<int>(item, "Type") == 3) continue;
            if (rowIndex >= rows.Count) break;
            var row = rows[rowIndex++];
            var pointer = N.Get<object>(row, "NameText");
            if (pointer == null) continue;
            current.Add(pointer);
            var label = new GameLabel(pointer);
            string original = label.Text ?? "";
            if (lobbyLabels.TryGetValue(pointer, out var previous) && original == previous.Applied)
            {
                original = previous.Original;
                label.Text = original;
            }
            lobbyLabels.Remove(pointer);
            var slot = N.Get<object>(item, "MemberCiv");
            if (slot == null || N.Get<int>(item, "Type") != 4 ||
                !label.Active ||
                !models.TryGetValue(N.Get<int>(slot, "Index"), out var model) ||
                string.IsNullOrWhiteSpace(model))
                continue;
            string applied = model.Replace('<', '＜').Replace('>', '＞');
            label.Text = applied;
            lobbyLabels[pointer] = (original, applied);
        }
        foreach (var pointer in new List<object>(lobbyLabels.Keys))
            if (!current.Contains(pointer)) lobbyLabels.Remove(pointer);
    }
}
