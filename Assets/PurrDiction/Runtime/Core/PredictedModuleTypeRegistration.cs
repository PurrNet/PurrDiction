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
#if !UNITY_6000_4_OR_NEWER
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            var length = assemblies.Length;
#else
            var assemblies = UnityEngine.Assemblies.CurrentAssemblies.GetLoadedAssemblies();
            var length = assemblies.Count;
#endif
            for (int a = 0; a < length; a++)
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
