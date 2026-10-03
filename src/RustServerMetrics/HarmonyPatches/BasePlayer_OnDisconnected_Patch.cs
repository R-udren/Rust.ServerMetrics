using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

// ReSharper disable InconsistentNaming

namespace RustServerMetrics.HarmonyPatches;

[HarmonyPatch(typeof(BasePlayer), nameof(BasePlayer.OnDisconnected))]
public class BasePlayer_OnDisconnected_Patch
{
    [HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> originalInstructions)
    {
        var retList = new List<CodeInstruction>(originalInstructions);

        var methodInfo = typeof(MetricsLogger)
            .GetMethod(nameof(MetricsLogger.TryOnPlayerDisconnected), BindingFlags.Static | BindingFlags.NonPublic,
                null, [typeof(BasePlayer)], null);

        retList.InsertRange(0, [
            new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Call, methodInfo)
        ]);

        return retList;
    }
}
