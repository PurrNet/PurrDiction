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
                    type.FullName == typeof(FPMath).FullName)
                    return;

                var fp64Type = module.GetTypeDefinition<FP>();
                var fromRawLong = fp64Type.GetMethod("FromRaw").Import(module);
                var opAdditionFp64_Fp64 = fp64Type.GetMethodWithParams("op_Addition", typeof(FP), typeof(FP)).Import(module);
                var opSubstractionFp64_Fp64 = fp64Type.GetMethodWithParams("op_Subtraction", typeof(FP), typeof(FP)).Import(module);
                var opMultiplyFp64_Fp64 = fp64Type.GetMethodWithParams("op_Multiply", typeof(FP), typeof(FP)).Import(module);
                var opDivisionFp64_Fp64 = fp64Type.GetMethodWithParams("op_Division", typeof(FP), typeof(FP)).Import(module);
                var opModulusFp64_Fp64 = fp64Type.GetMethodWithParams("op_Modulus", typeof(FP), typeof(FP)).Import(module);

                for (var i = 0; i < type.Methods.Count; i++)
                {
                    var method = type.Methods[i];

                    if (method is not { HasBody: true })
                        continue;

                    var instructions = method.Body.Instructions;
                    var ilProcessor = method.Body.GetILProcessor();

                    for (var j = 0; j < instructions.Count; j++)
                    {
                        var instruction = instructions[j];

                        if (instruction.OpCode != OpCodes.Call)
                            continue;

                        var methodReference = (MethodReference)instruction.Operand;

                        var methodDef = methodReference.Resolve();

                        if (methodDef == null)
                            continue;

                        if (!methodDef.IsStatic)
                            continue;

                        if (methodDef.DeclaringType.FullName != fp64Type.FullName)
                            continue;

                        switch (methodDef.Name)
                        {
                            case "op_Implicit" when methodDef.Parameters.Count == 1 && CheckParam(methodDef, 0):
                            {
                                try
                                {
                                    var res = ConvertToConstFP(ilProcessor, instructions[j - 1], messages);
                                    if (res == null) continue;
                                    instruction.OpCode = OpCodes.Call;
                                    instruction.Operand = fromRawLong;
                                }
                                catch
                                {
                                    Error(messages, $"Failed to generate implicit fixed point magic.", instruction, ilProcessor.Body.Method);
                                }
                                break;
                            }
                            case "op_Addition" when methodDef.Parameters.Count == 2 && CheckParam(methodDef, 1):
                            {
                                try
                                {
                                    var fp = ConvertToConstFP(ilProcessor, instructions[j - 1], messages);
                                    if (fp == null) continue;
                                    //var toRaw = ilProcessor.Create(OpCodes.Call, opAdditionFp64_Fp64);
                                    ilProcessor.InsertAfter(fp, ilProcessor.Create(OpCodes.Call, fromRawLong));
                                    //ilProcessor.Replace(instruction, toRaw);
                                    instruction.OpCode = OpCodes.Call;
                                    instruction.Operand = opAdditionFp64_Fp64;
                                }
                                catch
                                {
                                    Error(messages, $"Failed to generate addition fixed point magic.", instruction, ilProcessor.Body.Method);
                                }

                                break;
                            }
                            case "op_Addition" when methodDef.Parameters.Count == 2 && CheckParam(methodDef, 0):
                            {
                                try
                                {
                                    var fp = ConvertToConstFP(ilProcessor, instructions[j - 2], messages);
                                    if (fp == null) continue;
                                    //var toRaw = ilProcessor.Create(OpCodes.Call, opAdditionFp64_Fp64);
                                    ilProcessor.InsertAfter(fp, ilProcessor.Create(OpCodes.Call, fromRawLong));
                                    //ilProcessor.Replace(instruction, toRaw);
                                    instruction.OpCode = OpCodes.Call;
                                    instruction.Operand = opAdditionFp64_Fp64;
                                }
                                catch
                                {
                                    Error(messages, $"Failed to generate addition fixed point magic.", instruction, ilProcessor.Body.Method);
                                }

                                break;
                            }
                            case "op_Subtraction" when methodDef.Parameters.Count == 2 && CheckParam(methodDef, 1):
                            {
                                try
                                {
                                    var fp = ConvertToConstFP(ilProcessor, instructions[j - 1], messages);
                                    if (fp == null) continue;
                                    //var toRaw = ilProcessor.Create(OpCodes.Call, opSubstractionFp64_Fp64);
                                    ilProcessor.InsertAfter(fp, ilProcessor.Create(OpCodes.Call, fromRawLong));
                                    //ilProcessor.Replace(instruction, toRaw);
                                    instruction.OpCode = OpCodes.Call;
                                    instruction.Operand = opSubstractionFp64_Fp64;
                                }
                                catch
                                {
                                    Error(messages, $"Failed to generate subtraction fixed point magic.", instruction, ilProcessor.Body.Method);
                                }

                                break;
                            }
                            case "op_Subtraction" when methodDef.Parameters.Count == 2 && CheckParam(methodDef, 0):
                            {
                                try
                                {
                                    var fp = ConvertToConstFP(ilProcessor, instructions[j - 2], messages);
                                    if (fp == null) continue;
                                    //var toRaw = ilProcessor.Create(OpCodes.Call, opSubstractionFp64_Fp64);
                                    ilProcessor.InsertAfter(fp, ilProcessor.Create(OpCodes.Call, fromRawLong));
                                    //ilProcessor.Replace(instruction, toRaw);
                                    instruction.OpCode = OpCodes.Call;
                                    instruction.Operand = opSubstractionFp64_Fp64;
                                }
                                catch
                                {
                                    Error(messages, $"Failed to generate subtraction fixed point magic.", instruction, ilProcessor.Body.Method);
                                }
                                break;
                            }
                            case "op_Multiply" when methodDef.Parameters.Count == 2 && CheckParam(methodDef, 1):
                            {
                                try
                                {
                                    var fp = ConvertToConstFP(ilProcessor, instructions[j - 1], messages);
                                    if (fp == null) continue;
                                    //var toRaw = ilProcessor.Create(OpCodes.Call, opMultiplyFp64_Fp64);
                                    ilProcessor.InsertAfter(fp, ilProcessor.Create(OpCodes.Call, fromRawLong));
                                    //ilProcessor.Replace(instruction, toRaw);
                                    instruction.OpCode = OpCodes.Call;
                                    instruction.Operand = opMultiplyFp64_Fp64;
                                }
                                catch
                                {
                                    Error(messages, $"Failed to generate multiplication fixed point magic.", instruction, ilProcessor.Body.Method);
                                }
                                break;
                            }
                            case "op_Multiply" when methodDef.Parameters.Count == 2 && CheckParam(methodDef, 0):
                            {
                                try
                                {
                                    var fp = ConvertToConstFP(ilProcessor, instructions[j - 2], messages);
                                    if (fp == null) continue;
                                    //var toRaw = ilProcessor.Create(OpCodes.Call, opMultiplyFp64_Fp64);
                                    ilProcessor.InsertAfter(fp, ilProcessor.Create(OpCodes.Call, fromRawLong));
                                    //ilProcessor.Replace(instruction, toRaw);
                                    instruction.OpCode = OpCodes.Call;
                                    instruction.Operand = opMultiplyFp64_Fp64;
                                }
                                catch
                                {
                                    Error(messages, $"Failed to generate multiplication fixed point magic.", instruction, ilProcessor.Body.Method);
                                }
                                break;
                            }
                            case "op_Division" when methodDef.Parameters.Count == 2 && CheckParam(methodDef, 1):
                            {
                                try
                                {
                                    var fp = ConvertToConstFP(ilProcessor, instructions[j - 1], messages);
                                    if (fp == null) continue;
                                    //var toRaw = ilProcessor.Create(OpCodes.Call, opDivisionFp64_Fp64);
                                    ilProcessor.InsertAfter(fp, ilProcessor.Create(OpCodes.Call, fromRawLong));
                                    //ilProcessor.Replace(instruction, toRaw);
                                    instruction.OpCode = OpCodes.Call;
                                    instruction.Operand = opDivisionFp64_Fp64;
                                }
                                catch
                                {
                                    Error(messages, $"Failed to generate division fixed point magic.", instruction, ilProcessor.Body.Method);
                                }
                                break;
                            }
                            case "op_Division" when methodDef.Parameters.Count == 2 && CheckParam(methodDef, 0):
                            {
                                try
                                {
                                    var fp = ConvertToConstFP(ilProcessor, instructions[j - 2], messages);
                                    if (fp == null) continue;
                                    //var toRaw = ilProcessor.Create(OpCodes.Call, opDivisionFp64_Fp64);
                                    ilProcessor.InsertAfter(fp, ilProcessor.Create(OpCodes.Call, fromRawLong));
                                    //ilProcessor.Replace(instruction, toRaw);
                                    instruction.OpCode = OpCodes.Call;
                                    instruction.Operand = opDivisionFp64_Fp64;
                                }
                                catch
                                {
                                    Error(messages, $"Failed to generate division fixed point magic.", instruction, ilProcessor.Body.Method);
                                }
                                break;
                            }
                            case "op_Modulus" when methodDef.Parameters.Count == 2 && CheckParam(methodDef, 1):
                            {
                                try
                                {
                                    var fp = ConvertToConstFP(ilProcessor, instructions[j - 1], messages);
                                    if (fp == null) continue;
                                    //var toRaw = ilProcessor.Create(OpCodes.Call, opModulusFp64_Fp64);
                                    ilProcessor.InsertAfter(fp, ilProcessor.Create(OpCodes.Call, fromRawLong));
                                    //ilProcessor.Replace(instruction, toRaw);
                                    instruction.OpCode = OpCodes.Call;
                                    instruction.Operand = opModulusFp64_Fp64;
                                }
                                catch
                                {
                                    Error(messages, $"Failed to generate modulus fixed point magic.", instruction, ilProcessor.Body.Method);
                                }
                                break;
                            }
                            case "op_Modulus" when methodDef.Parameters.Count == 2 && CheckParam(methodDef, 0):
                            {
                                try
                                {
                                    var fp = ConvertToConstFP(ilProcessor, instructions[j - 2], messages);
                                    if (fp == null) continue;
                                    //var toRaw = ilProcessor.Create(OpCodes.Call, opModulusFp64_Fp64);
                                    ilProcessor.InsertAfter(fp, ilProcessor.Create(OpCodes.Call, fromRawLong));
                                    //ilProcessor.Replace(instruction, toRaw);
                                    instruction.OpCode = OpCodes.Call;
                                    instruction.Operand = opModulusFp64_Fp64;
                                }
                                catch
                                {
                                    Error(messages, $"Failed to generate modulus fixed point magic.", instruction, ilProcessor.Body.Method);
                                }
                                break;
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

        private static bool CheckParam(MethodDefinition methodDef, int idx)
        {
            var cmp = methodDef.Parameters[idx].ParameterType.FullName;
            return cmp is "System.Single" or "System.Double";
        }

        private static void Error(ICollection<DiagnosticMessage> messages, string message, Instruction instruction, MethodDefinition method)
        {
            if (method.DebugInformation.HasSequencePoints)
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

        private static Instruction ConvertToConstFP(ILProcessor processor, Instruction instruction, List<DiagnosticMessage> messages)
        {
            switch (instruction.OpCode.Code)
            {
                case Code.Ldc_R8:
                {
                    double value = (double)instruction.Operand;
                    //var constFp = processor.Create(OpCodes.Ldc_I8, FPMath.FromDouble(value));
                    instruction.OpCode = OpCodes.Ldc_I8;
                    instruction.Operand = FPMath.FromDouble(value);
                    //processor.Replace(instruction, constFp);
                    return instruction;
                }
                case Code.Ldc_R4:
                {
                    float value = (float)instruction.Operand;
                    //var constFp = processor.Create(OpCodes.Ldc_I8, FPMath.FromDouble(value));
                    instruction.OpCode = OpCodes.Ldc_I8;
                    instruction.Operand = FPMath.FromDouble(value);
                    //processor.Replace(instruction, constFp);
                    return instruction;
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
