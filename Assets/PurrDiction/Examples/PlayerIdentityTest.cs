using PurrNet;
using UnityEngine;

namespace PurrDiction.Examples
{
    public class PlayerIdentityTest : PlayerIdentity<PlayerIdentityTest>
    {
        protected override void OnOwnerChanged(PlayerID? oldOwner, PlayerID? newOwner, bool asServer)
        {
            base.OnOwnerChanged(oldOwner, newOwner, asServer);
            
            Debug.Log($"OnOwnerChanged: {newOwner} | asServer: {asServer}");
        }
    }
}
