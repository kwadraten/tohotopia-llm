using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using UnityEngine;
using TohotopiaShared;
using R = IL2CppSharp.Il2CppReflection;
using N = TohotopiaMod.Native;

namespace TohotopiaMod;

internal sealed unsafe class VirtualChat
{
    const string Prefix = "VCHAT1\n";
    const string Namespace = "TH1_Logic.Chat.";
    readonly VirtualP2P transport;
    readonly Dictionary<ulong, ChatInbox> inboxes = new();
    readonly Dictionary<(ulong, Guid), Wire> receipts = new();
    readonly Dictionary<ulong, float> nextSend = new();
    internal VirtualChat(VirtualP2P transport)
    {
        this.transport = transport;
    }
    sealed record Wire(string Type, Guid MessageId, ulong Sender = 0, byte Channel = 0, int Team = 0, string Text = "", byte State = 0, byte Error = 0);

    // Called only for the active Virtual lobby. Native host chat still resolves
    // the audience, filters text and emits receipts; this replaces its AI wire.
    internal bool Route(ulong recipient, IntPtr data)
    {
        if (!transport.IsAI(recipient) || data == IntPtr.Zero)
            return false;
        var type = N.Class(data);
        Wire packet;
        if (type == R.FindClass(Namespace + "ChatContext"))
            packet = new("context", Guid.Empty);
        else if (type == R.FindClass(Namespace + "ChatDelivery"))
            packet = new("delivery", N.Get<Guid>(data, "MessageId"), N.Get<ulong>(data, "Sender"), N.Get<byte>(data, "Channel"), N.Get<int>(data, "Team"), N.TextField(data, "Text"));
        else if (type == R.FindClass(Namespace + "ChatReceipt"))
            packet = new("receipt", N.Get<Guid>(data, "MessageId"), State: N.Get<byte>(data, "State"), Error: N.Get<byte>(data, "Error"));
        else
            return false;
        return transport.Send(VirtualP2P.HostId, recipient, Encoding.UTF8.GetBytes(Prefix + JsonSerializer.Serialize(packet)));
    }
    internal void Receive(VirtualP2P.Packet packet)
    {
        if (packet.Sender != VirtualP2P.HostId || !transport.IsAI(packet.Recipient))
            return;
        string text = Encoding.UTF8.GetString(packet.Data);
        if (!text.StartsWith(Prefix, StringComparison.Ordinal))
            return;
        var wire = JsonSerializer.Deserialize<Wire>(text.Substring(Prefix.Length));
        if (wire == null)
            return;
        if (wire.Type == "delivery")
        {
            if (!inboxes.TryGetValue(packet.Recipient, out var inbox))
                inboxes[packet.Recipient] = inbox = new();
            inbox.Add(new(wire.MessageId.ToString("N"), wire.Sender,
                transport.Members.TryGetValue(wire.Sender, out var name) ? name : "玩家",
                wire.Channel == 0 ? "public" : "team", wire.Team, wire.Text));
        }
        else if (wire.Type == "receipt")
        {
            // Retain the latest receipt only for each sender; human messages
            // and broadcasts must not grow an unbounded acknowledgement cache.
            foreach (var key in receipts.Keys.Where(k => k.Item1 == packet.Recipient).ToArray())
                receipts.Remove(key);
            receipts[(packet.Recipient, wire.MessageId)] = wire;
        }
    }
    IntPtr Session()
    {
        var manager = N.Static(Namespace + "ChatManager", "Instance");
        N.Call(manager, "spe");
        return N.Get<IntPtr>(manager, "<Session>k__BackingField");
    }
    IntPtr Context(ulong member)
    {
        if (!transport.Joined || !transport.IsAI(member))
            return IntPtr.Zero;
        var session = Session();
        return session == IntPtr.Zero ? IntPtr.Zero : N.Call(session, "tda", (IntPtr)(&member));
    }
    internal string[] Channels(int seat)
    {
        ulong member = VirtualP2P.MemberId(seat);
        if (nextSend.TryGetValue(member, out var at) && Time.realtimeSinceStartup < at)
            return Array.Empty<string>();
        var context = Context(member);
        if (context == IntPtr.Zero)
            return Array.Empty<string>();
        return N.Get<int>(context, "Team") > 0 && N.Get<int>(context, "TeammateCount") > 0
            ? new[] { "public", "team" } : new[] { "public" };
    }
    internal ChatObservation[] Observe(int seat)
    {
        ulong member = VirtualP2P.MemberId(seat);
        var context = Context(member);
        if (context == IntPtr.Zero || !inboxes.TryGetValue(member, out var inbox))
            return Array.Empty<ChatObservation>();
        return inbox.Visible(N.Get<int>(context, "Team"));
    }
    internal bool Send(int seat, string channel, string text, out string result)
    {
        ChatActions.ValidateMessage(text);
        if (!Channels(seat).Contains(channel))
        {
            result = "聊天频道已不可用或没有同队玩家";
            return false;
        }
        ulong member = VirtualP2P.MemberId(seat);
        var context = Context(member);
        if (context == IntPtr.Zero)
        {
            result = "聊天上下文尚未就绪";
            return false;
        }
        uint contextHandle = N.il2cpp_gchandle_new(context, false);
        uint submitHandle = 0;
        try
        {
            var submit = N.New(Namespace + "ChatSubmit");
            submitHandle = N.il2cpp_gchandle_new(submit, false);
            Guid messageId = Guid.NewGuid();
            N.Set(submit, "Session", N.Get<Guid>(context, "Session"));
            N.Set(submit, "Revision", N.Get<ulong>(context, "Revision"));
            N.Set(submit, "MessageId", messageId);
            N.Set(submit, "Channel", (byte)(channel == "public" ? 0 : 1));
            N.Set(submit, "ExpectedTeam", N.Get<int>(context, "Team"));
            N.SetText(submit, "Text", text.Trim());
            var manager = N.Static(Namespace + "ChatManager", "Instance");
            N.Call(manager, "swf", (IntPtr)(&member), submit);
            nextSend[member] = Time.realtimeSinceStartup + 1.05f;
            transport.Pump();
            if (!receipts.Remove((member, messageId), out var receipt))
            {
                result = "房主未确认聊天发送";
                return false;
            }
            result = receipt.State == 1 ? "房主确认已发送" : receipt.State == 2 ? "部分玩家已收到消息" : "房主拒绝消息：" + receipt.Error;
            return receipt.State == 1 || receipt.State == 2;
        }
        finally
        {
            if (submitHandle != 0)
                N.il2cpp_gchandle_free(submitHandle);
            N.il2cpp_gchandle_free(contextHandle);
        }
    }
    internal void Clear()
    {
        inboxes.Clear();
        receipts.Clear();
        nextSend.Clear();
    }
}
