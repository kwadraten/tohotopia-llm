using System.Collections.Generic;
namespace TohotopiaMod;

public static class Visibility
{
    public static bool Unit(uint owner, uint observer, uint grid, bool hidden, HashSet<uint> explored) => owner == observer || (explored.Contains(grid) && !hidden);
    public static bool Candidate(string kind, uint grid, uint targetUnit, uint targetGrid, uint targetPlayer, HashSet<uint> explored, HashSet<uint> visibleUnits, HashSet<uint> knownPlayers) =>
        (targetUnit == 0 || visibleUnits.Contains(targetUnit)) && (targetGrid == 0 || explored.Contains(targetGrid)) && (targetPlayer == 0 || knownPlayers.Contains(targetPlayer)) && (grid == 0 || explored.Contains(grid) || kind == "UnitMove");
}
