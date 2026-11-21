using System;

namespace TohotopiaMod;

internal static class ActionDelay
{
    const int Playing = 1, Pausing = 3;
    internal static bool Required(Type aiLogic) => aiLogic.GetField("_stallWatchdog", GameReflection.Flags) != null;
    internal static void Advance(object ai, bool presentationBusy, float delta)
    {
        if (ai == null || presentationBusy || Native.Get<int>(ai, "AILogicState") != Pausing) return;
        float left = Native.Get<float>(ai, "_targetTime") - delta;
        Native.Set(ai, "_targetTime", left);
        if (left <= 0) Native.SetInt(ai, "AILogicState", Playing);
    }
}
