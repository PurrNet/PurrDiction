using System;
using UnityEngine;

namespace PurrNet.Prediction.Tests
{
    public class TestFallingSandRenderer : MonoBehaviour
    {
        [SerializeField] private float _width = 50;
        [SerializeField] private float _padding = 2;

        public TestFallingSand sand;
        public event Action<int> onClicked;

        private void Awake()
        {
            InstanceHandler.RegisterInstance(this);
        }

        private void OnDestroy()
        {
            InstanceHandler.UnregisterInstance<TestFallingSandRenderer>();
        }

        private void OnGUI()
        {
            var viewState = sand.viewState;
            if (viewState.grid.isDisposed)
                return;

            var mousePos = Event.current.mousePosition;
            bool isMouseHeld = Input.GetMouseButton(0);

            for (int i = 0; i < viewState.grid.Count; i++)
            {
                int x = i % sand.gridSize;
                int y = i / sand.gridSize;

                GUI.color = viewState.grid[i] ? Color.red : Color.white;

                var rect = new Rect(
                    x * _width + _padding,
                    100 + y * _width + _padding,
                    _width - _padding * 2,
                    _width - _padding * 2
                );

                GUI.DrawTexture(rect, Texture2D.whiteTexture);

                // Single click (GUI.Button)
                if (GUI.Button(rect, "", GUI.skin.label))
                    onClicked?.Invoke(i);

                // Drag detection (hold + move)
                if (isMouseHeld && rect.Contains(mousePos))
                    onClicked?.Invoke(i);
            }
        }
    }
}
