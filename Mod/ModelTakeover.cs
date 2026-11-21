namespace TohotopiaMod;

internal static class ModelTakeover
{
    internal static bool Matches(object ai, object map, object player) => ai != null && map != null && player != null
        && Native.Get<object>(ai, "_mapData") == map && Native.Get<object>(ai, "_playerData") == player;
    internal static bool Holds(bool controlled, bool nativeFallback, bool initialized, int state) =>
        controlled && !nativeFallback && (!initialized || state != 2);
    internal static bool Running(int state) => state is 1 or 3 or 4;
    internal static bool Restart(object ai, object map, object player)
    {
        if (!Matches(ai, map, player)) return false;
        int state = Native.Get<int>(ai, "AILogicState");
        if (state is not (1 or 3)) return false;
        try
        {
            var kernel = Native.Get<object>(ai, "_kernel");
            if (kernel == null) throw new System.InvalidOperationException("原生 AI 内核未就绪");
            Native.Call(kernel, "wct");
            Native.Call(kernel, "wcr", map, player);
            return true;
        }
        catch (System.Exception e)
        {
            Battle.Log.LogError("原生 AI 恢复失败，结束当前回合：" + e.Message);
            Native.SetInt(ai, "AILogicState", 4);
            return false;
        }
    }
}
