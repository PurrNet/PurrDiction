using UnityEngine;

namespace PurrNet.Prediction
{
    public interface IBepuCollisionEnter
    {
        public void OnBepuCollisionEnter(GameObject other);
    }
    
    public interface IBepuCollisionExit
    {
        public void OnBepuCollisionExit(GameObject other);
    }

    public interface IBepuTriggerEnter
    {
        public void OnBepuTriggerEnter(GameObject other);
    }
    
    public interface IBepuTriggerExit
    {
        public void OnBepuTriggerExit(GameObject other);
    }
}
