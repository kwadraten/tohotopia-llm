using System;
using System.Collections.Generic;
using System.Linq;

namespace TohotopiaMod;

internal record struct NativeAiSeat(int Index, ulong MemberId, uint PlayerId, bool IsAI, bool IsHostControlled, int TeamId = 0);

internal static class NativeAiSeats
{
    static NativeAiSeat[] Eligible(IEnumerable<NativeAiSeat> seats) =>
        seats.Where(s => s.MemberId == 0 && s.IsAI && !s.IsHostControlled).OrderBy(s => s.Index).ToArray();

    internal static int Available(IEnumerable<NativeAiSeat> seats) => Eligible(seats).Length;

    internal static Dictionary<uint, int> Assign(IEnumerable<NativeAiSeat> seats, int count)
    {
        var selected = Eligible(seats);
        if (count < 1 || count > 4 || selected.Length < count)
            throw new InvalidOperationException("Native lobby has fewer AI seats than configured models");
        var result = new Dictionary<uint, int>();
        for (int i = 0; i < count; i++)
        {
            if (selected[i].PlayerId == 0 || !result.TryAdd(selected[i].PlayerId, i + 1))
                throw new InvalidOperationException("Native lobby AI player mapping is incomplete or duplicated");
        }
        return result;
    }
}
