using System;
using System.Collections.Generic;
using PurrNet.Pooling;
using PurrNet.Prediction.Profiler;
using UnityEditor;
using UnityEngine;

namespace PurrNet.Prediction.Editor
{
    // this is a unity editor window
    public class PurrdictionProfiler : EditorWindow
    {
        const int MAX_SAMPLES = 1024;

        [MenuItem("Tools/PurrDiction/Analysis/Profiler", false, -100)]
        public static void ShowWindow()
        {
            var window = GetWindow<PurrdictionProfiler>("PurrDiction Profiler");
            var purrnetLogo = Resources.Load("prediction") as Texture2D;
            window.titleContent = new GUIContent("PurrDiction Profiler", purrnetLogo, "PurrDiction Profiler");
            window.Show();
        }

        private readonly List<DeltaTickSample> _samples = new (MAX_SAMPLES);

        private int _selectedSampleIndex = -1;
        private Vector2 _detailsScrollPosition;
        private bool _paused;
        private bool _autoScrollToLatest = true;
        private bool _pendingAutoScroll;
        private Vector2 _graphScroll;
        private float _barWidth = 8f;
        private readonly float _barSpacing = 1f;

        private bool _showWroteStates = true;
        private bool _showReadStates = true;
        private bool _showWroteInputs = true;
        private bool _showReadInputs = true;

        private string _searchText = string.Empty;
		private bool _groupByParentType = true;
		private bool _sortByBitsDescending = true;

        private static readonly Color ColorWroteStates = new Color(0.30f, 0.80f, 0.35f, 1f);
        private static readonly Color ColorReadStates = new Color(0.25f, 0.55f, 0.90f, 1f);
        private static readonly Color ColorWroteInputs = new Color(0.95f, 0.60f, 0.20f, 1f);
        private static readonly Color ColorReadInputs = new Color(0.80f, 0.30f, 0.80f, 1f);
        private readonly Dictionary<string, bool> _foldoutStates = new Dictionary<string, bool>();

        private void OnEnable()
        {
            TickBandwidthProfiler.onTickEnded += GatherSamples;
        }

        private void OnDisable()
        {
            TickBandwidthProfiler.onTickEnded -= GatherSamples;
        }

        private void OnDestroy()
        {
            for (var i = 0; i < _samples.Count; i++)
                _samples[i].Dispose();
            _samples.Clear();
        }

        private void GatherSamples()
        {
            if (_paused)
                return;

            var sample = DeltaTickSample.CollectFromProfiler();
            _samples.Add(sample);

            if (_samples.Count > MAX_SAMPLES)
            {
                _samples[0].Dispose();
                _samples.RemoveAt(0);
                if (_selectedSampleIndex > 0)
                    _selectedSampleIndex--;
            }

            if (_autoScrollToLatest)
            {
                _selectedSampleIndex = _samples.Count - 1;
                _pendingAutoScroll = true;
            }

            Repaint();
        }

        void OnGUI()
        {
            DrawToolbar();
            DrawLegend();
            DrawGraph();
            DrawDetailsPanel();
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            if (GUILayout.Toggle(!_paused, _paused ? "Resume" : "Capturing", EditorStyles.toolbarButton) != !_paused)
            {
                _paused = !_paused;
            }

            if (GUILayout.Button("Clear", EditorStyles.toolbarButton))
            {
                ClearSamples();
            }

            GUILayout.Space(8f);
            _autoScrollToLatest = GUILayout.Toggle(_autoScrollToLatest, "Auto-select latest", EditorStyles.toolbarButton);

            GUILayout.FlexibleSpace();
            if (GUILayout.Button("-", EditorStyles.toolbarButton, GUILayout.Width(20)))
                _barWidth = Mathf.Clamp(_barWidth - 1f, 3f, 30f);
            if (GUILayout.Button("+", EditorStyles.toolbarButton, GUILayout.Width(20)))
                _barWidth = Mathf.Clamp(_barWidth + 1f, 3f, 30f);

            GUILayout.Label("Search:", GUILayout.Width(50));
            _searchText = GUILayout.TextField(_searchText, EditorStyles.toolbarTextField, GUILayout.MinWidth(120));
            EditorGUILayout.EndHorizontal();
        }

        private void ClearSamples()
        {
            for (var i = 0; i < _samples.Count; i++)
                _samples[i].Dispose();
            _samples.Clear();
            _selectedSampleIndex = -1;
        }

        private void DrawLegend()
        {
            EditorGUILayout.BeginHorizontal();
            _showWroteStates = DrawLegendToggle(_showWroteStates, ColorWroteStates, "Wrote States");
            _showReadStates = DrawLegendToggle(_showReadStates, ColorReadStates, "Read States");
            _showWroteInputs = DrawLegendToggle(_showWroteInputs, ColorWroteInputs, "Wrote Inputs");
            _showReadInputs = DrawLegendToggle(_showReadInputs, ColorReadInputs, "Read Inputs");
            EditorGUILayout.EndHorizontal();
        }

        private bool DrawLegendToggle(bool value, Color color, string label)
        {
            var rect = EditorGUILayout.GetControlRect(false, 18f, GUILayout.Width(18f));
            EditorGUI.DrawRect(rect, color);
            GUILayout.Space(4);
            var newVal = GUILayout.Toggle(value, label, GUILayout.ExpandWidth(false));
            GUILayout.Space(12);
            return newVal;
        }

        private void DrawGraph()
        {
            var graphHeight = 150f;
            var outerRect = GUILayoutUtility.GetRect(10, 10000, graphHeight, graphHeight);
            EditorGUI.DrawRect(outerRect, new Color(0.12f, 0.12f, 0.12f, 1f));

            int sampleCount = _samples.Count;
            if (sampleCount == 0)
                return;

            // Determine max total for scaling
            int maxBits = 0;
            for (var i = 0; i < sampleCount; i++)
            {
                var totals = GetSampleTotals(_samples[i]);
                int displayedTotal = 0;
                if (_showWroteStates) displayedTotal += totals.wroteStates;
                if (_showReadStates) displayedTotal += totals.readStates;
                if (_showWroteInputs) displayedTotal += totals.wroteInputs;
                if (_showReadInputs) displayedTotal += totals.readInputs;
                if (displayedTotal > maxBits) maxBits = displayedTotal;
            }
            if (maxBits <= 0) maxBits = 1;

            float contentWidth = sampleCount * (_barWidth) + Mathf.Max(0, sampleCount - 1) * _barSpacing;
            if (_pendingAutoScroll && Event.current.type == EventType.Repaint)
            {
                _graphScroll.x = ComputeScrollForIndex(Mathf.Max(_selectedSampleIndex, sampleCount - 1), outerRect.width, contentWidth);
                _pendingAutoScroll = false;
            }

            var contentRect = new Rect(0, 0, Mathf.Max(outerRect.width, contentWidth), Mathf.Max(1f, outerRect.height - 1f));
            _graphScroll.y = 0f; // prevent any vertical scrolling
            _graphScroll = GUI.BeginScrollView(outerRect, _graphScroll, contentRect, GUI.skin.horizontalScrollbar, GUIStyle.none);
            _graphScroll.y = 0f; // enforce vertical lock even if mouse wheel moves

            // Background inside content
            EditorGUI.DrawRect(new Rect(0, 0, contentRect.width, contentRect.height), new Color(0.10f, 0.10f, 0.10f, 1f));

            // Draw bars in local space
            for (var i = 0; i < sampleCount; i++)
            {
                var totals = GetSampleTotals(_samples[i]);
                float x = i * (_barWidth + _barSpacing);
                float y = contentRect.height;

                void DrawStack(int value, Color color)
                {
                    if (value <= 0) return;
                    float h = (value / (float)maxBits) * contentRect.height;
                    var r = new Rect(x, y - h, _barWidth, h);
                    EditorGUI.DrawRect(r, color);
                    y -= h;
                }

                if (_showReadInputs) DrawStack(totals.readInputs, ColorReadInputs);
                if (_showWroteInputs) DrawStack(totals.wroteInputs, ColorWroteInputs);
                if (_showReadStates) DrawStack(totals.readStates, ColorReadStates);
                if (_showWroteStates) DrawStack(totals.wroteStates, ColorWroteStates);

                // Selection highlight
                if (i == _selectedSampleIndex)
                {
                    var sel = new Rect(x, 0, _barWidth, contentRect.height);
                    EditorGUI.DrawRect(sel, new Color(1f, 1f, 1f, 0.08f));
                }

                // Handle click
                var clickRect = new Rect(x, 0, _barWidth, contentRect.height);
                if (Event.current.type == EventType.MouseDown && clickRect.Contains(Event.current.mousePosition))
                {
                    _selectedSampleIndex = i;
                    Repaint();
                }
            }

            // Axis label
            var label = new GUIContent($"Max: {maxBits} bits");
            var size = GUI.skin.label.CalcSize(label);
            GUI.Label(new Rect(4, 4, size.x, size.y), label);

            // Convert vertical wheel to horizontal pan when hovering the graph
            if (Event.current.type == EventType.ScrollWheel && outerRect.Contains(Event.current.mousePosition))
            {
                float delta = Event.current.delta.y * 10f;
                _graphScroll.x = Mathf.Clamp(_graphScroll.x + delta, 0f, Mathf.Max(0f, contentWidth - outerRect.width));
                Event.current.Use();
            }

            GUI.EndScrollView();
        }

        private float ComputeScrollForIndex(int index, float viewWidth, float contentWidth)
        {
            if (index < 0) return _graphScroll.x;
            float sampleX = index * (_barWidth + _barSpacing);
            float target = sampleX - (viewWidth - _barWidth);
            return Mathf.Clamp(target, 0f, Mathf.Max(0f, contentWidth - viewWidth));
        }

        private (int wroteStates, int readStates, int wroteInputs, int readInputs) GetSampleTotals(DeltaTickSample sample)
        {
            int Sum(DisposableList<PackingInfo> list)
            {
                int total = 0;
                for (var i = 0; i < list.Count; i++) total += list[i].bitCount;
                return total;
            }
            return (
                Sum(sample.wroteStates),
                Sum(sample.readStates),
                Sum(sample.wroteInputs),
                Sum(sample.readInputs)
            );
        }

        private void DrawDetailsPanel()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Details", EditorStyles.boldLabel);

			// View options
			EditorGUILayout.BeginHorizontal();
			_groupByParentType = GUILayout.Toggle(_groupByParentType, "Group by Parent", GUILayout.Width(140));
			_sortByBitsDescending = GUILayout.Toggle(_sortByBitsDescending, "Sort by bits (desc)", GUILayout.Width(160));
			GUILayout.FlexibleSpace();
			EditorGUILayout.EndHorizontal();

            if (_selectedSampleIndex < 0 || _selectedSampleIndex >= _samples.Count)
            {
                EditorGUILayout.HelpBox("Select a sample in the graph to see details.", MessageType.Info);
                return;
            }

            var sample = _samples[_selectedSampleIndex];
            var totals = GetSampleTotals(sample);
            EditorGUILayout.LabelField($"Sample {_selectedSampleIndex} — Total bits: {totals.wroteStates + totals.readStates + totals.wroteInputs + totals.readInputs}");

            _detailsScrollPosition = EditorGUILayout.BeginScrollView(_detailsScrollPosition, GUILayout.ExpandHeight(true));

            if (_showSectionHeaderAndList("Wrote States", ColorWroteStates, sample.wroteStates, _showWroteStates))
                DrawPackingList(sample.wroteStates);
            if (_showSectionHeaderAndList("Read States", ColorReadStates, sample.readStates, _showReadStates))
                DrawPackingList(sample.readStates);
            if (_showSectionHeaderAndList("Wrote Inputs", ColorWroteInputs, sample.wroteInputs, _showWroteInputs))
                DrawPackingList(sample.wroteInputs);
            if (_showSectionHeaderAndList("Read Inputs", ColorReadInputs, sample.readInputs, _showReadInputs))
                DrawPackingList(sample.readInputs);

            EditorGUILayout.EndScrollView();
        }

        private bool _showSectionHeaderAndList(string label, Color color, DisposableList<PackingInfo> list, bool enabled)
        {
            if (!enabled)
                return false;

            int totalBits = 0;
            for (var i = 0; i < list.Count; i++) totalBits += list[i].bitCount;

            var rect = EditorGUILayout.GetControlRect(false, 20f);
            var colorRect = new Rect(rect.x, rect.y + 3f, 14f, rect.height - 6f);
            EditorGUI.DrawRect(colorRect, color);
            var foldoutRect = new Rect(rect.x + 18f, rect.y, rect.width - 18f, rect.height);
            var currentState = _foldoutStates.GetValueOrDefault(label, true);
            currentState = EditorGUI.Foldout(foldoutRect, currentState, $"{label} (Count: {list.Count}, Bits: {totalBits})", true);
            _foldoutStates[label] = currentState;
            return currentState && list.Count > 0;
        }

		private void DrawPackingList(DisposableList<PackingInfo> list)
        {
			const float BitsColWidth = 70f;
			const float ReferenceColWidth = 240f;
			const float PingColWidth = 44f;
			float rowHeight = EditorGUIUtility.singleLineHeight + 4f;

			// Prepare filtered items
			var filtered = new List<PackingInfo>(Mathf.Max(16, list.Count));
			for (var i = 0; i < list.Count; i++)
			{
				var info = list[i];
				if (!string.IsNullOrEmpty(_searchText))
				{
					var typeName = info.parent != null ? info.parent.Name : "<null>";
					var refName = info.reference ? info.reference.name : string.Empty;
					if (!typeName.Contains(_searchText, StringComparison.OrdinalIgnoreCase) &&
						!refName.Contains(_searchText, StringComparison.OrdinalIgnoreCase))
						continue;
				}
				filtered.Add(info);
			}

			if (!_groupByParentType)
			{
				// Flat list with optional sorting
				if (_sortByBitsDescending)
					filtered.Sort((a, b) => b.bitCount.CompareTo(a.bitCount));

				var headerRect = EditorGUILayout.GetControlRect(false, rowHeight);
				EditorGUI.DrawRect(headerRect, new Color(0.18f, 0.18f, 0.18f, 1f));
				EditorGUI.DrawRect(new Rect(headerRect.x, headerRect.yMax - 1f, headerRect.width, 1f), new Color(0f, 0f, 0f, 0.35f));

				float parentColWidth = Mathf.Max(80f, headerRect.width - BitsColWidth - ReferenceColWidth - PingColWidth);
				float x = headerRect.x;
				GUI.Label(new Rect(x + 6f, headerRect.y + 2f, BitsColWidth - 8f, rowHeight - 4f), "Bits", EditorStyles.boldLabel);
				x += BitsColWidth;
				GUI.Label(new Rect(x + 6f, headerRect.y + 2f, parentColWidth - 8f, rowHeight - 4f), "Parent", EditorStyles.boldLabel);
				x = headerRect.x + headerRect.width - (ReferenceColWidth + PingColWidth);
				GUI.Label(new Rect(x + 6f, headerRect.y + 2f, ReferenceColWidth - 8f, rowHeight - 4f), "Reference", EditorStyles.boldLabel);

				int visibleRowIndex = 0;
				for (var i = 0; i < filtered.Count; i++)
				{
					var info = filtered[i];

					var rowRect = EditorGUILayout.GetControlRect(false, rowHeight);
					if ((visibleRowIndex & 1) == 0)
						EditorGUI.DrawRect(rowRect, new Color(1f, 1f, 1f, 0.035f));

					float rowParentWidth = Mathf.Max(80f, rowRect.width - BitsColWidth - ReferenceColWidth - PingColWidth);
					float xBits = rowRect.x;
					float xParent = xBits + BitsColWidth;
					float xRef = rowRect.x + rowRect.width - (ReferenceColWidth + PingColWidth);

					EditorGUI.DrawRect(new Rect(xParent, rowRect.y, 1f, rowHeight), new Color(0f, 0f, 0f, 0.2f));
					EditorGUI.DrawRect(new Rect(xRef, rowRect.y, 1f, rowHeight), new Color(0f, 0f, 0f, 0.2f));

					GUI.Label(new Rect(xBits + 6f, rowRect.y + 2f, BitsColWidth - 8f, rowHeight - 4f), info.bitCount.ToString());
					GUI.Label(new Rect(xParent + 6f, rowRect.y + 2f, rowParentWidth - 8f, rowHeight - 4f), info.parent != null ? info.parent.Name : "<null>");

					EditorGUI.BeginDisabledGroup(true);
					var obj = info.reference;
					EditorGUI.ObjectField(new Rect(xRef + 6f, rowRect.y + 2f, ReferenceColWidth - 8f, rowHeight - 4f), obj, typeof(UnityEngine.Object), true);
					EditorGUI.EndDisabledGroup();

					if (obj)
					{
						if (GUI.Button(new Rect(rowRect.x + rowRect.width - PingColWidth + 2f, rowRect.y + 2f, PingColWidth - 4f, rowHeight - 4f), "Ping"))
							EditorGUIUtility.PingObject(obj);
					}

					visibleRowIndex++;
				}
				return;
			}

			// Grouped by parent type
			var groups = new List<GroupRow>(16);
			for (var i = 0; i < filtered.Count; i++)
			{
				var info = filtered[i];
				string key = info.parent != null ? info.parent.Name : "<null>";
				int foundIndex = -1;
				for (var g = 0; g < groups.Count; g++)
					if (groups[g].key == key) { foundIndex = g; break; }
				if (foundIndex == -1)
				{
					var gr = new GroupRow { key = key, totalBits = 0 };
					gr.items = new List<PackingInfo>(8);
					groups.Add(gr);
					foundIndex = groups.Count - 1;
				}
				groups[foundIndex].items.Add(info);
				var group = groups[foundIndex];
				group.totalBits += info.bitCount;
				groups[foundIndex] = group;
			}

			if (_sortByBitsDescending)
				groups.Sort((a, b) => b.totalBits.CompareTo(a.totalBits));

			for (var g = 0; g < groups.Count; g++)
			{
				var group = groups[g];
				var groupKey = "Group/" + group.key;
				var foldRect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight + 6f);
				EditorGUI.DrawRect(foldRect, new Color(0.16f, 0.16f, 0.16f, 1f));
				var state = _foldoutStates.GetValueOrDefault(groupKey, true);
				state = EditorGUI.Foldout(new Rect(foldRect.x + 4f, foldRect.y + 2f, foldRect.width - 8f, foldRect.height - 4f), state, group.key + $"  (Count: {group.items.Count}, Bits: {group.totalBits})", true);
				_foldoutStates[groupKey] = state;
				if (!state)
					continue;

				// Header inside group
				var headerRect = EditorGUILayout.GetControlRect(false, rowHeight);
				EditorGUI.DrawRect(headerRect, new Color(0.18f, 0.18f, 0.18f, 1f));
				EditorGUI.DrawRect(new Rect(headerRect.x, headerRect.yMax - 1f, headerRect.width, 1f), new Color(0f, 0f, 0f, 0.35f));
				float parentColWidth = Mathf.Max(80f, headerRect.width - BitsColWidth - ReferenceColWidth - PingColWidth);
				float x = headerRect.x;
				GUI.Label(new Rect(x + 6f, headerRect.y + 2f, BitsColWidth - 8f, rowHeight - 4f), "Bits", EditorStyles.boldLabel);
				x += BitsColWidth;
				GUI.Label(new Rect(x + 6f, headerRect.y + 2f, parentColWidth - 8f, rowHeight - 4f), "Parent", EditorStyles.boldLabel);
				x = headerRect.x + headerRect.width - (ReferenceColWidth + PingColWidth);
				GUI.Label(new Rect(x + 6f, headerRect.y + 2f, ReferenceColWidth - 8f, rowHeight - 4f), "Reference", EditorStyles.boldLabel);

				if (_sortByBitsDescending)
					group.items.Sort((a, b) => b.bitCount.CompareTo(a.bitCount));

				int visibleRowIndex = 0;
				for (var i = 0; i < group.items.Count; i++)
				{
					var info = group.items[i];
					var rowRect = EditorGUILayout.GetControlRect(false, rowHeight);
					if ((visibleRowIndex & 1) == 0)
						EditorGUI.DrawRect(rowRect, new Color(1f, 1f, 1f, 0.035f));

					float rowParentWidth = Mathf.Max(80f, rowRect.width - BitsColWidth - ReferenceColWidth - PingColWidth);
					float xBits = rowRect.x;
					float xParent = xBits + BitsColWidth;
					float xRef = rowRect.x + rowRect.width - (ReferenceColWidth + PingColWidth);

					EditorGUI.DrawRect(new Rect(xParent, rowRect.y, 1f, rowHeight), new Color(0f, 0f, 0f, 0.2f));
					EditorGUI.DrawRect(new Rect(xRef, rowRect.y, 1f, rowHeight), new Color(0f, 0f, 0f, 0.2f));

					GUI.Label(new Rect(xBits + 6f, rowRect.y + 2f, BitsColWidth - 8f, rowHeight - 4f), info.bitCount.ToString());
					GUI.Label(new Rect(xParent + 6f, rowRect.y + 2f, rowParentWidth - 8f, rowHeight - 4f), info.parent != null ? info.parent.Name : "<null>");

					EditorGUI.BeginDisabledGroup(true);
					var obj = info.reference;
					EditorGUI.ObjectField(new Rect(xRef + 6f, rowRect.y + 2f, ReferenceColWidth - 8f, rowHeight - 4f), obj, typeof(UnityEngine.Object), true);
					EditorGUI.EndDisabledGroup();

					if (obj)
					{
						if (GUI.Button(new Rect(rowRect.x + rowRect.width - PingColWidth + 2f, rowRect.y + 2f, PingColWidth - 4f, rowHeight - 4f), "Ping"))
							EditorGUIUtility.PingObject(obj);
					}

					visibleRowIndex++;
				}
			}
		}

		private struct GroupRow
		{
			public string key;
			public int totalBits;
			public List<PackingInfo> items;
		}
    }
}
