using Avalonia;
using Avalonia.Automation;
using TohotopiaShared;
using Tohotopia;
namespace Settings;

public sealed partial class SettingsWindow
{
    readonly NumberField aiCount = new() { Name = "SeatCount", Minimum = 1, Maximum = 4, Value = 2, Step = 1, MaximumFractionDigits = 0, UseGrouping = false, MaxWidth = 220, HorizontalAlignment = HorizontalAlignment.Left };
    readonly Checkbox models = new() { Content = "对局开始时自动启用模型", IsChecked = true };
    readonly StackPanel seats = new() { Spacing = 4 };
    readonly List<SeatControls> seatRows = new();
    const string SeatColumns = "36,*,2*,112";
    readonly Alert actionStatus = Ui.StatusAlert("ActionStatus"), roomStatus = Ui.StatusAlert("RoomStatus");
    readonly DispatcherTimer roomPoll = new() { Interval = TimeSpan.FromSeconds(1) };

    Control BuildRoomPage()
    {
        models.Name = "UseModels";
        actionStatus.Name = "ActionStatus";
        roomStatus.Name = "RoomStatus";
        var guide = Ui.Form();
        Ui.Row(guide, "使用方式", Ui.Label("1. 在账号管理中连接提供商。  2. 配置下方模型并保存。\n3. 由房主在游戏内创建 Steam 房间、添加 AI，并在游戏内开始对局。"));
        Ui.Row(guide, "席位对应", Ui.Label("按游戏中可接管的 AI 席位顺序，从上到下依次分配模型。房间中的 AI 数量须不少于配置数量；文明、队伍和地图均在游戏中设置。"));
        Ui.Row(guide, "非回合通讯", Ui.Label("模型在回合外收到新消息时回应，会额外调用模型。公共消息由房主标注 AI 席位代发；同盟消息仅发送给 AI 的真人盟友。"));
        var seatsForm = Ui.Form();
        Ui.Row(seatsForm, "接管数量", aiCount);
        Ui.Row(seatsForm, "模型分配", seats);
        Ui.Row(seatsForm, "模型控制", models);
        var status = Ui.Form();
        Ui.Row(status, "房间状态", roomStatus);
        Ui.Row(status, "控制说明", Ui.Label("启用模型／恢复原生 AI 可在对局中切换；停用配置会结束接管。修改模型配置前请先恢复原生 AI。", true));
        Ui.Row(status, "操作提示", actionStatus);
        var help = new Accordion { ExpandedIndex = -1, Margin = new Thickness(0, 0, 0, 2) };
        help.Items.Add(new AccordionItem { Header = "使用说明", Content = guide });
        var panel = Ui.Page(help, Ui.Section("AI 席位", seatsForm), Ui.Section("状态与操作", status));
        aiCount.ValueChanged += async (_, _) => { ResizeSeats(); if (catalogProviders.Count > 0) await RefreshSeatModels(); };
        ResizeSeats();
        var saved = RoomFiles.Read<RoomConfig>(Files.Root, "room.json");
        if (saved?.NativeLobby == true && saved.AIs.Count > 0)
        {
            models.IsChecked = saved.UseModels;
            aiCount.Value = Math.Clamp(saved.AIs.Count, 1, 4);
            ResizeSeats();
            for (int i = 0; i < seatRows.Count; i++)
            {
                seatRows[i].ProviderId = saved.AIs[i].Provider;
                seatRows[i].ModelId = saved.AIs[i].Model;
                seatRows[i].Reasoning = saved.AIs[i].Reasoning;
            }
        }
        bool legacy = saved != null && !saved.NativeLobby;
        roomStatus.Title = legacy ? "需要更新旧版配置" : "等待保存接管配置";
        Ui.Status(roomStatus, legacy
            ? "检测到旧版虚拟房间配置，请重新选择模型并保存接管配置。"
            : "请配置模型并保存，再由房主在游戏中创建 Steam 房间、添加 AI 并开始对局。", legacy ? AlertTone.Warning : AlertTone.Info);
        actionStatus.Title = "接管操作";
        Ui.Status(actionStatus, "接管命令已移至窗口顶部 Ribbon。操作结果会完整显示在这里。", AlertTone.Info);
        roomPoll.Tick += (_, _) =>
        {
            try
            {
                var state = RoomFiles.Read<RoomStatus>(Files.Root, "room-status.json");
                if (state != null)
                {
                    roomStatus.Title = "房间状态已更新";
                    Ui.Status(roomStatus, state.Message + $"\n状态：{state.State} · 可用／已接管 AI：{state.AIPlayers} · 模型接管：{(state.ModelsEnabled ? "启用" : "未启用")}", state.ModelsEnabled ? AlertTone.Success : AlertTone.Info);
                }
            }
            catch (Exception e) when (e is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { }
        };
        roomPoll.Start();
        return panel;
    }

    Control[] RoomCommands()
    {
        Button CommandButton(string name, Action action, ButtonVariant variant = ButtonVariant.Secondary) => Ui.Button(name, () =>
        {
            try { action(); }
            catch (Exception e)
            {
                actionStatus.Title = "接管操作失败";
                Ui.Status(actionStatus, e.Message, AlertTone.Danger);
            }
        }, variant);
        return [CommandButton("保存接管配置", PublishRoom, ButtonVariant.Primary), CommandButton("启用模型接管", () => Command("models")),
            CommandButton("恢复原生 AI", () => Command("native")), CommandButton("停用接管配置", StopRoom)];
    }

    void ResizeSeats()
    {
        int count = (int)aiCount.Value;
        while (seatRows.Count > count) seatRows.RemoveAt(seatRows.Count - 1);
        while (seatRows.Count < count)
        {
            var seat = new SeatControls(seatRows.Count + 1);
            seat.Provider.SelectionChanged += async (_, _) =>
            {
                if (refreshingSeats || seat.Provider.SelectedItem is not CatalogProvider selected) return;
                seat.ProviderId = selected.Id;
                await RefreshSeatModels();
            };
            seat.Model.SelectionChanged += async (_, _) =>
            {
                if (refreshingSeats || seat.Model.SelectedItem is not CatalogModel selected) return;
                seat.ModelId = selected.Id;
                await RefreshSeatModels();
            };
            seat.Effort.SelectionChanged += (_, _) => { if (!refreshingSeats && seat.Effort.SelectedItem is string value) seat.Reasoning = value; };
            seatRows.Add(seat);
        }
        seats.Children.Clear();
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions(SeatColumns), ColumnSpacing = 8 };
        int col = 0;
        foreach (string caption in new[] { "顺序", "提供商", "模型", "推理" })
        {
            var label = Ui.Label(caption); Grid.SetColumn(label, col++); header.Children.Add(label);
        }
        seats.Children.Add(header);
        foreach (var seat in seatRows) seats.Children.Add(seat.View);
    }

    void PublishRoom()
    {
        if (RoomFiles.Read<RoomStatus>(Files.Root, "room-status.json")?.ModelsEnabled == true)
            throw new InvalidOperationException("请先恢复原生 AI，再修改接管配置。");
        var ai = new List<AISeat>();
        foreach (var row in seatRows)
        {
            if (!catalogProviders.Any(p => p.Id == row.ProviderId && p.ConnectionSupported)
                || !modelCache.TryGetValue(row.ProviderId, out var available) || !available.Any(m => m.Id == row.ModelId))
                throw new InvalidOperationException("请先从模型目录选择可连接的提供商和模型");
            ai.Add(new() { Provider = row.ProviderId, Model = row.ModelId.Trim(), Reasoning = row.Reasoning });
        }
        var config = new RoomConfig { NativeLobby = true, Published = true, UseModels = models.IsChecked == true, AIs = ai };
        config.Validate();
        RoomFiles.Write(Files.Root, "room.json", config);
        actionStatus.Title = "接管配置已保存";
        Ui.Status(actionStatus, "请由房主在游戏中创建 Steam 房间、添加 AI 并开始对局。", AlertTone.Success);
    }
    void StopRoom()
    {
        var config = RoomFiles.Read<RoomConfig>(Files.Root, "room.json");
        if (config?.NativeLobby == true) RoomFiles.Write(Files.Root, "room.json", config with { Published = false, Revision = Guid.NewGuid().ToString("N") });
        actionStatus.Title = "接管配置已停用";
        Ui.Status(actionStatus, "已停止接管，原生房间不受影响。", AlertTone.Success);
    }
    void Command(string action)
    {
        var config = RoomFiles.Read<RoomConfig>(Files.Root, "room.json");
        if (config?.NativeLobby != true || !config.Published) throw new InvalidOperationException("请先保存接管配置，再切换 AI 控制方式。");
        RoomFiles.Write(Files.Root, "room-command.json", new RoomCommand(Guid.NewGuid().ToString("N"), action));
        actionStatus.Title = action == "models" ? "已请求启用模型接管" : "已请求恢复原生 AI";
        Ui.Status(actionStatus, "命令已保存，等待游戏响应。", AlertTone.Info);
    }

    sealed class SeatControls
    {
        internal string ProviderId = "openai", ModelId = "", Reasoning = "default";
        internal readonly Select Provider, Model, Effort;
        internal readonly Grid View = new() { ColumnDefinitions = new ColumnDefinitions(SeatColumns), ColumnSpacing = 8 };
        internal SeatControls(int slot)
        {
            Provider = new Select { Name = $"Seat{slot}Provider", TextSelector = p => (p as CatalogProvider)?.Name };
            Model = new Select { Name = $"Seat{slot}Model", TextSelector = m => (m as CatalogModel)?.Id };
            Effort = new Select { Name = $"Seat{slot}Reasoning", ItemsSource = new[] { "default" }, SelectedIndex = 0 };
            var controls = new Control[] { Ui.Label(slot.ToString()), Provider, Model, Effort };
            for (int i = 0; i < controls.Length; i++)
            {
                Grid.SetColumn(controls[i], i);
                controls[i].HorizontalAlignment = HorizontalAlignment.Stretch;
                if (controls[i] is Select select) { select.MinWidth = 0; select.Size = ControlSize.Small; }
                AutomationProperties.SetName(controls[i], $"AI {slot} " + new[] { "顺序", "提供商", "模型", "推理" }[i]);
                View.Children.Add(controls[i]);
            }
        }
    }
}
