using System.Runtime.CompilerServices;
using PurrNet.Packing;
using PurrNet.Pooling;
using UnityEngine;
using UnityEngine.UI;
using Unity.Profiling;

namespace PurrNet.Prediction.Tests
{
    public class TestFallingSand : DeterministicIdentity<FallingSandState>
    {
        [SerializeField] private int _gridSize = 10;
        [SerializeField] private uint _seed = 65645;
        [SerializeField] private RawImage _display;
        [SerializeField] private Texture2D _brush;

        private int _gridSize2;

        private Texture2D _tex;
        private Color32[] _colors;
        private Color32[] _brushColors;

        public int gridSize => _gridSize;

        private void Awake()
        {
            _brushColors = _brush.GetPixels32();
        }

        protected override void LateAwake()
        {
            _gridSize2 = _gridSize * _gridSize;
            int n = _gridSize;
            _tex = new Texture2D(n, n, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point
            };
            _colors = new Color32[n * n];
            _display.texture = _tex;
        }

        static readonly ProfilerMarker SimulateMarker = new("FallingSand.Simulate");
        static readonly ProfilerMarker RenderMarker   = new("FallingSand.Render");

        protected override void Simulate(ref FallingSandState state, sfloat delta)
        {
            using (SimulateMarker.Auto())
            {
                TickSimulation(ref state);
            }
        }

        private void TickSimulation(ref FallingSandState state)
        {
            var newDirty = DisposableList<Size>.Create();
            var dirtyIndexes = currentState.dirtyIndexes.list;
            int count = dirtyIndexes.Count;

            for (int j = count - 1; j >= 0; --j)
            {
                int index = dirtyIndexes[j];
                var tile = currentState.grid[index];

                if (!tile.hasValue)
                    continue;

                var down = index + _gridSize;
                var up = index - _gridSize;

                // try to move down first
                if (!GetGridValue(down))
                {
                    state.grid[index] = default;
                    state.grid[down] = tile;
                    InsertDirtyIndex(newDirty, down);
                    if (up >= 0) InsertDirtyIndex(newDirty, up);
                    continue;
                }

                int x = index % _gridSize;
                var left = x == 0 ?               -1 : down - 1;
                var right = x == _gridSize - 1 ?  -1 : down + 1;

                bool canMoveLeft = !GetGridValue(left);
                bool canMoveRight = !GetGridValue(right);

                switch (canMoveLeft)
                {
                    case true when canMoveRight:
                    {
                        bool random = state.random.Next(2) == 0;
                        int nx = random ? left : right;
                        state.grid[index] = default;
                        state.grid[nx] = tile;
                        InsertDirtyIndex(newDirty, nx);
                        if (up >= 0) InsertDirtyIndex(newDirty, up);
                        break;
                    }
                    case true:
                        state.grid[index] = default;
                        state.grid[left] = tile;
                        InsertDirtyIndex(newDirty, left);
                        if (up >= 0) InsertDirtyIndex(newDirty, up);
                        break;
                    default:
                    {
                        if (canMoveRight)
                        {
                            state.grid[index] = default;
                            state.grid[right] = tile;
                            InsertDirtyIndex(newDirty, right);
                            if (up >= 0) InsertDirtyIndex(newDirty, up);
                        }
                        break;
                    }
                }
            }

            state.dirtyIndexes.Dispose();
            state.dirtyIndexes = newDirty;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool GetGridValue(int index)
        {
            if (index < 0 || index >= _gridSize2)
                return true;
            return currentState.grid[index].hasValue;
        }

        public void SetGridValue(int index, SandTile tile)
        {
            if (index < 0 || index >= _gridSize2)
                return;

            if (currentState.grid[index].hasValue)
                return;

            InsertDirtyIndex(index);
            currentState.grid[index] = tile;
        }

        public void PlaceBrush(int index)
        {
            var width = _brush.width;
            int x = index % _gridSize;
            int y = index / _gridSize;
            var cm1 = _brushColors.Length - 1;
            for (var i = cm1; i >= 0; --i)
            {
                int px = x + i % width - width / 2;
                int py = y + i / width - width / 2;

                int brushIndex = py * _gridSize + px;

                var color = _brushColors[cm1 - i];

                if (color.a < 200)
                    continue;

                var tile = new SandTile
                {
                    color = new ByteColor
                    {
                        R = color.r,
                        G = color.g,
                        B = color.b
                    }
                };

                SetGridValue(brushIndex, tile);
            }
        }

        private static bool BinarySearch(DisposableList<Size> list, int value, out int index)
        {
            int low = 0;
            int high = list.Count - 1;

            while (low <= high)
            {
                int mid = (low + high) / 2;
                if (list[mid].value < value)
                {
                    low = mid + 1;
                }
                else if (list[mid].value > value)
                {
                    high = mid - 1;
                }
                else
                {
                    index = mid;
                    return true;
                }
            }

            index = low;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void InsertDirtyIndex(DisposableList<Size> list, int index)
        {
            if (BinarySearch(list, index, out var result))
                return;

            list.Insert(result, index);
        }

        private void InsertDirtyIndex(int index)
        {
            InsertDirtyIndex(currentState.dirtyIndexes, index);
        }

        protected override FallingSandState GetInitialState()
        {
            return new FallingSandState
            {
                grid = DisposableArray<SandTile>.Create(_gridSize * _gridSize),
                dirtyIndexes = DisposableList<Size>.Create(),
                random = PredictedRandom.Create(_seed)
            };
        }

        protected override FallingSandState Interpolate(FallingSandState from, FallingSandState to, float t)
        {
            return to;
        }

        protected override void UpdateView(FallingSandState viewState, FallingSandState? verified)
        {
            using (RenderMarker.Auto())
            {
                var view = viewState.grid;
                var count = view.Count;
                var countm1 = count - 1;
                for (int i = 0; i < count; ++i)
                {
                    var tile = view[i];
                    if (tile.color.HasValue)
                    {
                        _colors[countm1 - i] = new Color32(
                            tile.color.Value.R,
                            tile.color.Value.G,
                            tile.color.Value.B, 255);
                    }
                    else
                    {
                        _colors[countm1 - i] = Color.white;
                    }
                }
                _tex.SetPixelData(_colors, 0);
                _tex.Apply(false);
            }
        }
    }
}
