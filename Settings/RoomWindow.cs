using TohotopiaShared;
using Tohotopia;
namespace Settings;

public sealed partial class SettingsWindow
{
    readonly NumericUpDown aiCount = new() { Minimum = 1, Maximum = 4, Value = 2 };
    readonly CheckBox models = new() { Text = "对局开始时自动启用模型", Checked = true };
    readonly DataGridView seats = new() { Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowHeadersVisible = false };
    readonly TextBox actionStatus = new() { ReadOnly = true, Dock = DockStyle.Fill };
    readonly TextBox roomStatus = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    readonly System.Windows.Forms.Timer roomPoll = new() { Interval = 1000 };
    Control BuildRoomPage()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(15), ColumnCount = 2, AutoScroll = true };
        panel.ColumnStyles.Add(new(SizeType.Absolute, 140));
        panel.ColumnStyles.Add(new(SizeType.Percent, 100));
        void Row(string label, Control control, int h = 36)
        {
            int r = panel.RowCount++;
            panel.RowStyles.Add(new(SizeType.Absolute, h));
            panel.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, r);
            control.Dock = DockStyle.Fill;
            panel.Controls.Add(control, 1, r);
        }
        Row("使用方式", new Label { Text = "1. 在账号管理中连接提供商。  2. 配置下方模型并保存。\r\n3. 由房主在游戏内创建 Steam 房间、添加 AI，并在游戏内开始对局。", TextAlign = ContentAlignment.MiddleLeft }, 68);
        Row("席位对应", new Label { Text = "按游戏中可接管的 AI 席位顺序，从上到下依次分配模型。房间中的 AI 数量须不少于配置数量；文明、队伍和地图均在游戏中设置。", TextAlign = ContentAlignment.MiddleLeft }, 68);
        Row("非回合通讯", new Label { Text = "模型在回合外收到新消息时回应，会额外调用模型。公共消息由房主标注 AI 席位代发；同盟消息仅发送给 AI 的真人盟友。", TextAlign = ContentAlignment.MiddleLeft }, 72);
        Row("接管数量", aiCount);
        seats.Columns.Add(new DataGridViewTextBoxColumn { Name = "slot", HeaderText = "接管顺序", ReadOnly = true, FillWeight = 35 });
        seats.Columns.Add(new DataGridViewComboBoxColumn { Name = "provider", HeaderText = "提供商", FillWeight = 90 });
        seats.Columns.Add(new DataGridViewComboBoxColumn { Name = "model", HeaderText = "模型", FillWeight = 160 });
        seats.Columns.Add(new DataGridViewComboBoxColumn { Name = "reasoning", HeaderText = "推理", DataSource = new[] { "default" }, FillWeight = 65 });
        seats.DataError += (_, e) => { e.ThrowException = false; };
        seats.CurrentCellDirtyStateChanged += (_, _) => { if (seats.IsCurrentCellDirty) seats.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        seats.CellValueChanged += async (_, e) => { if (e.RowIndex >= 0 && !refreshingSeats && catalogProviders.Count > 0 && (e.ColumnIndex == seats.Columns["provider"].Index || e.ColumnIndex == seats.Columns["model"].Index)) { await RefreshSeatModels(); } };
        Row("模型分配", seats, 210);
        Row("模型控制", models);
        var buttons = new FlowLayoutPanel();
        Button Button(string name, Action action)
        {
            var b = new Button { Text = name, AutoSize = true };
            b.Click += (_, _) => { try { action(); } catch (Exception e) { actionStatus.Text = e.Message; } };
            buttons.Controls.Add(b);
            return b;
        }
        Button("保存接管配置", PublishRoom);
        Button("启用模型接管", () => Command("models"));
        Button("恢复原生 AI", () => Command("native"));
        Button("停用接管配置", StopRoom);
        Row("接管控制", buttons, 72);
        Row("控制说明", new Label { Text = "启用模型／恢复原生 AI 可在对局中切换；停用配置会结束接管。修改模型配置前请先恢复原生 AI。", TextAlign = ContentAlignment.MiddleLeft }, 58);
        Row("操作提示", actionStatus);
        Row("房间状态", roomStatus, 100);
        aiCount.ValueChanged += async (_, _) => { ResizeSeats(); if (catalogProviders.Count > 0) await RefreshSeatModels(); };
        ResizeSeats();
        var saved = RoomFiles.Read<RoomConfig>(Files.Root, "room.json");
        if (saved?.NativeLobby == true && saved.AIs.Count > 0)
        {
            models.Checked = saved.UseModels;
            aiCount.Value = Math.Clamp(saved.AIs.Count, (int)aiCount.Minimum, (int)aiCount.Maximum);
            ResizeSeats();
            for (int i = 0; i < seats.Rows.Count; i++)
            {
                var a = saved.AIs[i];
                seats.Rows[i].SetValues(i + 1, a.Provider, a.Model, a.Reasoning);
            }
        }
        roomStatus.Text = saved != null && !saved.NativeLobby
            ? "检测到旧版虚拟房间配置，请重新选择模型并保存接管配置。"
            : "请配置模型并保存，再由房主在游戏中创建 Steam 房间、添加 AI 并开始对局。";
        roomPoll.Tick += (_, _) => { try { var s = RoomFiles.Read<RoomStatus>(Files.Root, "room-status.json"); if (s != null) roomStatus.Text = s.Message + $"\r\n状态：{s.State} · 可用／已接管 AI：{s.AIPlayers} · 模型接管：{(s.ModelsEnabled ? "启用" : "未启用")}"; } catch { } };
        roomPoll.Start();
        FormClosed += (_, _) => roomPoll.Stop();
        return panel;
    }
    void ResizeSeats()
    {
        while (seats.Rows.Count > (int)aiCount.Value)
            seats.Rows.RemoveAt(seats.Rows.Count - 1);
        while (seats.Rows.Count < (int)aiCount.Value)
        {
            int i = seats.Rows.Count;
            seats.Rows.Add(i + 1, "openai", "", "default");
        }
    }
    void PublishRoom()
    {
        var currentRoom = RoomFiles.Read<RoomStatus>(Files.Root, "room-status.json");
        if (currentRoom?.ModelsEnabled == true)
            throw new InvalidOperationException("请先恢复原生 AI，再修改接管配置。");
        seats.EndEdit();
        var ai = new List<AISeat>();
        foreach (DataGridViewRow row in seats.Rows)
        {
            string providerId = row.Cells["provider"].Value?.ToString() ?? "";
            string modelId = row.Cells["model"].Value?.ToString() ?? "";
            if (!catalogProviders.Any(p => p.Id == providerId && p.ConnectionSupported) || !modelCache.TryGetValue(providerId, out var available) || !available.Any(m => m.Id == modelId))
                throw new InvalidOperationException("请先从模型目录选择可连接的提供商和模型");
            ai.Add(new()
            {
                Provider = row.Cells["provider"].Value?.ToString() ?? "",
                Model = row.Cells["model"].Value?.ToString()?.Trim() ?? "",
                Reasoning = row.Cells["reasoning"].Value?.ToString() ?? "default"
            });
        }
        var config = new RoomConfig { NativeLobby = true, Published = true, UseModels = models.Checked, AIs = ai };
        config.Validate();
        RoomFiles.Write(Files.Root, "room.json", config);
        actionStatus.Text = "接管配置已保存；请由房主在游戏中创建 Steam 房间、添加 AI 并开始对局。";
    }
    void StopRoom()
    {
        var config = RoomFiles.Read<RoomConfig>(Files.Root, "room.json");
        if (config?.NativeLobby == true)
            RoomFiles.Write(Files.Root, "room.json", config with { Published = false, Revision = Guid.NewGuid().ToString("N") });
        actionStatus.Text = "已停止接管，原生房间不受影响。";
    }
    void Command(string action)
    {
        var config = RoomFiles.Read<RoomConfig>(Files.Root, "room.json");
        if (config?.NativeLobby != true || !config.Published)
            throw new InvalidOperationException("请先保存接管配置，再切换 AI 控制方式。");
        RoomFiles.Write(Files.Root, "room-command.json", new RoomCommand(Guid.NewGuid().ToString("N"), action));
        actionStatus.Text = action == "models" ? "已请求启用模型接管，等待游戏响应。" : "已请求恢复原生 AI，等待游戏响应。";
    }
}

