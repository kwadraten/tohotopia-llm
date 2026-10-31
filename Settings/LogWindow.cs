using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Tohotopia;
using TohotopiaShared;
namespace Settings;

public sealed partial class SettingsWindow
{
    readonly List<AgentEvent> logEvents = new(); readonly Dictionary<string, long> logOffsets = new();
    List<AgentEvent> visibleLogRows = new();
    readonly DataGridView logGrid = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
    readonly ComboBox logSeat = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 }; readonly CheckBox errorsOnly = new() { Text = "仅警告／错误", AutoSize = true }, logAuto = new() { Text = "自动刷新", Checked = true, AutoSize = true };
    readonly Label logSummary = new() { Dock = DockStyle.Fill, AutoSize = false }; readonly TextBox engineLog = new() { Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false };
    readonly System.Windows.Forms.Timer logPoll = new() { Interval = 1500 }; DateTime engineStamp; bool readingLogs, logsRendered;
    Control BuildLogPage()
    {
        var page = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(12) };
        page.RowStyles.Add(new(SizeType.Absolute, 42));
        page.RowStyles.Add(new(SizeType.Absolute, 65));
        page.RowStyles.Add(new(SizeType.Percent, 100));
        var tools = new FlowLayoutPanel { Dock = DockStyle.Fill };
        logSeat.Items.Add("全部 Agent");
        for (int i = 1; i <= 5; i++)
            logSeat.Items.Add("AI " + i);
        logSeat.SelectedIndex = 0;
        var refresh = new Button { Text = "立即刷新", AutoSize = true };
        refresh.Click += (_, _) => ReadLogs();
        var clear = new Button { Text = "清空 Agent 行动", AutoSize = true };
        clear.Click += (_, _) => ClearAgentLogView();
        var openFolder = new Button { Text = "打开日志文件夹", AutoSize = true };
        openFolder.Click += (_, _) => OpenLogsFolder();
        tools.Controls.AddRange(new Control[] { logSeat, errorsOnly, logAuto, refresh, clear, openFolder });
        page.Controls.Add(tools, 0, 0);
        page.Controls.Add(logSummary, 0, 1);
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var timeline = new TabPage("Agent 行动");
        timeline.Controls.Add(logGrid);
        var raw = new TabPage("游戏运行日志");
        raw.Controls.Add(engineLog);
        tabs.TabPages.AddRange(new[] { timeline, raw });
        logPoll.Interval = 1500;
        page.Controls.Add(tabs, 0, 2);
        foreach (var c in new[] { ("time", "时间"), ("agent", "Agent／玩家／回合"), ("stage", "阶段"), ("model", "提供商／模型"), ("candidate", "候选"), ("request", "请求 ID"), ("message", "详情") })
            logGrid.Columns.Add(c.Item1, c.Item2);
        logGrid.Columns["message"]!.FillWeight = 260;
        logGrid.Columns["agent"]!.FillWeight = 130;
        logGrid.Columns["time"]!.FillWeight = 70;
        logGrid.DefaultCellStyle.WrapMode = DataGridViewTriState.False;
        logGrid.VirtualMode = true;
        logGrid.CellValueNeeded += (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= visibleLogRows.Count) return;
            var entry = visibleLogRows[e.RowIndex];
            e.Value = e.ColumnIndex switch
            {
                0 => entry.Time.ToLocalTime().ToString("MM-dd HH:mm:ss"),
                1 => entry.AiSlot > 0 ? $"AI {entry.AiSlot} / P{entry.PlayerId} / T{entry.Turn}" : "系统",
                2 => Stage(entry.Stage), 3 => entry.Provider + " / " + entry.Model,
                4 => entry.CandidateId, 5 => entry.RequestId,
                6 => entry.Message.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' '), _ => ""
            };
        };
        logGrid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= visibleLogRows.Count) return;
            e.CellStyle!.ForeColor = visibleLogRows[e.RowIndex].Level switch { "error" => Color.Firebrick, "warning" => Color.DarkOrange, _ => logGrid.DefaultCellStyle.ForeColor };
        };
        logGrid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= visibleLogRows.Count) return;
            var entry = visibleLogRows[e.RowIndex];
            using var detail = new Form { Text = "日志详情 · " + Stage(entry.Stage), Width = 850, Height = 600, StartPosition = FormStartPosition.CenterParent, MinimizeBox = false };
            var content = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = true,
                Text = $"时间：{entry.Time.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff}  来源：{entry.Source}  级别：{entry.Level}\r\n地图：{entry.MapId}  玩家：{entry.PlayerId}  回合：{entry.Turn}  AI：{entry.AiSlot}  序号：{entry.Sequence}\r\n提供商／模型：{entry.Provider} / {entry.Model}\r\n请求 ID：{entry.RequestId}  候选：{entry.CandidateId}\r\n\r\n{entry.Message}" };
            detail.Controls.Add(content);
            detail.ShowDialog(this);
        };
        logSeat.SelectedIndexChanged += (_, _) => RenderLogs();
        errorsOnly.CheckedChanged += (_, _) => RenderLogs();
        logPoll.Tick += (_, _) => { if (logAuto.Checked) ReadLogs(); };
        logPoll.Start();
        Shown += (_, _) => ReadLogs();
        FormClosed += (_, _) => logPoll.Stop();
        return page;
    }
    void ReadLogs()
    {
        if (readingLogs)
            return;
        readingLogs = true;
        try
        {
            bool changed = false;
            string dir = Path.Combine(Files.Root, "logs");
            if (Directory.Exists(dir))
                foreach (string path in Directory.EnumerateFiles(dir, "agents-*.jsonl").OrderByDescending(File.GetLastWriteTimeUtc).Take(6))
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    bool fresh = !logOffsets.TryGetValue(path, out long position) || position > stream.Length;
                    if (fresh)
                        position = Math.Max(0, stream.Length - 512 * 1024);
                    bool skipPartial = fresh && position > 0;
                    stream.Position = position;
                    int size = (int)Math.Min(256 * 1024, stream.Length - position);
                    if (size == 0)
                        continue;
                    byte[] bytes = new byte[size];
                    int count = stream.Read(bytes, 0, size);
                    if (count == 0)
                        continue;
                    int last = Array.LastIndexOf(bytes, (byte)10, count - 1, count);
                    if (last < 0)
                        continue;
                    logOffsets[path] = position + last + 1;
                    string text = Encoding.UTF8.GetString(bytes, 0, last + 1);
                    if (skipPartial)
                    {
                        int first = text.IndexOf('\n');
                        text = first < 0 ? "" : text[(first + 1)..];
                    }
                    foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        try
                        {
                            var e = JsonSerializer.Deserialize<AgentEvent>(line, RoomFiles.Json);
                            if (e != null)
                            {
                                logEvents.Add(e);
                                if (e.Stage is not ("reasoning_delta" or "reasoning_summary" or "model_output_delta")) changed = true;
                            }
                        }
                        catch (JsonException) { }
                    }
                }
            if (logEvents.Count > 5000)
            {
                logEvents.Sort((a, b) => a.Time.CompareTo(b.Time));
                logEvents.RemoveRange(0, logEvents.Count - 5000);
            }
            if (changed || !logsRendered)
            {
                RenderLogs();
                logsRendered = true;
            }
            string pathRaw = Path.GetFullPath(Path.Combine(Files.Root, "..", "..", "..", "LogOutput.log"));
            if (File.Exists(pathRaw) && File.GetLastWriteTimeUtc(pathRaw) != engineStamp)
            {
                engineStamp = File.GetLastWriteTimeUtc(pathRaw);
                using var raw = new FileStream(pathRaw, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                raw.Position = Math.Max(0, raw.Length - 256 * 1024);
                using var reader = new StreamReader(raw, Encoding.UTF8);
                engineLog.Text = reader.ReadToEnd();
                engineLog.SelectionStart = engineLog.TextLength;
                engineLog.ScrollToCaret();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { logSummary.Text = "日志读取失败：" + e.Message; }
        finally { readingLogs = false; }
    }
    void ClearAgentLogView()
    {
        try
        {
            string dir = Path.Combine(Files.Root, "logs");
            if (Directory.Exists(dir))
                foreach (string path in Directory.EnumerateFiles(dir, "agents-*.jsonl"))
                    logOffsets[path] = new FileInfo(path).Length;
            logEvents.Clear();
            RenderLogs();
            logsRendered = true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logSummary.Text = "清空视图失败：" + e.Message;
        }
    }
    void OpenLogsFolder()
    {
        string path = Path.GetFullPath(Path.Combine(Files.Root, "logs"));
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception e) { MessageBox.Show(this, "无法打开日志文件夹：" + e.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
    void RenderLogs()
    {
        var completed = logEvents.Where(e => e.Source == "host" && e.Stage is "model_decision" or "model_failed" or "comm_reply" or "comm_failed")
            .Select(e => e.RequestId).Where(id => !string.IsNullOrEmpty(id)).ToHashSet();
        IEnumerable<AgentEvent> filtered = logEvents.Where(e => e.Stage is not ("reasoning_delta" or "reasoning_summary" or "model_output_delta")
            || string.IsNullOrEmpty(e.RequestId) || completed.Contains(e.RequestId));
        if (logSeat.SelectedIndex > 0)
            filtered = filtered.Where(e => e.AiSlot == logSeat.SelectedIndex);
        var selected = filtered.ToList();
        if (errorsOnly.Checked)
            filtered = filtered.Where(e => e.Level is "warning" or "error");
        visibleLogRows = MergeModelFragments(filtered).OrderByDescending(e => e.Time).Take(600).ToList();
        logGrid.RowCount = visibleLogRows.Count;
        logGrid.Invalidate();
        logSummary.Text = $"最近已加载 {logEvents.Count} 条；所选 Agent：模型请求 {selected.Count(e => e.Stage == "model_request")}，思考片段 {selected.Count(e => e.Stage is "reasoning_summary" or "reasoning_delta")}，有效决策 {selected.Count(e => e.Stage == "model_decision")}，游戏执行 {selected.Count(e => e.Stage == "executed")}，聊天发送 {selected.Count(e => e.Stage == "message_sent")}，模型结束回合 {selected.Count(e => e.Stage == "end_turn")}，失败 {selected.Count(e => e.Level == "error")}\n只有‘游戏执行’是动作落地记录；‘有效决策’仅说明模型返回了合法候选。原生 AI 行为不计为模型行动。";
    }
    static IEnumerable<AgentEvent> MergeModelFragments(IEnumerable<AgentEvent> events)
    {
        var rows = new List<AgentEvent>();
        var fragments = new Dictionary<(string Source, uint Map, uint Player, uint Turn, int Slot, string Provider, string Model, string Request, string Stage, string Level), (int Row, StringBuilder Text)>();
        foreach (var e in events.OrderBy(e => e.Time))
        {
            if (e.Stage is not ("reasoning_delta" or "reasoning_summary" or "model_output_delta") || string.IsNullOrEmpty(e.RequestId))
            {
                rows.Add(e);
                continue;
            }
            var key = (e.Source, e.MapId, e.PlayerId, e.Turn, e.AiSlot, e.Provider, e.Model, e.RequestId, e.Stage, e.Level);
            if (!fragments.TryGetValue(key, out var group))
            {
                group = (rows.Count, new StringBuilder());
                fragments.Add(key, group);
                rows.Add(e);
            }
            if (e.Stage == "reasoning_summary" && group.Text.Length > 0)
                group.Text.Append(' ');
            group.Text.Append(e.Message);
        }
        foreach (var group in fragments.Values)
            rows[group.Row] = rows[group.Row] with { Message = group.Text.ToString() };
        return rows;
    }
    static string Stage(string s) => s switch { "reasoning_delta" => "思考", "model_output_delta" => "模型输出", "tool_call" => "查询规则", "tool_result" => "规则结果", "game_request" => "游戏提交请求", "model_request" => "模型开始决策", "reasoning_summary" => "服务端推理摘要", "model_decision" => "模型有效决策", "model_failed" => "模型请求失败", "json_retry" => "JSON 重新生成", "comm_request" => "非回合局势巡检", "comm_reply" => "非回合通讯决策", "comm_sent" => "通讯消息已发送", "comm_rejected" => "通讯消息发送失败", "comm_memory" => "战术记忆已更新", "comm_failed" => "非回合通讯失败", "plan_received" => "收到动作序列", "plan_blocked" => "序列执行受阻", "executed" => "游戏执行", "message_sent" => "聊天已发送", "message_rejected" => "聊天发送被拒绝", "end_turn" => "模型结束回合", "candidate_stale" => "候选失效", "budget_end" => "动作上限结束", "turn_start" => "AI 回合开始", "fallback" => "停止／回退原 AI", "control_enabled" => "启用模型接管", "auth_saved" => "凭据已保存", "auth_refresh_failed" => "凭据刷新失败", _ => s };
}
