namespace PurrNet.Prediction
{
    public interface IBepuCollisionEnter
    {
        public void OnBepuCollisionEnter(BepuCollisionData data);
    }
    
    public interface IBepuCollisionExit
    {
        public void OnBepuCollisionExit(BepuCollisionData other);
    }

    public interface IBepuTriggerEnter
    {
        public void OnBepuTriggerEnter(BepuCollisionData data);
    }
    
    public interface IBepuTriggerExit
    {
        public void OnBepuTriggerExit(BepuCollisionData other);
    }
}
