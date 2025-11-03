using System;
using System.Collections.Generic;
using System.IO;
using JetBrains.Annotations;
using Mono.Cecil;
using Mono.Cecil.Cil;
using PurrNet.Codegen;
using PurrNet.Pooling;
using PurrNet.Prediction;
using Unity.CompilationPipeline.Common.Diagnostics;
using Unity.CompilationPipeline.Common.ILPostProcessing;

namespace Purrdiction.Codegen
{
    [UsedImplicitly]
    public class PredictionProcessor : ILPostProcessor
    {
        static DisposableList<TypeDefinition> GetAllTypes(ModuleDefinition module)
        {
            var types = DisposableList<TypeDefinition>.Create(32);

            types.AddRange(module.Types);
            foreach (var type in module.Types)
                types.AddRange(type.NestedTypes);

            return types;
        }

        private static bool HasPurrDictionAsReference(string myName, ModuleDefinition module)
        {
            if (myName == "PurrNet.Prediction")
                return true;

            bool hasRef = false;

            foreach (var reference in module.AssemblyReferences)
            {
                if (reference.Name == "PurrNet.Prediction")
                {
                    hasRef = true;
                    break;
                }
            }

            return hasRef;
        }

        public override ILPostProcessResult Process(ICompiledAssembly compiledAssembly)
        {
            try
            {
                if (!WillProcess(compiledAssembly))
                    return null!;

                var messages = new List<DiagnosticMessage>();

                using var peStream = new MemoryStream(compiledAssembly.InMemoryAssembly.PeData);
                using var pdbStream = new MemoryStream(compiledAssembly.InMemoryAssembly.PdbData);
                var resolver = new AssemblyResolver(compiledAssembly);

                var assemblyDefinition = AssemblyDefinition.ReadAssembly(peStream, new ReaderParameters
                {
                    ReadSymbols = true,
                    SymbolStream = pdbStream,
                    SymbolReaderProvider = new PortablePdbReaderProvider(),
                    AssemblyResolver = resolver
                });

                resolver.SetSelf(assemblyDefinition);

                for (var m = 0; m < assemblyDefinition.Modules.Count; m++)
                {
                    var module = assemblyDefinition.Modules[m];
                    var hasPurrDictionAsReference = HasPurrDictionAsReference(compiledAssembly.Name, module);

                    if (!hasPurrDictionAsReference)
                        continue;

                    using var types = GetAllTypes(module);

                    for (var t = 0; t < types.Count; t++)
                    {
                        var type = types[t];

                        FPProcessor.HandleType(module, type, messages);

                        if (!PostProcessor.InheritsFrom(type, typeof(PredictedIdentity).FullName))
                            continue;

                        var methods = type.Methods;

                        for (var i = 0; i < methods.Count; i++)
                        {
                            var method = methods[i];
                            var attributes = method.CustomAttributes;

                            for (var a = 0; a < attributes.Count; a++)
                            {
                                var attribute = attributes[a];

                                if (attribute.AttributeType.FullName != typeof(SimulationOnlyAttribute).FullName)
                                    continue;

                                var returnType = method.ReturnType;

                                if (returnType.FullName != module.TypeSystem.Void.FullName)
                                {
                                    messages.Add(new DiagnosticMessage
                                    {
                                        DiagnosticType = DiagnosticType.Error,
                                        MessageData = $"[<b>{method.FullName}</b>] SimulationOnly attribute must return void",
                                    });
                                    continue;
                                }

                                ProcessMethod(method, module);
                            }
                        }
                    }
                }

                var pe = new MemoryStream();
                var pdb = new MemoryStream();

                var writerParameters = new WriterParameters
                {
                    WriteSymbols = true,
                    SymbolStream = pdb,
                    SymbolWriterProvider = new PortablePdbWriterProvider()
                };

                try
                {
                    assemblyDefinition.Write(pe, writerParameters);
                }
                catch (Exception e)
                {
                    messages.Add(new DiagnosticMessage
                    {
                        DiagnosticType = DiagnosticType.Error,
                        MessageData = $"Failed to write assembly ({compiledAssembly.Name}): {e.Message}\n{e.StackTrace}",
                    });
                }

                return new ILPostProcessResult(new InMemoryAssembly(pe.ToArray(), pdb.ToArray()), messages);
            }
            catch (Exception e)
            {
                var messages = new List<DiagnosticMessage> {
                    new()
                    {
                        DiagnosticType = DiagnosticType.Error,
                        MessageData = $"Unhandled exception {e.Message}\n{e.StackTrace}",
                    }
                };

                return new ILPostProcessResult(compiledAssembly.InMemoryAssembly, messages);
            }
        }

        private static void ProcessMethod(MethodDefinition method, ModuleDefinition module)
        {
            var predictedIdentity = module.GetTypeDefinition<PredictedIdentity>();
            var isSimulating = predictedIdentity.GetMethod("IsSimulating").Import(module);

            var instructions = method.Body.Instructions;
            var processor = method.Body.GetILProcessor();
            var first = instructions[0];

            processor.InsertBefore(first, processor.Create(OpCodes.Ldarg_0));
            processor.InsertBefore(first, processor.Create(OpCodes.Call, isSimulating));
            processor.InsertBefore(first, processor.Create(OpCodes.Brtrue, first));
            processor.InsertBefore(first, processor.Create(OpCodes.Ret));
        }

        public override ILPostProcessor GetInstance() => this;

        public override bool WillProcess(ICompiledAssembly compiledAssembly)
        {
            var name = compiledAssembly.Name;

            if (name.StartsWith("Unity."))
                return false;

            if (name.StartsWith("UnityEngine."))
                return false;

            return !name.Contains("Editor");
        }
    }
}
