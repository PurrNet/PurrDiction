using PurrNet.Packing;
using PurrNet.Pooling;
using UnityEngine;

namespace PurrNet.Prediction.Tests
{
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
            var newDirty = DisposableList<Size>.Create();
            var dirtyIndexes = currentState.dirtyIndexes.list;
            int count = dirtyIndexes.Count;

            for (int j = count - 1; j >= 0; j--)
            {
                int index = dirtyIndexes[j];
                var down = index + _gridSize;

                // try to move down first
                if (!GetGridValue(down))
                {
                    state.grid[index] = false;
                    state.grid[down] = true;
                    InsertDirtyIndex(newDirty, down);
                    var up = index - _gridSize;
                    if (up >= 0)
                        InsertDirtyIndex(newDirty, up);
                    continue;
                }

                int x = index % _gridSize;
                var left = x == 0 ?               -1 : index - 1 + _gridSize;
                var right = x == _gridSize - 1 ?  -1 : index + 1 + _gridSize;

                bool canMoveLeft = !GetGridValue(left);
                bool canMoveRight = !GetGridValue(right);

                switch (canMoveLeft)
                {
                    case true when canMoveRight:
                    {
                        bool random = state.random.Next(2) == 0;
                        int nx = random ? left : right;
                        state.grid[index] = false;
                        state.grid[nx] = true;
                        InsertDirtyIndex(newDirty, nx);
                        break;
                    }
                    case true:
                        state.grid[index] = false;
                        state.grid[left] = true;
                        InsertDirtyIndex(newDirty, left);
                        break;
                    default:
                    {
                        if (canMoveRight)
                        {
                            state.grid[index] = false;
                            state.grid[right] = true;
                            InsertDirtyIndex(newDirty, right);
                        }
                        break;
                    }
                }
            }

            state.dirtyIndexes.Dispose();
            state.dirtyIndexes = newDirty;
        }

        bool GetGridValue(int index)
        {
            if (index < 0 ||index >= _gridSize * _gridSize)
                return true;
            return currentState.grid[index];
        }

        public void SetGridValue(int index)
        {
            if (index < 0 ||index >= _gridSize * _gridSize)
                return;

            if (currentState.grid[index])
                return;

            InsertDirtyIndex(index);
            currentState.grid[index] = true;
        }

        private static void InsertDirtyIndex(DisposableList<Size> list, int index)
        {
            var existingCount = list.Count;
            int posToInsert = 0;

            for (var i = 0; i < existingCount; ++i)
            {
                posToInsert = i;
                var curVal = list[i].value;
                if (curVal > index)
                {
                    if (curVal == index)
                        return;
                    break;
                }
            }

            list.Insert(posToInsert, index);
        }

        private void InsertDirtyIndex(int index)
        {
            var existingCount = currentState.dirtyIndexes.Count;
            int posToInsert = 0;

            for (var i = 0; i < existingCount; ++i)
            {
                posToInsert = i;
                if (currentState.dirtyIndexes[i].value > index)
                    break;
            }

            currentState.dirtyIndexes.Insert(posToInsert, index);
        }

        protected override FallingSandState GetInitialState()
        {
            return new FallingSandState
            {
                grid = DisposableArray<bool>.Create(_gridSize * _gridSize),
                dirtyIndexes = DisposableList<Size>.Create(),
                random = PredictedRandom.Create(_seed)
            };
        }
    }
}
