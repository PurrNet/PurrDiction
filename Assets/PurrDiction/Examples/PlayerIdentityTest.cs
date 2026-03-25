using PurrNet;
using PurrNet.Logging;

namespace PurrDiction.Examples
{
    public class PlayerIdentityTest : PlayerIdentity<PlayerIdentityTest>
    {
        protected override void OnOwnerChanged(PlayerID? oldOwner, PlayerID? newOwner, bool asServer)
        {
            base.OnOwnerChanged(oldOwner, newOwner, asServer);
            // PurrLogger.Log($"OnOwnerChanged: oldOwner={oldOwner}, newOwner={newOwner}, asServer={asServer}");
        }
    }
}
