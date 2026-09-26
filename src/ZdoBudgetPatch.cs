using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;

namespace AdaptiveNet
{
    [HarmonyPatch(typeof(ZDOMan), "SendZDOs")]
    internal static class ZdoBudgetPatch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> source)
        {
            List<CodeInstruction> instructions = source.ToList();
            List<int> vanillaBudgetLoads = instructions
                .Select((instruction, index) => new { instruction, index })
                .Where(item => item.instruction.opcode == OpCodes.Ldc_I4 &&
                               item.instruction.operand is int value &&
                               value == NetworkRuntime.VanillaZdoQueueBudgetBytes)
                .Select(item => item.index)
                .ToList();

            if (vanillaBudgetLoads.Count != 2)
            {
                NetworkRuntime.ReportZdoPatchStatus(
                    false,
                    $"Adaptive ZDO budget disabled safely: expected two vanilla 10 KiB constants, found {vanillaBudgetLoads.Count}.");
                return instructions;
            }

            var budgetMethod = AccessTools.Method(typeof(NetworkRuntime), nameof(NetworkRuntime.GetZdoQueueBudget));
            for (int match = vanillaBudgetLoads.Count - 1; match >= 0; match--)
            {
                int index = vanillaBudgetLoads[match];
                CodeInstruction original = instructions[index];
                var loadPeer = new CodeInstruction(OpCodes.Ldarg_1);
                loadPeer.labels.AddRange(original.labels);
                loadPeer.blocks.AddRange(original.blocks);
                instructions[index] = loadPeer;
                instructions.Insert(index + 1, new CodeInstruction(OpCodes.Call, budgetMethod));
            }

            NetworkRuntime.ReportZdoPatchStatus(
                true,
                "Adaptive ZDO queue budget patch verified and applied (2/2 guards)." );
            return instructions;
        }
    }
}
