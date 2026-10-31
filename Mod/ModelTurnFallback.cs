using System.Collections.Generic;

namespace TohotopiaMod;

// A failed request releases native AI only for that player's current turn.
public sealed class ModelTurnFallback
{
    readonly Dictionary<uint, uint> turns = new();
    public void Fail(uint player, uint turn) => turns[player] = turn;
    public bool IsNative(uint player, uint turn) => turns.TryGetValue(player, out var failed) && failed == turn;
    public void Clear() => turns.Clear();
}
