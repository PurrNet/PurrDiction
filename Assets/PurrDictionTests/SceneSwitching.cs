namespace PurrNet.Prediction.Tests
{
    public class SceneSwitching : NetworkBehaviour
    {
        [PurrButton]
        public void LoadScene0()
        {
            networkManager.sceneModule.LoadSceneAsync(0);
        }

        [PurrButton]
        public void LoadScene1()
        {
            networkManager.sceneModule.LoadSceneAsync(1);
        }
    }
}
