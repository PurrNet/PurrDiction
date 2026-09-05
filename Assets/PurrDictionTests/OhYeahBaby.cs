using UnityEngine;

namespace PurrNet.Prediction.Tests
{
    public class OhYeahBaby : StatelessPredictedIdentity
    {
        [SerializeField] private PredictedRigidbody _rb;

        protected override void LateAwake()
        {
            _rb.onTriggerEnter += OnPTriggerEnter;
            _rb.onTriggerExit += OnPTriggerExit;
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            _rb.onTriggerEnter -= OnPTriggerEnter;
            _rb.onTriggerExit -= OnPTriggerExit;
        }

        private void OnPTriggerEnter(GameObject other, PredictedComponentID otherId)
        {
            if (!isServer)
                return;
            if (!other)
                return;
            if (other.TryGetComponent<SimpleCC>(out var controller))
            {
                var players = predictionManager.players.players;
                for (var i = 0; i < players.Count; i++)
                {
                    var player = players[i];
                    predictionManager.HideFrom(player, controller.id.objectId);
                }
            }
        }

        private void OnPTriggerExit(GameObject other, PredictedComponentID otherId)
        {
            if (!isServer)
                return;
            if (!other)
                return;
            if (other.TryGetComponent<SimpleCC>(out var controller))
            {
                var players = predictionManager.players.players;
                for (var i = 0; i < players.Count; i++)
                {
                    var player = players[i];
                    predictionManager.ShowTo(player, controller.id.objectId);
                }
            }
        }
    }
}
