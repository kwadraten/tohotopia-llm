using System;
using System.Collections.Generic;
using System.Linq;

namespace TohotopiaMod;

// All endpoints share the host's authoritative world. Packets are delivered in
// FIFO order; a receipt confirms delivery, never executes the action a second time.
internal sealed class VirtualP2P
{
    internal const ulong HostId = 0xFFFF000000000001;
    internal const ulong RoomId = 0xFFFF000000000000;
    const int MaxQueuedBytes = 128 * 1024 * 1024;
    readonly Dictionary<ulong, string> members = new();
    readonly Queue<Packet> incoming = new();
    int queuedBytes;
    long sequence;

    internal record Packet(long Sequence, ulong Sender, ulong Recipient, byte[] Data);
    internal IReadOnlyDictionary<ulong, string> Members => members;
    internal bool Joined
    {
        get; private set;
    }
    internal event Action<Packet>? Received;
    internal static ulong MemberId(int seat) => seat >= 0 && seat <= 5
        ? HostId + (ulong)seat : throw new ArgumentOutOfRangeException(nameof(seat));
    internal bool IsAI(ulong id) => id != HostId && members.ContainsKey(id);
    internal int ModelSeat(ulong id) => IsAI(id) ? checked((int)(id - HostId)) : 0;
    internal bool SpectatorMode { get; private set; }
    internal bool IsParticipant(ulong id) => members.ContainsKey(id) && (!SpectatorMode || IsAI(id));
    internal int ParticipantCount => members.Keys.Count(IsParticipant);

    internal void Join(IEnumerable<string> models, bool spectatorMode = false)
    {
        var names = models.ToArray();
        if (names.Length < 1 || names.Length > 5 || names.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Virtual 房间需要 1–5 个模型玩家");
        Leave();
        SpectatorMode = spectatorMode;
        if (spectatorMode && names.Length < 2) throw new ArgumentException("观战模式至少需要两个模型玩家");
        members.Add(HostId, spectatorMode ? "本地观众" : "本地玩家");
        for (int i = 0; i < names.Length; i++)
            members.Add(MemberId(i + 1), names[i]);
        Joined = true;
    }

    internal bool Send(ulong sender, ulong recipient, byte[] data)
    {
        if (!Joined || !members.ContainsKey(sender) || !members.ContainsKey(recipient)
            || data == null || data.Length == 0 || data.Length > MaxQueuedBytes - queuedBytes)
            return false;
        incoming.Enqueue(new(++sequence, sender, recipient, (byte[])data.Clone()));
        queuedBytes += data.Length;
        return true;
    }

    internal bool Broadcast(ulong sender, byte[] data)
    {
        if (!Joined || !members.ContainsKey(sender) || data == null || data.Length == 0)
            return false;
        var recipients = members.Keys.Where(id => id != sender).ToArray();
        if ((long)data.Length * recipients.Length > MaxQueuedBytes - queuedBytes)
            return false;
        foreach (var recipient in recipients)
            Send(sender, recipient, data);
        return true;
    }

    internal void Pump()
    {
        // Deliver only the current batch, so replies cannot recurse forever.
        int count = incoming.Count;
        while (count-- > 0 && incoming.TryDequeue(out var packet))
        {
            queuedBytes -= packet.Data.Length;
            Received?.Invoke(packet);
        }
    }

    internal void Leave()
    {
        Joined = false;
        SpectatorMode = false;
        incoming.Clear();
        members.Clear();
        queuedBytes = 0;
        sequence = 0;
    }
}
