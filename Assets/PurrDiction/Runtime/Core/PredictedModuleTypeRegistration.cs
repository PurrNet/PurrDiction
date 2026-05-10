using System;
using PurrNet.Packing;
using PurrNet.Utils;

namespace PurrNet.Prediction
{
    internal static class PredictedModuleTypeRegistration
    {
        [RegisterPackers]
        private static void RegisterAllModuleTypes()
        {
            var moduleBase = typeof(PredictedModule);
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int a = 0; a < assemblies.Length; a++)
            {
                Type[] types;
                try { types = assemblies[a].GetTypes(); }
                catch { continue; }

                for (int t = 0; t < types.Length; t++)
                {
                    var type = types[t];
                    if (type.IsAbstract) continue;
                    if (!moduleBase.IsAssignableFrom(type)) continue;
                    Hasher.PrepareType(type);
                }
            }
        }
    }
}
