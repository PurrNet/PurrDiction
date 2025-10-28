using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace PurrNet.Prediction.Tests
{
    public class FallingSandEvents : MonoBehaviour, IPointerDownHandler, IPointerExitHandler, IPointerMoveHandler, IPointerUpHandler
    {
        [SerializeField] private RectTransform _rectTransform;
        [SerializeField] private TestFallingSand _sand;

        public TestFallingSand sand => _sand;

        public bool isClicking => _clicking;

        public int clickingIndex;

        private bool _clicking;

        private void Awake()
        {
            InstanceHandler.RegisterInstance(this);
        }

        private void OnDestroy()
        {
            InstanceHandler.UnregisterInstance<FallingSandEvents>();
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            _clicking = true;
            TriggerClick(eventData);
        }

        private void TriggerClick(PointerEventData eventData)
        {
            var rect = _rectTransform.rect;
            var pos = eventData.position;
            var rectPos = _rectTransform.position;

            var relativePos = pos - new Vector2(rectPos.x, rectPos.y);
            var normalizedPos = new Vector2(relativePos.x / rect.width, relativePos.y / rect.height);

            normalizedPos.x = 1f - Mathf.Clamp01(normalizedPos.x + 0.5f);

            var gridPos = normalizedPos * _sand.gridSize;
            clickingIndex = Mathf.FloorToInt(gridPos.x) + Mathf.FloorToInt(gridPos.y) * -_sand.gridSize;
        }

        public void OnPointerMove(PointerEventData eventData)
        {
            if (_clicking)
                TriggerClick(eventData);
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            _clicking = false;
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            _clicking = false;
        }
    }
}
