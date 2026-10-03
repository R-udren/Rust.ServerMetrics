using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

// ReSharper disable InconsistentNaming

namespace RustServerMetrics.HarmonyPatches;

[HarmonyPatch(typeof(BasePlayer), nameof(BasePlayer.PlayerInit))]
public class BasePlayer_PlayerInit_Patch
{
    [HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> originalInstructions)
    {
        var retList = new List<CodeInstruction>(originalInstructions);

        var methodInfo = typeof(MetricsLogger)
            .GetMethod(nameof(MetricsLogger.TryOnPlayerInit), BindingFlags.Static | BindingFlags.NonPublic,
                null, [typeof(BasePlayer)], null);

        var idx = retList.FindIndex(x => x.opcode == OpCodes.Call && x.operand is MethodInfo methodInfo1 && methodInfo1.DeclaringType.Name == "EACServer" && methodInfo1.Name == "OnStartLoading");

        if (idx < 0) throw new Exception("Failed to find the insertion index for PlayerInit");

        retList.InsertRange(idx, [
            new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Call, methodInfo)
        ]);

        return retList;
    }
}
