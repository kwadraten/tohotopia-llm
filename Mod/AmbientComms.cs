using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using TohotopiaShared;
using N = TohotopiaMod.Native;

namespace TohotopiaMod;

public partial class Battle
{
    readonly AmbientSchedule ambientSchedule = new();
    readonly Dictionary<uint, (float Next, JsonElement Observation)> visibleSnapshots = new();
    readonly Dictionary<uint, (string Message, float At)> recentAmbient = new();
    Task<string> ambientPending;
    CancellationTokenSource ambientCancellation;
    IntPtr ambientMap;
    uint ambientPlayer;
    string ambientSession = "", ambientRequestId = "";
    float ambientErrorUntil;

    void ResetAmbient()
    {
        CancelAmbient();
        ambientSchedule.Clear();
        visibleSnapshots.Clear();
        recentAmbient.Clear();
        ambientSession = Guid.NewGuid().ToString("N");
        ambientErrorUntil = 0;
    }

    void CancelAmbient()
    {
        ambientCancellation?.Cancel();
        ambientCancellation?.Dispose();
        ambientCancellation = null;
        ambientPending = null;
        ambientPlayer = 0;
        ambientMap = IntPtr.Zero;
        ambientRequestId = "";
    }

    void PollAmbient(IntPtr map)
    {
        if (!room.ModelsEnabled)
        {
            CancelAmbient();
            return;
        }
        var current = N.Call(map, "get_ivn");
        uint currentPlayer = current == IntPtr.Zero ? 0 : N.Id(current);
        if (ambientPending != null && ambientPlayer == currentPlayer)
            CancelAmbient();
        float now = Time.realtimeSinceStartup;
        if (now < ambientErrorUntil) return;
        try
        {
            if (ambientPending?.IsCompleted == true)
            {
                var task = ambientPending;
                string id = ambientRequestId;
                uint pid = ambientPlayer;
                IntPtr requestMap = ambientMap;
                ambientPending = null;
                ambientCancellation?.Dispose();
                ambientCancellation = null;
                ambientPlayer = 0;
                ambientMap = IntPtr.Zero;
                ambientRequestId = "";
                if (task.IsFaulted || task.IsCanceled)
                    AmbientEvent(map, pid, id, "comm_failed", task.Exception?.GetBaseException().Message ?? "非回合通讯已取消", "warning");
                else if (requestMap == map && room.Seat(pid) > 0)
                {
                    var reply = JsonSerializer.Deserialize<AmbientReply>(task.Result, json);
                    if (reply == null || reply.RequestId != id || reply.Channel is not ("" or "team" or "public"))
                        throw new InvalidOperationException("非回合通讯回复无效");
                    if (reply.Channel is "team" or "public")
                    {
                        if (recentAmbient.TryGetValue(pid, out var previous) && previous.Message == reply.Message && now - previous.At < 120f)
                            AmbientEvent(map, pid, id, "comm_rejected", "重复消息已抑制", "warning");
                        else
                        {
                            bool sent = room.SendChat(pid, reply.Channel, reply.Message, out var receipt);
                            if (sent) recentAmbient[pid] = (reply.Message, now);
                            AmbientEvent(map, pid, id, sent ? "comm_sent" : "comm_rejected", receipt, sent ? "info" : "warning");
                        }
                    }
                    else AmbientEvent(map, pid, id, "comm_memory", "已查看可见局势和房间通讯");
                }
            }
            var players = N.Items(N.Get<IntPtr>(N.Get<IntPtr>(map, "PlayerMap"), "PlayerDataList"))
                .Where(player => player != IntPtr.Zero).ToDictionary(N.Id);
            uint mapId = N.Get<uint>(map, "MapID");
            foreach (uint pid in room.ControlledPlayers())
            {
                if (pid == currentPlayer || !players.TryGetValue(pid, out var player)) continue;
                if (!visibleSnapshots.TryGetValue(pid, out var snapshot) || now >= snapshot.Next)
                {
                    var observation = JsonSerializer.SerializeToElement(Observe(map, player, out _, out _, out _, ambient: true), json);
                    visibleSnapshots[pid] = snapshot = (now + 5f, observation);
                    RoomFiles.Write(data, $"live-{mapId}-{pid}.json",
                        new VisibleSnapshot(ambientSession, mapId, pid, DateTimeOffset.UtcNow, observation));
                }
                var received = room.ChatHistory(pid).Where(m => !room.IsOwnMessage(pid, m.SenderMemberId)).ToArray();
                string latest = received.LastOrDefault()?.MessageId ?? "";
                if (!ambientSchedule.Due(pid, latest, now) || ambientPending != null) continue;
                var channels = room.ChatChannels(pid);
                string id = Guid.NewGuid().ToString("N");
                var request = new CommunicationRequest(id, ambientSession, mapId, pid, N.Get<uint>(player, "Turn"),
                    room.Seat(pid), snapshot.Observation, channels, Civilization(player));
                string body = JsonSerializer.Serialize(request, json);
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
                    File.ReadAllText(Path.Combine(data, "bridge.token")).Trim());
                ambientSchedule.Mark(pid, latest, now);
                ambientCancellation = new CancellationTokenSource();
                ambientPlayer = pid;
                ambientMap = map;
                ambientRequestId = id;
                ambientPending = Send(body, ambientCancellation.Token, "/communication");
                AmbientEvent(map, pid, id, "comm_request", "收到新的房间消息");
                break;
            }
        }
        catch (Exception e)
        {
            CancelAmbient();
            ambientErrorUntil = now + 10f;
            Log.LogWarning("非回合通信暂停：" + e.Message);
        }
    }

    void AmbientEvent(IntPtr map, uint pid, string id, string stage, string text, string level = "info")
    {
        try
        {
            AgentEvents.Write(data, new(DateTimeOffset.UtcNow, "game", stage, text, level,
                N.Get<uint>(map, "MapID"), pid, 0, room.Seat(pid), RequestId: id));
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.LogWarning("非回合通信日志写入失败：" + e.GetType().Name);
        }
    }
}
