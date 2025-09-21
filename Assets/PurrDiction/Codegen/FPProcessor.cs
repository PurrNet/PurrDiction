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
        static readonly string FP_FullName = typeof(FP).FullName;
        static readonly string FP64_FullName = typeof(FP64).FullName;

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

                var fp32Type = module.GetTypeDefinition<FP>();
                var fromRawInt = fp32Type.GetMethod("FromRaw").Import(module);
                var opAdditionFp_Fp = fp32Type.GetMethodWithParams("op_Addition", typeof(FP), typeof(FP)).Import(module);
                var opSubstractionFp_Fp = fp32Type.GetMethodWithParams("op_Subtraction", typeof(FP), typeof(FP)).Import(module);
                var opMultiplyFp_Fp = fp32Type.GetMethodWithParams("op_Multiply", typeof(FP), typeof(FP)).Import(module);
                var opDivisionFp_Fp = fp32Type.GetMethodWithParams("op_Division", typeof(FP), typeof(FP)).Import(module);
                var opModulusFp_Fp = fp32Type.GetMethodWithParams("op_Modulus", typeof(FP), typeof(FP)).Import(module);

                var fp64Type = module.GetTypeDefinition<FP64>();
                var fromRawLong = fp64Type.GetMethod("FromRaw").Import(module);
                var opAdditionFp64_Fp64 = fp64Type.GetMethodWithParams("op_Addition", typeof(FP64), typeof(FP64)).Import(module);
                var opSubstractionFp64_Fp64 = fp64Type.GetMethodWithParams("op_Subtraction", typeof(FP64), typeof(FP64)).Import(module);
                var opMultiplyFp64_Fp64 = fp64Type.GetMethodWithParams("op_Multiply", typeof(FP64), typeof(FP64)).Import(module);
                var opDivisionFp64_Fp64 = fp64Type.GetMethodWithParams("op_Division", typeof(FP64), typeof(FP64)).Import(module);
                var opModulusFp64_Fp64 = fp64Type.GetMethodWithParams("op_Modulus", typeof(FP64), typeof(FP64)).Import(module);

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

                        if (methodDef.DeclaringType.FullName != FP_FullName ||
                            methodDef.DeclaringType.FullName != FP64_FullName)
                            continue;

                        bool is64 = methodDef.DeclaringType.FullName == FP64_FullName;
                        var fromRawOp = is64 ? fromRawLong : fromRawInt;

                        switch (methodDef.Name)
                        {
                            case "op_Implicit" when methodDef.Parameters.Count == 1 && methodDef.Parameters[0].ParameterType.FullName == "System.Single":
                            {
                                var res = ConvertToConstFP(is64, ilProcessor, instructions[j - 1], messages);
                                if (res == null) continue;
                                var fromRaw = ilProcessor.Create(OpCodes.Call, fromRawOp);
                                ilProcessor.Replace(instruction, fromRaw);
                                break;
                            }
                            case "op_Addition" when methodDef.Parameters.Count == 2 && methodDef.Parameters[1].ParameterType.FullName == "System.Single":
                            {
                                var fp = ConvertToConstFP(is64, ilProcessor, instructions[j - 1], messages);
                                if (fp == null) continue;
                                var addOp = is64 ? opAdditionFp64_Fp64 : opAdditionFp_Fp;
                                var toRaw = ilProcessor.Create(OpCodes.Call, addOp);
                                ilProcessor.InsertAfter(fp, ilProcessor.Create(OpCodes.Call, fromRawOp));
                                ilProcessor.Replace(instruction, toRaw);
                                break;
                            }
                            case "op_Addition" when methodDef.Parameters.Count == 2 && methodDef.Parameters[0].ParameterType.FullName == "System.Single":
                            {
                                var fp = ConvertToConstFP(is64, ilProcessor, instructions[j - 2], messages);
                                if (fp == null) continue;
                                var addOp = is64 ? opAdditionFp64_Fp64 : opAdditionFp_Fp;
                                var toRaw = ilProcessor.Create(OpCodes.Call, addOp);
                                ilProcessor.InsertAfter(fp, ilProcessor.Create(OpCodes.Call, fromRawOp));
                                ilProcessor.Replace(instruction, toRaw);
                                break;
                            }
                            case "op_Subtraction" when methodDef.Parameters.Count == 2 && methodDef.Parameters[1].ParameterType.FullName == "System.Single":
                            {
                                var fp = ConvertToConstFP(is64, ilProcessor, instructions[j - 1], messages);
                                if (fp == null) continue;
                                var subOp = is64 ? opSubstractionFp64_Fp64 : opSubstractionFp_Fp;
                                var toRaw = ilProcessor.Create(OpCodes.Call, subOp);
                                ilProcessor.InsertAfter(fp, ilProcessor.Create(OpCodes.Call, fromRawOp));
                                ilProcessor.Replace(instruction, toRaw);
                                break;
                            }
                            case "op_Subtraction" when methodDef.Parameters.Count == 2 && methodDef.Parameters[0].ParameterType.FullName == "System.Single":
                            {
                                var fp = ConvertToConstFP(is64, ilProcessor, instructions[j - 2], messages);
                                if (fp == null) continue;
                                var subOp = is64 ? opSubstractionFp64_Fp64 : opSubstractionFp_Fp;
                                var toRaw = ilProcessor.Create(OpCodes.Call, subOp);
                                ilProcessor.InsertAfter(fp, ilProcessor.Create(OpCodes.Call, fromRawOp));
                                ilProcessor.Replace(instruction, toRaw);
                                break;
                            }
                            case "op_Multiply" when methodDef.Parameters.Count == 2 && methodDef.Parameters[1].ParameterType.FullName == "System.Single":
                            {
                                var fp = ConvertToConstFP(is64, ilProcessor, instructions[j - 1], messages);
                                if (fp == null) continue;
                                var multpyOp = is64 ? opMultiplyFp64_Fp64 : opMultiplyFp_Fp;
                                var toRaw = ilProcessor.Create(OpCodes.Call, multpyOp);
                                ilProcessor.InsertAfter(fp, ilProcessor.Create(OpCodes.Call, fromRawOp));
                                ilProcessor.Replace(instruction, toRaw);
                                break;
                            }
                            case "op_Multiply" when methodDef.Parameters.Count == 2 && methodDef.Parameters[0].ParameterType.FullName == "System.Single":
                            {
                                var fp = ConvertToConstFP(is64, ilProcessor, instructions[j - 2], messages);
                                if (fp == null) continue;
                                var multpyOp = is64 ? opMultiplyFp64_Fp64 : opMultiplyFp_Fp;
                                var toRaw = ilProcessor.Create(OpCodes.Call, multpyOp);
                                ilProcessor.InsertAfter(fp, ilProcessor.Create(OpCodes.Call, fromRawOp));
                                ilProcessor.Replace(instruction, toRaw);
                                break;
                            }
                            case "op_Division" when methodDef.Parameters.Count == 2 && methodDef.Parameters[1].ParameterType.FullName == "System.Single":
                            {
                                var fp = ConvertToConstFP(is64, ilProcessor, instructions[j - 1], messages);
                                if (fp == null) continue;
                                var divOp = is64 ? opDivisionFp64_Fp64 : opDivisionFp_Fp;
                                var toRaw = ilProcessor.Create(OpCodes.Call, divOp);
                                ilProcessor.InsertAfter(fp, ilProcessor.Create(OpCodes.Call, fromRawOp));
                                ilProcessor.Replace(instruction, toRaw);
                                break;
                            }
                            case "op_Division" when methodDef.Parameters.Count == 2 && methodDef.Parameters[0].ParameterType.FullName == "System.Single":
                            {
                                var fp = ConvertToConstFP(is64, ilProcessor, instructions[j - 2], messages);
                                if (fp == null) continue;
                                var divOp = is64 ? opDivisionFp64_Fp64 : opDivisionFp_Fp;
                                var toRaw = ilProcessor.Create(OpCodes.Call, divOp);
                                ilProcessor.InsertAfter(fp, ilProcessor.Create(OpCodes.Call, fromRawOp));
                                ilProcessor.Replace(instruction, toRaw);
                                break;
                            }
                            case "op_Modulus" when methodDef.Parameters.Count == 2 && methodDef.Parameters[1].ParameterType.FullName == "System.Single":
                            {
                                var fp = ConvertToConstFP(is64, ilProcessor, instructions[j - 1], messages);
                                if (fp == null) continue;
                                var modOp = is64 ? opModulusFp64_Fp64 : opModulusFp_Fp;
                                var toRaw = ilProcessor.Create(OpCodes.Call, modOp);
                                ilProcessor.InsertAfter(fp, ilProcessor.Create(OpCodes.Call, fromRawOp));
                                ilProcessor.Replace(instruction, toRaw);
                                break;
                            }
                            case "op_Modulus" when methodDef.Parameters.Count == 2 && methodDef.Parameters[0].ParameterType.FullName == "System.Single":
                            {
                                var fp = ConvertToConstFP(is64, ilProcessor, instructions[j - 2], messages);
                                if (fp == null) continue;
                                var modOp = is64 ? opModulusFp64_Fp64 : opModulusFp_Fp;
                                var toRaw = ilProcessor.Create(OpCodes.Call, modOp);
                                ilProcessor.InsertAfter(fp, ilProcessor.Create(OpCodes.Call, fromRawOp));
                                ilProcessor.Replace(instruction, toRaw);
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

        public static void Error(ICollection<DiagnosticMessage> messages, string message, Instruction instruction, MethodDefinition method)
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

        private static Instruction ConvertToConstFP(bool is64, ILProcessor processor, Instruction instruction, List<DiagnosticMessage> messages)
        {
            switch (instruction.OpCode.Code)
            {
                case Code.Ldc_R8:
                {
                    double value = (double)instruction.Operand;
                    var constFp =
                        is64 ? processor.Create(OpCodes.Ldc_R8, FP64Math.FromDouble(value))
                            : processor.Create(OpCodes.Ldc_I4, FPMath.FromDouble(value));
                    processor.Replace(instruction, constFp);
                    return constFp;
                }
                case Code.Ldc_R4:
                {
                    float value = (float)instruction.Operand;
                    var constFp =
                        is64 ? processor.Create(OpCodes.Ldc_R8, FP64Math.FromDouble(value))
                            : processor.Create(OpCodes.Ldc_I4, FPMath.FromDouble(value));
                    processor.Replace(instruction, constFp);
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
