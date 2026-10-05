using UnityEngine;

namespace PurrNet.Prediction.Tests
{
    public class OhYeahBaby : StatelessPredictedIdentity
    {
        [SerializeField] private PredictedRigidbody _rb;

        protected override void LateAwake()
        {
            _rb.onPredictedTriggerEnter += OnPTriggerEnter;
            _rb.onPredictedTriggerExit += OnPTriggerExit;
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            _rb.onPredictedTriggerEnter -= OnPTriggerEnter;
            _rb.onPredictedTriggerExit -= OnPTriggerExit;
        }

        private void OnPTriggerEnter(PredictedTrigger trigger)
        {
            if (!isServer)
                return;
            if (trigger.other && trigger.other.TryGetComponent<SimpleCC>(out var controller))
            {
                var players = predictionManager.players.players;
                for (var i = 0; i < players.Count; i++)
                {
                    var player = players[i];
                    predictionManager.HideFrom(player, controller.id.objectId);
                }
            }
        }

        private void OnPTriggerExit(PredictedTrigger trigger)
        {
            if (!isServer)
                return;
            if (trigger.other && trigger.other.TryGetComponent<SimpleCC>(out var controller))
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
