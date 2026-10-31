using TohotopiaMod;

internal static class VirtualChecks
{
    internal static void Run()
    {
        void Check(bool valid, string message)
        {
            if (!valid)
                throw new Exception(message);
            Console.WriteLine("PASS " + message);
        }
        var transport = new VirtualP2P();
        transport.Join(new[] { "same-model", "same-model", "third-model", "fourth", "fifth" });
        Check(transport.Members.Count == 6 && transport.Members.Keys.Distinct().Count() == 6,
            "virtual room gives every player an independent ID, including duplicate models");
        var received = new List<VirtualP2P.Packet>();
        transport.Received += received.Add;
        byte[] data = { 1, 2, 3 };
        Check(transport.Broadcast(VirtualP2P.HostId, data), "virtual broadcast enqueues all five AI players");
        data[0] = 99;
        transport.Pump();
        Check(received.Count == 5 && received.All(p => p.Data[0] == 1) && received.Select(p => p.Sequence).SequenceEqual(new long[] { 1, 2, 3, 4, 5 }),
            "virtual packets retain a payload snapshot and ordered delivery");
        Check(!transport.Send(VirtualP2P.HostId, 0, data) && !transport.Send(0, VirtualP2P.HostId, data)
            && !transport.Send(VirtualP2P.HostId, VirtualP2P.MemberId(1), Array.Empty<byte>()),
            "virtual transport rejects missing members and empty payloads");
        Check(transport.Send(VirtualP2P.MemberId(1), VirtualP2P.HostId, data), "AI endpoint can send to the host");
        transport.Pump();
        Check(received.Last().Sender == VirtualP2P.MemberId(1) && received.Last().Recipient == VirtualP2P.HostId,
            "AI messages preserve sender identity");
        var large = new byte[27 * 1024 * 1024];
        Check(!transport.Broadcast(VirtualP2P.HostId, large), "virtual broadcast rejects queue overflow atomically");
        int before = received.Count;
        transport.Pump();
        Check(received.Count == before, "failed broadcast never partially delivers");
        transport.Send(VirtualP2P.HostId, VirtualP2P.MemberId(1), data);
        transport.Leave();
        transport.Join(new[] { "replacement" });
        transport.Pump();
        Check(received.Count == before && transport.Members.Count == 2 && !transport.IsAI(VirtualP2P.MemberId(2)),
            "leave and rejoin clear stale packets and old AI membership");
        transport.Leave();
        Check(!transport.Broadcast(VirtualP2P.HostId, data), "closed virtual room rejects sends");
    }
}
