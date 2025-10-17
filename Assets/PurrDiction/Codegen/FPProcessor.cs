using System;
using System.Collections.Generic;
using Mono.Cecil;
using Mono.Cecil.Cil;
using PurrNet.Codegen;
using PurrNet.Prediction;
using Unity.CompilationPipeline.Common.Diagnostics;

namespace Purrdiction.Codegen
{
    public static class FPProcessor
    {
        static MethodDefinition GetMethodWithParams(this TypeDefinition type, string name, params Type[] types)
        {
            for (var i = 0; i < type.Methods.Count; i++)
            {
                if (type.Methods[i].Name != name)
                    continue;

                if (type.Methods[i].Parameters.Count != types.Length)
                    continue;

                bool match = true;

                for (var j = 0; j < types.Length; j++)
                {
                    if (type.Methods[i].Parameters[j].ParameterType.FullName != types[j].FullName)
                    {
                        match = false;
                        break;
                    }
                }

                if (!match)
                    continue;

                return type.Methods[i];
            }

            throw new Exception($"Method {name} not found on type {type.FullName}");
        }

        public static void HandleType(ModuleDefinition module, TypeDefinition type, List<DiagnosticMessage> messages)
        {
            try
            {
                if (!type.HasMethods)
                    return;

                if (type.FullName == typeof(FP).FullName ||
                    type.FullName == typeof(MathFP).FullName)
                    return;

                if (type.FullName == typeof(sfloat).FullName ||
                    type.FullName == typeof(MathS).FullName)
                    return;

                var fp64Type = module.GetTypeDefinition<FP>();
                var fromRawLong = fp64Type.GetMethod("FromRaw").Import(module);
                var opAdditionFp64_Fp64 = fp64Type.GetMethodWithParams("op_Addition", typeof(FP), typeof(FP)).Import(module);
                var opSubstractionFp64_Fp64 = fp64Type.GetMethodWithParams("op_Subtraction", typeof(FP), typeof(FP)).Import(module);
                var opMultiplyFp64_Fp64 = fp64Type.GetMethodWithParams("op_Multiply", typeof(FP), typeof(FP)).Import(module);
                var opDivisionFp64_Fp64 = fp64Type.GetMethodWithParams("op_Division", typeof(FP), typeof(FP)).Import(module);
                var opModulusFp64_Fp64 = fp64Type.GetMethodWithParams("op_Modulus", typeof(FP), typeof(FP)).Import(module);

                var sfloatType = module.GetTypeDefinition<sfloat>();
                var fromRawLongS = sfloatType.GetMethod("FromRaw").Import(module);

                for (var i = 0; i < type.Methods.Count; i++)
                {
                    var method = type.Methods[i];

                    if (method is not { HasBody: true })
                        continue;

                    var replacementMap = new Dictionary<Instruction, Instruction>();
                    var ilProcessor = method.Body.GetILProcessor();

                    for (var j = 0; j < method.Body.Instructions.Count; j++)
                    {
                        var instruction = method.Body.Instructions[j];

                        if (instruction.OpCode != OpCodes.Call)
                            continue;

                        var methodReference = (MethodReference)instruction.Operand;

                        var methodDef = methodReference.Resolve();

                        if (methodDef == null)
                            continue;

                        if (!methodDef.IsStatic)
                            continue;

                        if (methodDef.DeclaringType.FullName == fp64Type.FullName)
                        {
                            HandleFPOperations(messages, methodDef, ilProcessor, method, j, replacementMap, instruction, fromRawLong,
                                opAdditionFp64_Fp64, opSubstractionFp64_Fp64, opMultiplyFp64_Fp64, opDivisionFp64_Fp64, opModulusFp64_Fp64);
                        }
                        else if (methodDef.DeclaringType.FullName == sfloatType.FullName)
                        {
                            switch (methodDef.Name)
                            {
                                case "op_Implicit" when methodDef.Parameters.Count == 1 && CheckParamForS(methodDef, 0):
                                {
                                    try
                                    {
                                        var res = ConvertToConstS(ilProcessor, method.Body.Instructions[j - 1],
                                            messages, replacementMap);
                                        if (res == null) return;
                                        Replace(ilProcessor, instruction, ilProcessor.Create(OpCodes.Call, fromRawLongS),
                                            replacementMap);
                                    }
                                    catch (Exception e)
                                    {
                                        Error(messages, $"Failed to generate implicit fixed point magic.\n{e.Message}\n{e.StackTrace}", instruction,
                                            ilProcessor.Body.Method);
                                    }

                                    break;
                                }
                            }
                        }
                    }

                    foreach (var inst in method.Body.Instructions)
                    {
                        if (inst.Operand is Instruction target && replacementMap.TryGetValue(target, out var newTarget))
                            inst.Operand = newTarget;

                        // Handle switch statements
                        if (inst.Operand is Instruction[] targets)
                        {
                            for (int j = 0; j < targets.Length; j++)
                            {
                                if (replacementMap.TryGetValue(targets[j], out var newSwitchTarget))
                                    targets[j] = newSwitchTarget;
                            }
                            inst.Operand = targets;
                        }
                    }

                    // convert short branches that overflow, took me long to figure this one out
                    foreach (var inst in method.Body.Instructions)
                    {
                        if (inst.Operand is Instruction target)
                        {
                            int delta = target.Offset - (inst.Offset + inst.GetSize());

                            if (delta is <= -128 or >= 127)
                            {
                                // Overflow - convert to long form
                                if (inst.OpCode == OpCodes.Br_S) inst.OpCode = OpCodes.Br;
                                else if (inst.OpCode == OpCodes.Brfalse_S) inst.OpCode = OpCodes.Brfalse;
                                else if (inst.OpCode == OpCodes.Brtrue_S) inst.OpCode = OpCodes.Brtrue;
                                else if (inst.OpCode == OpCodes.Beq_S) inst.OpCode = OpCodes.Beq;
                                else if (inst.OpCode == OpCodes.Bne_Un_S) inst.OpCode = OpCodes.Bne_Un;
                                else if (inst.OpCode == OpCodes.Bge_S) inst.OpCode = OpCodes.Bge;
                                else if (inst.OpCode == OpCodes.Bge_Un_S) inst.OpCode = OpCodes.Bge_Un;
                                else if (inst.OpCode == OpCodes.Bgt_S) inst.OpCode = OpCodes.Bgt;
                                else if (inst.OpCode == OpCodes.Bgt_Un_S) inst.OpCode = OpCodes.Bgt_Un;
                                else if (inst.OpCode == OpCodes.Ble_S) inst.OpCode = OpCodes.Ble;
                                else if (inst.OpCode == OpCodes.Ble_Un_S) inst.OpCode = OpCodes.Ble_Un;
                                else if (inst.OpCode == OpCodes.Blt_S) inst.OpCode = OpCodes.Blt;
                                else if (inst.OpCode == OpCodes.Blt_Un_S) inst.OpCode = OpCodes.Blt_Un;
                                else if (inst.OpCode == OpCodes.Leave_S) inst.OpCode = OpCodes.Leave;
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                messages.Add(new DiagnosticMessage
                {
                    DiagnosticType = DiagnosticType.Error,
                    MessageData = $"Unhandled exception {e.Message}\n{e.StackTrace}",
                });
            }
        }

        private static void HandleFPOperations(List<DiagnosticMessage> messages, MethodDefinition methodDef, ILProcessor ilProcessor,
            MethodDefinition method, int j, Dictionary<Instruction, Instruction> replacementMap, Instruction instruction, MethodReference fromRawLong,
            MethodReference opAdditionFp64_Fp64, MethodReference opSubstractionFp64_Fp64, MethodReference opMultiplyFp64_Fp64,
            MethodReference opDivisionFp64_Fp64, MethodReference opModulusFp64_Fp64)
        {
            switch (methodDef.Name)
            {
                case "op_Implicit" when methodDef.Parameters.Count == 1 && CheckParam(methodDef, 0):
                {
                    try
                    {
                        var res = ConvertToConstFP(ilProcessor, method.Body.Instructions[j - 1],
                            messages, replacementMap);
                        if (res == null) return;
                        Replace(ilProcessor, instruction, ilProcessor.Create(OpCodes.Call, fromRawLong),
                            replacementMap);
                    }
                    catch
                    {
                        Error(messages, $"Failed to generate implicit fixed point magic.", instruction,
                            ilProcessor.Body.Method);
                    }

                    break;
                }
                case "op_Addition" when methodDef.Parameters.Count == 2 && CheckParam(methodDef, 1):
                {
                    try
                    {
                        var fp = ConvertToConstFP(ilProcessor, method.Body.Instructions[j - 1],
                            messages, replacementMap);
                        if (fp == null) return;
                        var toRaw = ilProcessor.Create(OpCodes.Call, opAdditionFp64_Fp64);
                        ilProcessor.InsertAfter(fp, ilProcessor.Create(OpCodes.Call, fromRawLong));
                        Replace(ilProcessor, instruction, toRaw, replacementMap);
                    }
                    catch
                    {
                        Error(messages, $"Failed to generate addition fixed point magic.", instruction,
                            ilProcessor.Body.Method);
                    }

                    break;
                }
                case "op_Addition" when methodDef.Parameters.Count == 2 && CheckParam(methodDef, 0):
                {
                    try
                    {
                        var fp = ConvertToConstFP(ilProcessor, method.Body.Instructions[j - 2],
                            messages, replacementMap);
                        if (fp == null) return;
                        var toRaw = ilProcessor.Create(OpCodes.Call, opAdditionFp64_Fp64);
                        ilProcessor.InsertAfter(fp, ilProcessor.Create(OpCodes.Call, fromRawLong));
                        Replace(ilProcessor, instruction, toRaw, replacementMap);
                    }
                    catch
                    {
                        Error(messages, $"Failed to generate addition fixed point magic.", instruction,
                            ilProcessor.Body.Method);
                    }

                    break;
                }
                case "op_Subtraction" when methodDef.Parameters.Count == 2 && CheckParam(methodDef, 1):
                {
                    try
                    {
                        var fp = ConvertToConstFP(ilProcessor, method.Body.Instructions[j - 1],
                            messages, replacementMap);
                        if (fp == null) return;
                        var toRaw = ilProcessor.Create(OpCodes.Call, opSubstractionFp64_Fp64);
                        ilProcessor.InsertAfter(fp, ilProcessor.Create(OpCodes.Call, fromRawLong));
                        Replace(ilProcessor, instruction, toRaw, replacementMap);
                    }
                    catch
                    {
                        Error(messages, $"Failed to generate subtraction fixed point magic.",
                            instruction, ilProcessor.Body.Method);
                    }

                    break;
                }
                case "op_Subtraction" when methodDef.Parameters.Count == 2 && CheckParam(methodDef, 0):
                {
                    try
                    {
                        var fp = ConvertToConstFP(ilProcessor, method.Body.Instructions[j - 2],
                            messages, replacementMap);
                        if (fp == null) return;
                        var toRaw = ilProcessor.Create(OpCodes.Call, opSubstractionFp64_Fp64);
                        ilProcessor.InsertAfter(fp, ilProcessor.Create(OpCodes.Call, fromRawLong));
                        Replace(ilProcessor, instruction, toRaw, replacementMap);
                    }
                    catch
                    {
                        Error(messages, $"Failed to generate subtraction fixed point magic.",
                            instruction, ilProcessor.Body.Method);
                    }

                    break;
                }
                case "op_Multiply" when methodDef.Parameters.Count == 2 && CheckParam(methodDef, 1):
                {
                    try
                    {
                        var fp = ConvertToConstFP(ilProcessor, method.Body.Instructions[j - 1],
                            messages, replacementMap);
                        if (fp == null) return;
                        var toRaw = ilProcessor.Create(OpCodes.Call, opMultiplyFp64_Fp64);
                        ilProcessor.InsertAfter(fp, ilProcessor.Create(OpCodes.Call, fromRawLong));
                        Replace(ilProcessor, instruction, toRaw, replacementMap);
                    }
                    catch
                    {
                        Error(messages, $"Failed to generate multiplication fixed point magic.",
                            instruction, ilProcessor.Body.Method);
                    }

                    break;
                }
                case "op_Multiply" when methodDef.Parameters.Count == 2 && CheckParam(methodDef, 0):
                {
                    try
                    {
                        var fp = ConvertToConstFP(ilProcessor, method.Body.Instructions[j - 2],
                            messages, replacementMap);
                        if (fp == null) return;
                        var toRaw = ilProcessor.Create(OpCodes.Call, opMultiplyFp64_Fp64);
                        ilProcessor.InsertAfter(fp, ilProcessor.Create(OpCodes.Call, fromRawLong));
                        Replace(ilProcessor, instruction, toRaw, replacementMap);
                    }
                    catch
                    {
                        Error(messages, $"Failed to generate multiplication fixed point magic.",
                            instruction, ilProcessor.Body.Method);
                    }

                    break;
                }
                case "op_Division" when methodDef.Parameters.Count == 2 && CheckParam(methodDef, 1):
                {
                    try
                    {
                        var fp = ConvertToConstFP(ilProcessor, method.Body.Instructions[j - 1],
                            messages, replacementMap);
                        if (fp == null) return;
                        var toRaw = ilProcessor.Create(OpCodes.Call, opDivisionFp64_Fp64);
                        ilProcessor.InsertAfter(fp, ilProcessor.Create(OpCodes.Call, fromRawLong));
                        Replace(ilProcessor, instruction, toRaw, replacementMap);
                    }
                    catch
                    {
                        Error(messages, $"Failed to generate division fixed point magic.", instruction,
                            ilProcessor.Body.Method);
                    }

                    break;
                }
                case "op_Division" when methodDef.Parameters.Count == 2 && CheckParam(methodDef, 0):
                {
                    try
                    {
                        var fp = ConvertToConstFP(ilProcessor, method.Body.Instructions[j - 2],
                            messages, replacementMap);
                        if (fp == null) return;
                        var toRaw = ilProcessor.Create(OpCodes.Call, opDivisionFp64_Fp64);
                        ilProcessor.InsertAfter(fp, ilProcessor.Create(OpCodes.Call, fromRawLong));
                        Replace(ilProcessor, instruction, toRaw, replacementMap);
                    }
                    catch
                    {
                        Error(messages, $"Failed to generate division fixed point magic.", instruction,
                            ilProcessor.Body.Method);
                    }

                    break;
                }
                case "op_Modulus" when methodDef.Parameters.Count == 2 && CheckParam(methodDef, 1):
                {
                    try
                    {
                        var fp = ConvertToConstFP(ilProcessor, method.Body.Instructions[j - 1],
                            messages, replacementMap);
                        if (fp == null) return;
                        var toRaw = ilProcessor.Create(OpCodes.Call, opModulusFp64_Fp64);
                        ilProcessor.InsertAfter(fp, ilProcessor.Create(OpCodes.Call, fromRawLong));
                        Replace(ilProcessor, instruction, toRaw, replacementMap);
                    }
                    catch
                    {
                        Error(messages, $"Failed to generate modulus fixed point magic.", instruction,
                            ilProcessor.Body.Method);
                    }

                    break;
                }
                case "op_Modulus" when methodDef.Parameters.Count == 2 && CheckParam(methodDef, 0):
                {
                    try
                    {
                        var fp = ConvertToConstFP(ilProcessor, method.Body.Instructions[j - 2],
                            messages, replacementMap);
                        if (fp == null) return;
                        ilProcessor.InsertAfter(fp, ilProcessor.Create(OpCodes.Call, fromRawLong));
                        Replace(ilProcessor, instruction,
                            ilProcessor.Create(OpCodes.Call, opModulusFp64_Fp64), replacementMap);
                    }
                    catch
                    {
                        Error(messages, $"Failed to generate modulus fixed point magic.", instruction,
                            ilProcessor.Body.Method);
                    }

                    break;
                }
            }
        }

        static void Replace(ILProcessor processor, Instruction instruction, Instruction newInstruction, Dictionary<Instruction, Instruction> replacementMap)
        {
            replacementMap[instruction] = newInstruction;
            processor.Replace(instruction, newInstruction);
        }

        private static bool CheckParam(MethodDefinition methodDef, int idx)
        {
            var cmp = methodDef.Parameters[idx].ParameterType.FullName;
            return cmp is "System.Single" or "System.Double";
        }

        private static bool CheckParamForS(MethodDefinition methodDef, int idx)
        {
            var cmp = methodDef.Parameters[idx].ParameterType.FullName;
            return cmp is "System.Single";
        }

        private static void Error(ICollection<DiagnosticMessage> messages, string message, Instruction instruction, MethodDefinition method)
        {
            if (method.DebugInformation.HasSequencePoints)
            {
                try
                {
                    var first = GetSequence(instruction, method);
                    string file = first.Document.Url;
                    if (!string.IsNullOrEmpty(file))
                        file = '/' + file[file.IndexOf("Assets", StringComparison.Ordinal)..].Replace('\\', '/');
                    else file = string.Empty;

                    messages.Add(new DiagnosticMessage
                    {
                        DiagnosticType = DiagnosticType.Error,
                        MessageData = message,
                        Column = first.StartColumn,
                        Line = first.StartLine,
                        File = file
                    });
                }
                catch (Exception e)
                {
                    messages.Add(new DiagnosticMessage
                    {
                        DiagnosticType = DiagnosticType.Error,
                        MessageData = $"[{method.DeclaringType.FullName}] {message}\n{e.StackTrace}"
                    });
                }
            }
            else
            {
                messages.Add(new DiagnosticMessage
                {
                    DiagnosticType = DiagnosticType.Error,
                    MessageData = $"[{method.DeclaringType.FullName}] {message}"
                });
            }
        }

        private static SequencePoint GetSequence(Instruction instruction, MethodDefinition method)
        {
            while (true)
            {
                var sq = method.DebugInformation.GetSequencePoint(instruction);

                if (sq == null)
                {
                    instruction = instruction.Previous;
                    continue;
                }

                return sq;
            }
        }

        private static Instruction ConvertToConstS(ILProcessor processor, Instruction instruction, List<DiagnosticMessage> messages, Dictionary<Instruction, Instruction> replacementMap)
        {
            switch (instruction.OpCode.Code)
            {
                case Code.Ldc_R8:
                {
                    Error(messages, $"Usage of `double` is not supported in sfloat math for now.", instruction, processor.Body.Method);
                    return null;
                }
                case Code.Ldc_R4:
                {
                    float value = (float)instruction.Operand;
                    var constFp = processor.Create(OpCodes.Ldc_I4, (int)sfloat.FromFloat(value).rawValue);
                    Replace(processor, instruction, constFp, replacementMap);
                    return constFp;
                }
                default:
                {
                    Error(messages, $"You can only do math with constant values and not runtime evaluated values.", instruction, processor.Body.Method);
                    return null;
                }
            }
        }

        private static Instruction ConvertToConstFP(ILProcessor processor, Instruction instruction, List<DiagnosticMessage> messages, Dictionary<Instruction, Instruction> replacementMap)
        {
            switch (instruction.OpCode.Code)
            {
                case Code.Ldc_R8:
                {
                    double value = (double)instruction.Operand;
                    var constFp = processor.Create(OpCodes.Ldc_I8, MathFP.FromDouble(value));
                    Replace(processor, instruction, constFp, replacementMap);
                    return constFp;
                }
                case Code.Ldc_R4:
                {
                    float value = (float)instruction.Operand;
                    var constFp = processor.Create(OpCodes.Ldc_I8, MathFP.FromDouble(value));
                    Replace(processor, instruction, constFp, replacementMap);
                    return constFp;
                }
                default:
                {
                    Error(messages, $"You can only do math with constant `float` and not runtime values.", instruction, processor.Body.Method);
                    return null;
                }
            }
        }
    }
}
