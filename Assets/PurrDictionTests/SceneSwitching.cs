using UnityEngine.SceneManagement;

namespace PurrNet.Prediction.Tests
{
    public class SceneSwitching : NetworkBehaviour
    {
        [PurrButton]
        public void LoadScene0()
        {
            networkManager.sceneModule.LoadSceneAsync(0, new LoadSceneParameters(LoadSceneMode.Additive, LocalPhysicsMode.Physics3D));
        }

        [PurrButton]
        public void LoadScene1()
        {
            networkManager.sceneModule.LoadSceneAsync(1, new LoadSceneParameters(LoadSceneMode.Additive, LocalPhysicsMode.Physics3D));
        }
    }
}
