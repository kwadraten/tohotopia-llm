using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Globalization;

namespace TohotopiaShared;

public static class ChatActions
{
    public const string PublicId = "chat_public";
    public const string TeamId = "chat_team";
    public const int MaxTextBytes = 2048;
    public const int MaxPerTurn = 2;
    public static string ModelLabel(string model)
    {
        string safe = new string((model ?? "").Select(c =>
            char.IsControl(c) ? ' ' : c == '<' ? '＜' : c == '>' ? '＞' : c).ToArray()).Trim();
        var text = new StringInfo(safe);
        return text.LengthInTextElements > 48 ? text.SubstringByTextElements(0, 48) + "…" : safe;
    }
    public static string RelayMessage(int seat, string model, string text)
    {
        string label = ModelLabel(model);
        string name = label.Length == 0 ? $"AI 席位 {seat}" : label;
        string message = $"[代{name} 转发] {text.Trim()}";
        ValidateMessage(message);
        return message;
    }
    public static bool IsRelayedMessage(string text) =>
        text.StartsWith("[房主代 AI 席位 ", StringComparison.Ordinal) ||
        (text.StartsWith("[代", StringComparison.Ordinal) && text.IndexOf(" 转发]", 2, StringComparison.Ordinal) >= 3);
    public static bool CanRelayTeam(int aiTeam, int hostTeam) =>
        aiTeam > 0 && aiTeam == hostTeam;
    public static string Channel(string candidateId) => candidateId switch
    {
        PublicId => "public",
        TeamId => "team",
        _ => ""
    };
    public static string Kind(string candidateId) => candidateId switch
    {
        PublicId => "SendPublicMessage",
        TeamId => "SendTeamMessage",
        _ => ""
    };
    public static void ValidateMessage(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || Encoding.UTF8.GetByteCount(text.Trim()) > MaxTextBytes)
            throw new ArgumentException("聊天消息必须非空，且 UTF-8 长度不能超过 2048 字节");
    }
}

public sealed class AmbientSchedule
{
    readonly Dictionary<uint, (string Seen, float NextReactive)> states = new();
    public bool Due(uint playerId, string latestMessageId, float now) =>
        !string.IsNullOrEmpty(latestMessageId) && (!states.TryGetValue(playerId, out var state)
            || latestMessageId != state.Seen && now >= state.NextReactive);
    public void Mark(uint playerId, string latestMessageId, float now) =>
        states[playerId] = (latestMessageId, now + 10f);
    public void Clear() => states.Clear();
}

public record ChatObservation(string MessageId, ulong SenderMemberId, string SenderName, string Channel, int Team, string Text);
public record ChatParticipant(uint PlayerId, ulong MemberId, string Name, int Team);

// Keep only deliveries addressed to this endpoint. Recheck the current team
// when exporting history, since membership may change after delivery.
public sealed class ChatInbox
{
    readonly List<ChatObservation> messages = new();
    public void Add(ChatObservation message)
    {
        if (messages.Any(m => m.SenderMemberId == message.SenderMemberId && m.MessageId == message.MessageId))
            return;
        messages.Add(message);
        if (messages.Count > 100)
            messages.RemoveAt(0);
    }
    public ChatObservation[] Visible(int team) => messages
        .Where(m => m.Channel == "public" || m.Channel == "team" && team > 0 && m.Team == team)
        .TakeLast(20).ToArray();
    public void Clear() => messages.Clear();
}
