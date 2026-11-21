using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Platform.Storage;
using Tohotopia;
using TohotopiaShared;
namespace Settings;

public sealed partial class SettingsWindow
{
    readonly List<AgentEvent> logEvents = new();
    readonly Dictionary<string, long> logOffsets = new();
    List<AgentEvent> visibleLogRows = new();
    readonly Table logGrid = Ui.Records("Agent 行动", 420);
    readonly Select logSeat = new() { Name = "LogSeat", Width = 150, Size = ControlSize.Small };
    readonly Checkbox errorsOnly = new() { Content = "仅警告／错误" }, logAuto = new() { Content = "自动刷新", IsChecked = true };
    readonly Alert logSummary = Ui.StatusAlert("LogStatus");
    readonly DispatcherTimer logPoll = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    bool readingLogs, logsRendered;
    Control BuildLogPage()
    {
        logGrid.Name = "AgentLogs";
        errorsOnly.Name = "ErrorsOnly";
        logSeat.ItemsSource = new[] { "全部 Agent", "AI 1", "AI 2", "AI 3", "AI 4", "AI 5" };
        logSeat.SelectedIndex = 0;
        AutomationProperties.SetName(logSeat, "日志席位");
        logSummary.Title = "Agent 日志概况";
        Ui.Status(logSummary, "日志只来自本地 Agent JSONL 文件。点击“查看详情”可查看完整错误、运行结果和推理内容。", AlertTone.Info);
        static ArcDesignTokenKind? Color(AgentEvent e) => e.Level switch { "error" => ArcDesignTokenKind.Danger, "warning" => ArcDesignTokenKind.Warning, _ => null };
        Ui.Column<AgentEvent>(logGrid, "时间", e => e.Time.ToLocalTime().ToString("MM-dd HH:mm:ss"), .8, Color);
        Ui.Column<AgentEvent>(logGrid, "Agent／玩家／回合", e => e.AiSlot > 0 ? $"AI {e.AiSlot} / P{e.PlayerId} / T{e.Turn}" : "系统", 1.3, Color);
        Ui.Column<AgentEvent>(logGrid, "阶段", e => Stage(e.Stage), 1, Color);
        Ui.Column<AgentEvent>(logGrid, "提供商／模型", e => e.Provider + " / " + e.Model, 1, Color);
        Ui.Column<AgentEvent>(logGrid, "候选", e => e.CandidateId, 1, Color);
        Ui.Column<AgentEvent>(logGrid, "请求 ID", e => e.RequestId, 1, Color);
        logGrid.Columns.Add(new DataTableColumn("详情", _ => "")
        {
            Width = new GridLength(1.1, GridUnitType.Star), IsSortable = false,
            CellTemplate = new FuncDataTemplate<AgentEvent>((row, scope) =>
            {
                var button = Ui.Button("查看详情", () => _ = ShowLogDetail(row));
                button.Name = "AgentLogDetail";
                button.Size = ControlSize.Small;
                return button;
            })
        });
        logSeat.SelectionChanged += (_, _) => RenderLogs();
        errorsOnly.IsCheckedChanged += (_, _) => RenderLogs();
        logPoll.Tick += (_, _) => { if (logAuto.IsChecked == true) ReadLogs(); };
        logPoll.Start();
        return Ui.Page(logSummary, logGrid);
    }
    Control[] LogCommands() => [logSeat, errorsOnly, logAuto, Ui.Button("立即刷新", ReadLogs),
        Ui.Button("清空 Agent 行动", ClearAgentLogView), Ui.Button("打开日志文件夹", OpenLogsFolder)];
    async Task ShowLogDetail(AgentEvent entry)
    {
        if (closed) return;
        string text = $"时间：{entry.Time.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff}  来源：{entry.Source}  级别：{entry.Level}\r\n阶段：{Stage(entry.Stage)}\r\n地图：{entry.MapId}  玩家：{entry.PlayerId}  回合：{entry.Turn}  AI：{entry.AiSlot}  序号：{entry.Sequence}\r\n提供商／模型：{entry.Provider} / {entry.Model}\r\n请求 ID：{entry.RequestId}  候选：{entry.CandidateId}\r\n\r\n{entry.Message}";
        if (entry.RequestId.Length > 0)
        {
            var related = MergeModelFragments(logEvents.Where(item => item.RequestId == entry.RequestId
                && item.Stage is "reasoning_delta" or "reasoning_summary" or "model_output_delta")).ToArray();
            if (related.Length > 0)
                text += "\r\n\r\n同请求推理与模型输出\r\n" + string.Join("\r\n\r\n", related.Select(item => $"【{Stage(item.Stage)}】\r\n{item.Message}"));
        }
        var dialog = new Window
        {
            Title = "日志详情 — " + Stage(entry.Stage), Width = 900, Height = 700, MinWidth = 560, MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false
        };
        var content = Ui.Text(readOnly: true, multiline: true);
        content.Name = "AgentLogDetailText";
        content.Text = text;
        content.VerticalAlignment = VerticalAlignment.Stretch;
        var close = Ui.Button("关闭", dialog.Close, ButtonVariant.Primary);
        close.IsDefault = true;
        close.IsCancel = true;
        close.HorizontalAlignment = HorizontalAlignment.Right;
        dialog.Content = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            Margin = new Thickness(20),
            Children = { content, close }
        };
        Grid.SetRow(close, 1);
        await dialog.ShowDialog(this);
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
                            var e = JsonSerializer.Deserialize(line, SettingsJson.Type<AgentEvent>(RoomFiles.Json));
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
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logSummary.Title = "日志读取失败";
            Ui.Status(logSummary, e.Message, AlertTone.Danger);
        }
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
            logSummary.Title = "清空视图失败";
            Ui.Status(logSummary, e.Message, AlertTone.Danger);
        }
    }
    async void OpenLogsFolder()
    {
        string path = Path.GetFullPath(Path.Combine(Files.Root, "logs"));
        try
        {
            Directory.CreateDirectory(path);
            await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path));
        }
        catch (Exception e) { await Ui.Message(this, "无法打开日志文件夹：" + e.Message, Title ?? "日志"); }
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
        if (errorsOnly.IsChecked == true)
            filtered = filtered.Where(e => e.Level is "warning" or "error");
        visibleLogRows = MergeModelFragments(filtered).OrderByDescending(e => e.Time).Take(600).ToList();
        logGrid.ItemsSource = visibleLogRows;
        logSummary.Title = "Agent 日志概况";
        Ui.Status(logSummary, $"最近已加载 {logEvents.Count} 条；所选 Agent：模型请求 {selected.Count(e => e.Stage == "model_request")}，思考片段 {selected.Count(e => e.Stage is "reasoning_summary" or "reasoning_delta")}，有效决策 {selected.Count(e => e.Stage == "model_decision")}，游戏执行 {selected.Count(e => e.Stage == "executed")}，聊天发送 {selected.Count(e => e.Stage == "message_sent")}，模型结束回合 {selected.Count(e => e.Stage == "end_turn")}，失败 {selected.Count(e => e.Level == "error")}\n只有‘游戏执行’是动作落地记录；‘有效决策’仅说明模型返回了合法候选。原生 AI 行为不计为模型行动。", selected.Any(e => e.Level == "error") ? AlertTone.Warning : AlertTone.Info);
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
    static string Stage(string s) => s switch { "reasoning_delta" => "思考", "model_output_delta" => "模型输出", "tool_call" => "查询规则", "tool_result" => "规则结果", "game_request" => "游戏提交请求", "model_request" => "模型开始决策", "reasoning_summary" => "服务端推理摘要", "model_decision" => "模型有效决策", "model_failed" => "模型请求失败", "json_retry" => "JSON 重新生成", "comm_request" => "非回合局势巡检", "comm_reply" => "非回合通讯决策", "comm_sent" => "通讯消息已发送", "comm_rejected" => "通讯消息发送失败", "comm_memory" => "战术记忆已更新", "comm_failed" => "非回合通讯失败", "plan_received" => "收到动作序列", "plan_blocked" => "序列执行受阻", "executed" => "游戏执行", "message_sent" => "聊天已发送", "message_rejected" => "聊天发送被拒绝", "end_turn" => "模型结束回合", "candidate_stale" => "候选失效", "budget_end" => "动作上限结束", "turn_start" => "AI 回合开始", "fallback" => "停止／回退原 AI", "control_enabled" => "启用模型接管", "auth_saved" => "凭据已保存", "auth_refresh_failed" => "凭据刷新失败", "ping" => "连通性测试", _ => s };
}
