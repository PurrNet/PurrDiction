using BEPUutilities;
using FixMath.NET;
using PurrNet.Modules;
using PurrNet.Packing;

namespace PurrNet.Prediction
{
    public static class FPWriters
    {
        [UsedByIL]
        public static void WriteFP(BitPacker packer, FP value)
        {
            Packer<long>.Write(packer, value.RawValue);
        }
        
        [UsedByIL]
        public static void ReadFP(BitPacker packer, ref FP value)
        {
            long rawValue = default;
            Packer<long>.Read(packer, ref rawValue);
            value = FP.FromRaw(rawValue);
        }
        
        [UsedByIL]
        public static void WriteFPVector2(BitPacker packer, FPVector2 value)
        {
            WriteFP(packer, value.x);
            WriteFP(packer, value.y);
        }
        
        [UsedByIL]
        public static void ReadFPVector2(BitPacker packer, ref FPVector2 value)
        {
            ReadFP(packer, ref value.x);
            ReadFP(packer, ref value.y);
        }
        
        [UsedByIL]
        public static void WriteFPVector3(BitPacker packer, FPVector3 value)
        {
            WriteFP(packer, value.x);
            WriteFP(packer, value.y);
            WriteFP(packer, value.z);
        }
        
        [UsedByIL]
        public static void ReadFPVector3(BitPacker packer, ref FPVector3 value)
        {
            ReadFP(packer, ref value.x);
            ReadFP(packer, ref value.y);
            ReadFP(packer, ref value.z);
        }
        
        [UsedByIL]
        public static void WriteFPVector4(BitPacker packer, FPVector4 value)
        {
            WriteFP(packer, value.x);
            WriteFP(packer, value.y);
            WriteFP(packer, value.z);
            WriteFP(packer, value.w);
        }
        
        [UsedByIL]
        public static void ReadFPVector4(BitPacker packer, ref FPVector4 value)
        {
            ReadFP(packer, ref value.x);
            ReadFP(packer, ref value.y);
            ReadFP(packer, ref value.z);
            ReadFP(packer, ref value.w);
        }
        
        [UsedByIL]
        public static void WriteFPQuaternion(BitPacker packer, FPQuaternion value)
        {
            WriteFP(packer, value.x);
            WriteFP(packer, value.y);
            WriteFP(packer, value.z);
            WriteFP(packer, value.w);
        }
        
        [UsedByIL]
        public static void ReadFPQuaternion(BitPacker packer, ref FPQuaternion value)
        {
            ReadFP(packer, ref value.x);
            ReadFP(packer, ref value.y);
            ReadFP(packer, ref value.z);
            ReadFP(packer, ref value.w);
        }
    }
}
