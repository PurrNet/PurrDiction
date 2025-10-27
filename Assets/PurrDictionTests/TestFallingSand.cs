using PurrNet.Pooling;
using UnityEngine;

namespace PurrNet.Prediction.Tests
{
    public struct FallingSandState : IPredictedData<FallingSandState>
    {
        public DisposableArray<bool> grid;
        public PredictedRandom random;
        public void Dispose() { }
    }

    public class TestFallingSand : DeterministicIdentity<FallingSandState>
    {
        [SerializeField] private int _gridSize = 10;
        [SerializeField] private uint _seed = 65645;

        public int gridSize => _gridSize;

        protected override void Simulate(ref FallingSandState state, sfloat delta)
        {
            TickSimulation(ref state);
        }

        private void TickSimulation(ref FallingSandState state)
        {
            var nextState = DisposableArray<bool>.Create(_gridSize * _gridSize);

            // iterate from bottom to top
            for (int y = _gridSize - 1; y >= 0; y--)
            {
                for (int x = 0; x < _gridSize; x++)
                {
                    int i = y * _gridSize + x;
                    if (!state.grid[i])
                        continue;

                    // try to move down first
                    if (!GetGridValue(x, y + 1, nextState))
                    {
                        nextState[(y + 1) * _gridSize + x] = true;
                        continue;
                    }

                    bool canMoveLeft = !GetGridValue(x - 1, y + 1, nextState);
                    bool canMoveRight = !GetGridValue(x + 1, y + 1, nextState);

                    switch (canMoveLeft)
                    {
                        case true when canMoveRight:
                        {
                            bool random = state.random.Next(2) == 0;
                            int nx = random ? x - 1 : x + 1;
                            nextState[(y + 1) * _gridSize + nx] = true;
                            continue;
                        }
                        case true:
                            nextState[(y + 1) * _gridSize + (x - 1)] = true;
                            continue;
                    }

                    if (canMoveRight)
                    {
                        nextState[(y + 1) * _gridSize + (x + 1)] = true;
                        continue;
                    }

                    // can't move, stay still
                    nextState[i] = true;
                }
            }

            state.grid.Dispose();
            state.grid = nextState;
        }

        public bool GetGridValue(int x, int y, DisposableArray<bool> nextState)
        {
            if (x < 0 || x >= _gridSize || y < 0 || y >= _gridSize)
                return true; // out of bounds = solid
            int index = y * _gridSize + x;
            return currentState.grid[index] || nextState[index];
        }

        public void SetGridValue(int index)
        {
            if (index < 0 ||index >= _gridSize * _gridSize)
                return;
            currentState.grid[index] = true;
        }

        protected override FallingSandState GetInitialState()
        {
            return new FallingSandState
            {
                grid = DisposableArray<bool>.Create(_gridSize * _gridSize),
                random = PredictedRandom.Create(_seed)
            };
        }
    }
}
