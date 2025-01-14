using PurrNet.Packing;

namespace PurrNet.Prediction
{
    public enum PredictedActionType
    {
        Instantiate,
        Destroy
    }
    
    public struct PredictedAction : IPackedSimple
    {
        public PredictedActionType type;

        public PredictedInstantiate instantiateAction;
        public PredictedDestroy destroyAction;
        
        public PredictedAction(PredictedInstantiate instantiateAction) : this()
        {
            type = PredictedActionType.Instantiate;
            this.instantiateAction = instantiateAction;
        }
        
        public PredictedAction(PredictedDestroy destroyAction) : this()
        {
            type = PredictedActionType.Destroy;
            this.destroyAction = destroyAction;
        }

        public void Serialize(BitPacker packer)
        {
            int actionType = (int)type;
            Packer<int>.Serialize(packer, ref actionType);
            type = (PredictedActionType)actionType;
            
            switch (type)
            {
                case PredictedActionType.Instantiate: 
                    Packer<PredictedInstantiate>.Serialize(packer, ref instantiateAction); break;
                case PredictedActionType.Destroy: 
                    Packer<PredictedDestroy>.Serialize(packer, ref destroyAction); break;
                default: throw new System.NotImplementedException();
            }
        }
        
        public bool Matches(PredictedAction other)
        {
            if (type != other.type)
                return false;

            return type switch
            {
                PredictedActionType.Instantiate => instantiateAction.Matches(other.instantiateAction),
                PredictedActionType.Destroy => destroyAction.Matches(other.destroyAction),
                _ => throw new System.NotImplementedException()
            };
        }
    }
}