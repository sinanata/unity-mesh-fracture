using UnityEngine;
using UnityEditor;
using UnityEditorInternal;
using UnityEditorInternal.Profiling;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Linq;

namespace LeapOfLegends.Editor
{
    /// <summary>
    /// Editor tool to analyze Unity Profiler data files and export spike information.
    /// Captures full hierarchical profiler data including nested script calls.
    /// </summary>
    public class ProfilerDataAnalyzer : EditorWindow
    {
        private string profilerDataPath = @"D:\UnityProjects\LeapofLegends\ProfilerCaptures";
        private int startFrame = 0;
        private int endFrame = 300;
        private float spikeThresholdMs = 10f;
        private float sampleThresholdMs = 0.5f;
        private int maxHierarchyDepth = 8;
        private Vector2 scrollPos;
        private List<FrameAnalysis> analyzedFrames = new List<FrameAnalysis>();
        private bool isAnalyzing = false;
        private string statusMessage = "";
        private bool showHierarchy = true;

        [MenuItem("Leap of Legends/Tools/Profiler Data Analyzer")]
        public static void ShowWindow()
        {
            var window = GetWindow<ProfilerDataAnalyzer>("Profiler Analyzer");
            window.minSize = new Vector2(600, 400);
        }

        private void OnGUI()
        {
            try
            {
                DrawGUI();
            }
            catch (ExitGUIException)
            {
                throw;
            }
            catch (System.Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        private void DrawGUI()
        {
            // Ensure analyzedFrames is never null
            if (analyzedFrames == null)
                analyzedFrames = new List<FrameAnalysis>();

            EditorGUILayout.LabelField("Unity Profiler Data Analyzer", EditorStyles.boldLabel);
            EditorGUILayout.Space(10);

            // File path
            EditorGUILayout.BeginHorizontal();
            profilerDataPath = EditorGUILayout.TextField("Profiler Data File", profilerDataPath);
            if (GUILayout.Button("Browse", GUILayout.Width(60)))
            {
                string path = EditorUtility.OpenFilePanel("Select Profiler Data", "ProfilerCaptures", "data");
                if (!string.IsNullOrEmpty(path))
                    profilerDataPath = path;
            }
            EditorGUILayout.EndHorizontal();

            // Frame range
            EditorGUILayout.BeginHorizontal();
            startFrame = EditorGUILayout.IntField("Start Frame", startFrame);
            endFrame = EditorGUILayout.IntField("End Frame", endFrame);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            spikeThresholdMs = EditorGUILayout.FloatField("Spike Threshold (ms)", spikeThresholdMs);
            sampleThresholdMs = EditorGUILayout.FloatField("Sample Threshold (ms)", sampleThresholdMs);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            maxHierarchyDepth = EditorGUILayout.IntSlider("Max Hierarchy Depth", maxHierarchyDepth, 3, 15);
            showHierarchy = EditorGUILayout.Toggle("Show Hierarchy", showHierarchy);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(10);

            // Actions
            EditorGUILayout.BeginHorizontal();

            bool canAnalyze = !isAnalyzing && !string.IsNullOrEmpty(profilerDataPath);
            EditorGUI.BeginDisabledGroup(!canAnalyze);
            if (GUILayout.Button("Load & Analyze", GUILayout.Height(30)))
            {
                LoadAndAnalyze();
            }
            EditorGUI.EndDisabledGroup();

            if (GUILayout.Button("Open in Profiler", GUILayout.Height(30)))
            {
                OpenInProfiler();
            }

            bool hasFrames = analyzedFrames != null && analyzedFrames.Count > 0;
            EditorGUI.BeginDisabledGroup(!hasFrames);
            if (GUILayout.Button("Export Report", GUILayout.Height(30)))
            {
                ExportReport();
            }
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(statusMessage))
            {
                EditorGUILayout.HelpBox(statusMessage, MessageType.Info);
            }

            EditorGUILayout.Space(10);

            // Results
            if (hasFrames)
            {
                EditorGUILayout.LabelField($"Analyzed {analyzedFrames.Count} frames", EditorStyles.boldLabel);

                var spikes = analyzedFrames.Where(f => f != null && f.totalMs > spikeThresholdMs).OrderByDescending(f => f.totalMs).ToList();
                EditorGUILayout.LabelField($"Frames above {spikeThresholdMs}ms: {spikes.Count}", EditorStyles.boldLabel);

                EditorGUILayout.Space(5);

                scrollPos = EditorGUILayout.BeginScrollView(scrollPos);

                // Show top spikes
                EditorGUILayout.LabelField("Top Spikes:", EditorStyles.boldLabel);
                foreach (var frame in spikes.Take(15))
                {
                    if (frame == null) continue;

                    EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                    EditorGUILayout.LabelField($"Frame {frame.frameIndex}: {frame.totalMs:F2}ms", EditorStyles.boldLabel);

                    if (showHierarchy && frame.rootSamples != null && frame.rootSamples.Count > 0)
                    {
                        // Show hierarchical view
                        var significantRoots = frame.rootSamples
                            .Where(s => s.timeMs >= sampleThresholdMs)
                            .OrderByDescending(s => s.timeMs)
                            .Take(5);

                        foreach (var root in significantRoots)
                        {
                            DrawHierarchicalSample(root, 0, 4);
                        }
                    }
                    else if (frame.topSamples != null && frame.topSamples.Count > 0)
                    {
                        EditorGUI.indentLevel++;
                        foreach (var sample in frame.topSamples.Take(10))
                        {
                            if (sample == null) continue;
                            EditorGUILayout.LabelField($"{sample.name}: {sample.timeMs:F2}ms ({sample.callCount} calls)");
                        }
                        EditorGUI.indentLevel--;
                    }
                    EditorGUILayout.EndVertical();
                }

                EditorGUILayout.EndScrollView();
            }
        }

        private void OpenInProfiler()
        {
            if (!File.Exists(profilerDataPath))
            {
                statusMessage = $"File not found: {profilerDataPath}";
                return;
            }

            // Open Profiler window
            var profilerWindow = EditorWindow.GetWindow<ProfilerWindow>();
            profilerWindow.Show();

            // Load the data file
            ProfilerDriver.LoadProfile(profilerDataPath, false);

            statusMessage = $"Loaded in Profiler. Navigate to frame {startFrame}-{endFrame} to see spikes.";
        }

        private void DrawHierarchicalSample(HierarchicalSample sample, int currentDepth, int maxDepth)
        {
            if (currentDepth > maxDepth || sample.timeMs < sampleThresholdMs)
                return;

            // Save and restore indent level properly
            int previousIndent = EditorGUI.indentLevel;
            EditorGUI.indentLevel = currentDepth + 1;

            try
            {
                // Color code by time
                var originalColor = GUI.color;
                if (sample.timeMs > 50f)
                    GUI.color = new Color(1f, 0.4f, 0.4f);
                else if (sample.timeMs > 10f)
                    GUI.color = new Color(1f, 0.7f, 0.3f);
                else if (sample.timeMs > 5f)
                    GUI.color = new Color(1f, 1f, 0.5f);

                EditorGUILayout.LabelField(sample.GetDisplayString());
                GUI.color = originalColor;

                // Draw significant children
                var significantChildren = sample.children
                    .Where(c => c.timeMs >= sampleThresholdMs)
                    .OrderByDescending(c => c.timeMs)
                    .Take(5);

                foreach (var child in significantChildren)
                {
                    DrawHierarchicalSample(child, currentDepth + 1, maxDepth);
                }
            }
            finally
            {
                EditorGUI.indentLevel = previousIndent;
            }
        }

        private void LoadAndAnalyze()
        {
            if (!File.Exists(profilerDataPath))
            {
                statusMessage = $"File not found: {profilerDataPath}";
                return;
            }

            isAnalyzing = true;
            analyzedFrames.Clear();
            statusMessage = "Loading profiler data...";
            Repaint();

            try
            {
                // Load the profiler data
                ProfilerDriver.LoadProfile(profilerDataPath, false);

                int firstFrame = ProfilerDriver.firstFrameIndex;
                int lastFrame = ProfilerDriver.lastFrameIndex;

                statusMessage = $"Loaded. First frame: {firstFrame}, Last frame: {lastFrame}. Analyzing...";
                Repaint();

                // Clamp to available range
                int analyzeStart = Mathf.Max(startFrame, firstFrame);
                int analyzeEnd = Mathf.Min(endFrame, lastFrame);

                for (int frameIdx = analyzeStart; frameIdx <= analyzeEnd; frameIdx++)
                {
                    var frameAnalysis = AnalyzeFrame(frameIdx);
                    if (frameAnalysis != null)
                    {
                        analyzedFrames.Add(frameAnalysis);
                    }

                    if (frameIdx % 50 == 0)
                    {
                        statusMessage = $"Analyzing frame {frameIdx}/{analyzeEnd}...";
                        Repaint();
                    }
                }

                var spikeCount = analyzedFrames.Count(f => f.totalMs > spikeThresholdMs);
                var maxSpike = analyzedFrames.Count > 0 ? analyzedFrames.Max(f => f.totalMs) : 0;
                statusMessage = $"Analysis complete. {analyzedFrames.Count} frames analyzed. {spikeCount} spikes detected. Max: {maxSpike:F2}ms";
            }
            catch (System.Exception ex)
            {
                statusMessage = $"Error: {ex.Message}";
                Debug.LogException(ex);
            }

            isAnalyzing = false;
            Repaint();
        }

        private FrameAnalysis AnalyzeFrame(int frameIndex)
        {
            var analysis = new FrameAnalysis
            {
                frameIndex = frameIndex,
                topSamples = new List<SampleInfo>(),
                rootSamples = new List<HierarchicalSample>(),
                threads = new List<ThreadData>()
            };

            try
            {
                // Analyze available threads by probing (API doesn't provide thread count directly)
                for (int threadIdx = 0; threadIdx < 8; threadIdx++)
                {
                    var threadData = AnalyzeThread(frameIndex, threadIdx);
                    if (threadData == null)
                        break; // No more valid threads

                    if (threadData.rootSamples.Count > 0)
                    {
                        analysis.threads.Add(threadData);

                        // Main thread (index 0) provides the primary data
                        if (threadIdx == 0)
                        {
                            analysis.totalMs = threadData.totalTimeMs;
                            analysis.rootSamples = threadData.rootSamples;
                        }
                    }
                }

                // Calculate total GC and build flat sample list from main thread
                using (var frameData = ProfilerDriver.GetRawFrameDataView(frameIndex, 0))
                {
                    if (!frameData.valid)
                        return null;

                    if (analysis.totalMs == 0)
                        analysis.totalMs = frameData.frameTimeMs;

                    int sampleCount = frameData.sampleCount;
                    if (sampleCount == 0)
                        return analysis;

                    // Build hierarchy using children count to traverse the tree
                    var allSamples = new List<HierarchicalSample>(sampleCount);
                    var parentStack = new Stack<(int sampleIdx, int remainingChildren)>();

                    for (int i = 0; i < sampleCount; i++)
                    {
                        string name = frameData.GetSampleName(i);
                        float timeMs = frameData.GetSampleTimeMs(i);
                        int childCount = frameData.GetSampleChildrenCount(i);

                        // Pop completed parents
                        while (parentStack.Count > 0 && parentStack.Peek().remainingChildren <= 0)
                            parentStack.Pop();

                        var sample = new HierarchicalSample
                        {
                            name = name,
                            timeMs = timeMs,
                            depth = parentStack.Count,
                            sampleIndex = i,
                            gcAllocBytes = 0,
                            children = new List<HierarchicalSample>()
                        };

                        if (parentStack.Count > 0)
                        {
                            int parentIdx = parentStack.Peek().sampleIdx;
                            if (parentIdx < allSamples.Count)
                            {
                                sample.parent = allSamples[parentIdx];
                                allSamples[parentIdx].children.Add(sample);
                            }
                            // Decrement parent's remaining children
                            var parent = parentStack.Pop();
                            parentStack.Push((parent.sampleIdx, parent.remainingChildren - 1));
                        }
                        else
                        {
                            analysis.rootSamples.Add(sample);
                        }

                        allSamples.Add(sample);

                        // If this sample has children, push it to track
                        if (childCount > 0)
                            parentStack.Push((i, childCount));
                    }

                    // Build flat list for legacy compatibility (aggregated by name)
                    var sampleDict = new Dictionary<string, SampleInfo>();
                    foreach (var sample in allSamples)
                    {
                        if (sampleDict.TryGetValue(sample.name, out var existing))
                        {
                            existing.timeMs += sample.timeMs;
                            existing.callCount++;
                        }
                        else
                        {
                            sampleDict[sample.name] = new SampleInfo
                            {
                                name = sample.name,
                                timeMs = sample.timeMs,
                                callCount = 1
                            };
                        }
                    }

                    analysis.topSamples = sampleDict.Values
                        .OrderByDescending(s => s.timeMs)
                        .Take(20)
                        .ToList();
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"Error analyzing frame {frameIndex}: {ex.Message}");
            }

            return analysis;
        }

        private ThreadData AnalyzeThread(int frameIndex, int threadIndex)
        {
            try
            {
                using (var frameData = ProfilerDriver.GetRawFrameDataView(frameIndex, threadIndex))
                {
                    if (!frameData.valid || frameData.sampleCount == 0)
                        return null;

                    var threadData = new ThreadData
                    {
                        threadIndex = threadIndex,
                        threadName = frameData.threadName ?? $"Thread {threadIndex}",
                        totalTimeMs = frameData.frameTimeMs,
                        rootSamples = new List<HierarchicalSample>()
                    };

                    var allSamples = new List<HierarchicalSample>(frameData.sampleCount);
                    var parentStack = new Stack<(int sampleIdx, int remainingChildren)>();

                    for (int i = 0; i < frameData.sampleCount; i++)
                    {
                        string name = frameData.GetSampleName(i);
                        float timeMs = frameData.GetSampleTimeMs(i);
                        int childCount = frameData.GetSampleChildrenCount(i);

                        // Pop completed parents
                        while (parentStack.Count > 0 && parentStack.Peek().remainingChildren <= 0)
                            parentStack.Pop();

                        var sample = new HierarchicalSample
                        {
                            name = name,
                            timeMs = timeMs,
                            depth = parentStack.Count,
                            sampleIndex = i,
                            gcAllocBytes = 0,
                            children = new List<HierarchicalSample>()
                        };

                        if (parentStack.Count > 0)
                        {
                            int parentIdx = parentStack.Peek().sampleIdx;
                            if (parentIdx < allSamples.Count)
                            {
                                sample.parent = allSamples[parentIdx];
                                allSamples[parentIdx].children.Add(sample);
                            }
                            var parent = parentStack.Pop();
                            parentStack.Push((parent.sampleIdx, parent.remainingChildren - 1));
                        }
                        else
                        {
                            threadData.rootSamples.Add(sample);
                        }

                        allSamples.Add(sample);

                        if (childCount > 0)
                            parentStack.Push((i, childCount));
                    }

                    return threadData;
                }
            }
            catch
            {
                return null;
            }
        }

        private void CollectHierarchySamples(
            HierarchicalSample sample,
            StringBuilder sb,
            int currentDepth,
            float thresholdMs)
        {
            if (currentDepth > maxHierarchyDepth || sample.timeMs < thresholdMs)
                return;

            string indent = new string(' ', currentDepth * 4);
            string gcInfo = sample.gcAllocBytes > 0
                ? $" [GC: {(sample.gcAllocBytes >= 1024 ? $"{sample.gcAllocBytes / 1024f:F1}KB" : $"{sample.gcAllocBytes}B")}]"
                : "";
            sb.AppendLine($"{indent}{sample.name}: {sample.timeMs:F2}ms{gcInfo}");

            // Sort children by time and recurse
            var significantChildren = sample.children
                .Where(c => c.timeMs >= thresholdMs)
                .OrderByDescending(c => c.timeMs);

            foreach (var child in significantChildren)
            {
                CollectHierarchySamples(child, sb, currentDepth + 1, thresholdMs);
            }
        }

        private void ExportReport()
        {
            string exportPath = EditorUtility.SaveFilePanel(
                "Export Analysis Report",
                Path.GetDirectoryName(profilerDataPath),
                $"ProfilerAnalysis_{System.DateTime.Now:yyyy-MM-dd_HH-mm-ss}",
                "txt");

            if (string.IsNullOrEmpty(exportPath))
                return;

            var sb = new StringBuilder();
            sb.AppendLine("=== Unity Profiler Analysis Report ===");
            sb.AppendLine($"Source: {profilerDataPath}");
            sb.AppendLine($"Frame Range: {startFrame} - {endFrame}");
            sb.AppendLine($"Spike Threshold: {spikeThresholdMs}ms");
            sb.AppendLine($"Generated: {System.DateTime.Now}");
            sb.AppendLine();

            var spikes = analyzedFrames.Where(f => f.totalMs > spikeThresholdMs).OrderByDescending(f => f.totalMs).ToList();

            sb.AppendLine($"=== SUMMARY ===");
            sb.AppendLine($"Total Frames Analyzed: {analyzedFrames.Count}");
            sb.AppendLine($"Spike Frames (>{spikeThresholdMs}ms): {spikes.Count}");
            if (analyzedFrames.Count > 0)
            {
                sb.AppendLine($"Max Frame Time: {analyzedFrames.Max(f => f.totalMs):F2}ms");
                sb.AppendLine($"Avg Frame Time: {analyzedFrames.Average(f => f.totalMs):F2}ms");
                sb.AppendLine($"Min Frame Time: {analyzedFrames.Min(f => f.totalMs):F2}ms");

                // GC Allocation summary
                long totalGc = analyzedFrames.Sum(f => f.totalGcAlloc);
                if (totalGc > 0)
                {
                    string gcStr = totalGc >= 1024 * 1024
                        ? $"{totalGc / (1024f * 1024f):F2}MB"
                        : totalGc >= 1024
                            ? $"{totalGc / 1024f:F1}KB"
                            : $"{totalGc}B";
                    sb.AppendLine($"Total GC Allocations: {gcStr}");
                }

                // Thread summary
                var threadNames = analyzedFrames
                    .Where(f => f.threads != null)
                    .SelectMany(f => f.threads)
                    .Select(t => t.threadName)
                    .Distinct()
                    .Take(10);
                sb.AppendLine($"Threads Analyzed: {string.Join(", ", threadNames)}");
            }
            sb.AppendLine();

            sb.AppendLine("=== SPIKE FRAMES (sorted by time) ===");
            sb.AppendLine();

            foreach (var frame in spikes)
            {
                string gcStr = frame.totalGcAlloc > 0
                    ? $" [GC: {(frame.totalGcAlloc >= 1024 ? $"{frame.totalGcAlloc / 1024f:F1}KB" : $"{frame.totalGcAlloc}B")}]"
                    : "";
                sb.AppendLine($"--- Frame {frame.frameIndex}: {frame.totalMs:F2}ms{gcStr} ---");

                if (showHierarchy && frame.rootSamples != null && frame.rootSamples.Count > 0)
                {
                    // Export hierarchical view for main thread
                    sb.AppendLine("    [Main Thread]");
                    var significantRoots = frame.rootSamples
                        .Where(s => s.timeMs >= sampleThresholdMs)
                        .OrderByDescending(s => s.timeMs);

                    foreach (var root in significantRoots)
                    {
                        CollectHierarchySamples(root, sb, 2, sampleThresholdMs);
                    }

                    // Export other threads with significant time
                    if (frame.threads != null)
                    {
                        foreach (var thread in frame.threads.Where(t => t.threadIndex > 0 && t.totalTimeMs > sampleThresholdMs))
                        {
                            sb.AppendLine($"    [{thread.threadName}] {thread.totalTimeMs:F2}ms");
                            var threadRoots = thread.rootSamples
                                .Where(s => s.timeMs >= sampleThresholdMs)
                                .OrderByDescending(s => s.timeMs)
                                .Take(3);
                            foreach (var root in threadRoots)
                            {
                                CollectHierarchySamples(root, sb, 2, sampleThresholdMs);
                            }
                        }
                    }
                }
                else
                {
                    // Fallback to flat view
                    foreach (var sample in frame.topSamples.Take(15))
                    {
                        sb.AppendLine($"    {sample.name}: {sample.timeMs:F2}ms ({sample.callCount} calls)");
                    }
                }
                sb.AppendLine();
            }

            // Aggregate analysis - what functions appear most in spikes
            sb.AppendLine("=== MOST COMMON SPIKE CONTRIBUTORS ===");
            var aggregated = new Dictionary<string, (float totalTime, int frameCount)>();
            foreach (var frame in spikes)
            {
                foreach (var sample in frame.topSamples.Where(s => s.timeMs > 1f)) // Only samples > 1ms
                {
                    if (aggregated.TryGetValue(sample.name, out var existing))
                    {
                        aggregated[sample.name] = (existing.totalTime + sample.timeMs, existing.frameCount + 1);
                    }
                    else
                    {
                        aggregated[sample.name] = (sample.timeMs, 1);
                    }
                }
            }

            foreach (var kvp in aggregated.OrderByDescending(x => x.Value.totalTime).Take(30))
            {
                sb.AppendLine($"{kvp.Key}: {kvp.Value.totalTime:F2}ms total across {kvp.Value.frameCount} spike frames");
            }

            File.WriteAllText(exportPath, sb.ToString());
            statusMessage = $"Report exported to: {exportPath}";

            // Open the report
            EditorUtility.RevealInFinder(exportPath);
        }

        private class FrameAnalysis
        {
            public int frameIndex;
            public float totalMs;
            public long totalGcAlloc;
            public List<SampleInfo> topSamples;
            public List<HierarchicalSample> rootSamples;
            public List<ThreadData> threads;
        }

        private class SampleInfo
        {
            public string name;
            public float timeMs;
            public int callCount;
        }

        private class HierarchicalSample
        {
            public string name;
            public float timeMs;
            public int depth;
            public int sampleIndex;
            public long gcAllocBytes;
            public HierarchicalSample parent;
            public List<HierarchicalSample> children;

            public string GetFullPath()
            {
                if (parent == null)
                    return name;
                return $"{parent.GetFullPath()} > {name}";
            }

            public string GetDisplayString()
            {
                if (gcAllocBytes > 0)
                {
                    string allocStr = gcAllocBytes >= 1024
                        ? $"{gcAllocBytes / 1024f:F1}KB"
                        : $"{gcAllocBytes}B";
                    return $"{name}: {timeMs:F2}ms (GC: {allocStr})";
                }
                return $"{name}: {timeMs:F2}ms";
            }
        }

        private class ThreadData
        {
            public string threadName;
            public int threadIndex;
            public float totalTimeMs;
            public List<HierarchicalSample> rootSamples;
        }
    }
}
