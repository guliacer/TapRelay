using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace TapRelay
{
    /// <summary>
    /// Extracts positive prompt text from a ComfyUI execution prompt graph.
    /// </summary>
    internal static class ComfyUIPromptParser
    {
        private const int MaxPositiveLength = 4000;
        private const int MaxVisitedNodes = 128;

        private static readonly HashSet<string> ConditioningInputKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> PositiveOnlyInputKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> DiffusionInputKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> ExecutionInputKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> KnownSamplerClassTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> NonSamplerClassTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> ConditioningPathClassHints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> TextNodeClassHints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> TextInputKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static Action<string>? DebugLog = null;

        static ComfyUIPromptParser()
        {
            foreach (var k in new[] { "positive", "positive_conditioning", "conditioning", "negative", "negative_conditioning", "guider", "guide" })
                ConditioningInputKeys.Add(k);
            foreach (var k in new[] { "positive", "positive_conditioning" })
                PositiveOnlyInputKeys.Add(k);
            foreach (var k in new[] { "latent_image", "latent", "samples", "latent_samples", "noise", "sigmas" })
                DiffusionInputKeys.Add(k);
            foreach (var k in new[] { "model", "unet", "sampler", "steps", "cfg", "denoise", "seed", "scheduler", "start_step", "end_step", "return_with_leftover_noise" })
                ExecutionInputKeys.Add(k);
            foreach (var k in new[] { "KSampler", "KSamplerAdvanced", "SamplerCustom", "SamplerCustomAdvanced", "GGZImageSampler", "GG\u91C7\u6837\u5668" })
                KnownSamplerClassTypes.Add(k);
            foreach (var k in new[] { "KSamplerSelect", "SamplerSelect" })
                NonSamplerClassTypes.Add(k);
            foreach (var k in new[] { "Guider", "CLIP", "Text", "Encode", "Prompt", "Show", "Display", "Wildcard", "Style", "Conditioning", "Concat", "Edit", "Switch", "Selector", "If", "\u5F15\u5BFC", "\u6587\u672C", "\u63D0\u793A\u8BCD", "CR", "SDXLPromptStyler", "StringFunction", "Intermediate", "Looping" })
                ConditioningPathClassHints.Add(k);
            foreach (var k in new[] { "CLIP", "Text", "Encode", "Prompt", "Show", "Display", "Wildcard", "Style", "Conditioning" })
                TextNodeClassHints.Add(k);
            foreach (var k in new[] { "text", "prompt", "prompt_text", "\u6587\u672C", "string", "content", "value", "text1", "text2", "text_a", "text_b", "text_g", "text_l", "prompt_positive", "prompt_negative", "positive_prompt", "negative_prompt" })
                TextInputKeys.Add(k);
        }

        public static string ExtractKSamplerPositive(string graphJson)
        {
            if (string.IsNullOrWhiteSpace(graphJson)) return string.Empty;
            try { using var doc = JsonDocument.Parse(graphJson); return ExtractKSamplerPositive(doc.RootElement); }
            catch (JsonException) { return string.Empty; }
        }

        public static string ExtractBestPositive(string? promptGraphJson, string? workflowJson)
        {
            if (!string.IsNullOrWhiteSpace(workflowJson))
            {
                var apiGraph = ComfyUIWorkflowConverter.ConvertToApiGraph(workflowJson);
                if (!string.IsNullOrWhiteSpace(apiGraph))
                {
                    var result = ExtractKSamplerPositive(apiGraph);
                    if (!string.IsNullOrWhiteSpace(result) && !IsMetaPrompt(result)) return result;
                }
            }
            if (!string.IsNullOrWhiteSpace(promptGraphJson))
            {
                var result = ExtractKSamplerPositive(promptGraphJson);
                if (!string.IsNullOrWhiteSpace(result) && !IsMetaPrompt(result)) return result;
            }
            return string.Empty;
        }

        public static string ExtractFromPng(byte[] pngBytes)
        {
            var (promptJson, workflowJson) = ComfyUIPngChunkReader.ExtractComfyuiBlocks(pngBytes);
            return ExtractBestPositive(promptJson, workflowJson);
        }

        public static string ExtractKSamplerPositive(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object) return string.Empty;

            var textIndex = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var edgeIndex = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            var classTypeIndex = new Dictionary<string, string>(StringComparer.Ordinal);
            var inputKeysByNode = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

            foreach (var node in root.EnumerateObject())
            {
                if (node.Value.ValueKind != JsonValueKind.Object) continue;
                var nodeId = node.Name;
                if (TryGetClassType(node.Value, out var ct)) classTypeIndex[nodeId] = ct;
                if (!node.Value.TryGetProperty("inputs", out var inputs) || inputs.ValueKind != JsonValueKind.Object) continue;

                var edges = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var inputKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var texts = new List<string>();

                foreach (var input in inputs.EnumerateObject())
                {
                    inputKeys.Add(input.Name);
                    if (input.Value.ValueKind == JsonValueKind.String)
                    {
                        var text = (input.Value.GetString() ?? string.Empty).Trim();
                        if (text.Length >= 2) texts.Add(text);
                    }
                    else if (input.Value.ValueKind == JsonValueKind.Array && input.Value.GetArrayLength() >= 1)
                    {
                        var first = input.Value[0];
                        if (first.ValueKind == JsonValueKind.String)
                        {
                            var targetId = first.GetString() ?? string.Empty;
                            if (targetId.Length > 0) edges[input.Name] = targetId;
                        }
                    }
                }
                if (texts.Count > 0) textIndex[nodeId] = texts;
                if (edges.Count > 0) edgeIndex[nodeId] = edges;
                if (inputKeys.Count > 0) inputKeysByNode[nodeId] = inputKeys;
            }

            var collectedTexts = new List<string>();

            foreach (var node in root.EnumerateObject())
            {
                if (node.Value.ValueKind != JsonValueKind.Object) continue;
                if (!inputKeysByNode.TryGetValue(node.Name, out var samplerInputKeys)) continue;

                var ct = classTypeIndex.TryGetValue(node.Name, out var cType) ? cType : null;
                if (!IsSamplerNode(ct, samplerInputKeys)) continue;

                if (node.Value.TryGetProperty("inputs", out var si) && si.ValueKind == JsonValueKind.Object)
                {
                    foreach (var input in si.EnumerateObject())
                    {
                        if (input.Value.ValueKind != JsonValueKind.String) continue;
                        if (!ConditioningInputKeys.Contains(input.Name)) continue;
                        var inline = (input.Value.GetString() ?? string.Empty).Trim();
                        if (inline.Length >= 2) collectedTexts.Add(inline);
                    }
                }

                var queue = new Queue<string>();
                var visited = new HashSet<string>(StringComparer.Ordinal);

                if (edgeIndex.TryGetValue(node.Name, out var samplerEdges))
                {
                    foreach (var key in PositiveOnlyInputKeys)
                        if (samplerEdges.TryGetValue(key, out var t) && visited.Add(t)) queue.Enqueue(t);
                    foreach (var gk in new[] { "guider", "guide" })
                        if (samplerEdges.TryGetValue(gk, out var t) && visited.Add(t)) queue.Enqueue(t);
                    foreach (var kv in samplerEdges)
                        if (kv.Key.IndexOf("positive", StringComparison.OrdinalIgnoreCase) >= 0 && visited.Add(kv.Value))
                            queue.Enqueue(kv.Value);
                }

                int vc = 0;
                while (queue.Count > 0 && vc < MaxVisitedNodes)
                {
                    var curId = queue.Dequeue(); vc++;
                    var curCt = classTypeIndex.TryGetValue(curId, out var cnType) ? cnType : null;
                    bool isGuider = curCt != null && (curCt.IndexOf("Guider", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                       curCt.IndexOf("\u5F15\u5BFC", StringComparison.Ordinal) >= 0);
                    bool isCondPath = IsConditioningPathNode(curCt, curId, inputKeysByNode, edgeIndex);

                    if (textIndex.TryGetValue(curId, out var texts))
                    {
                        var curIk = inputKeysByNode.TryGetValue(curId, out var keys) ? keys : null;
                        foreach (var t in texts)
                        {
                            if (IsPlausiblePromptText(t, curCt, curIk))
                                collectedTexts.Add(t);
                        }
                    }

                    if (edgeIndex.TryGetValue(curId, out var curEdges))
                    {
                        if (!isCondPath)
                        {
                            DebugLog?.Invoke($"ComfyUI BFS: skip edges from {curId} (class={curCt ?? "?"}) — not conditioning path");
                            continue;
                        }

                        foreach (var kv2 in curEdges)
                        {
                            var targetId = kv2.Value;
                            if (!visited.Add(targetId)) continue;
                            if (isGuider && kv2.Key.IndexOf("negative", StringComparison.OrdinalIgnoreCase) >= 0)
                                continue;
                            queue.Enqueue(targetId);
                        }
                    }
                }
            }

            string best = string.Empty;
            foreach (var t in collectedTexts)
            {
                if (IsMetaPrompt(t)) continue;
                if (t.Length > best.Length) best = t;
            }
            return string.IsNullOrEmpty(best) ? string.Empty : Truncate(best, MaxPositiveLength);
        }

        private static bool IsConditioningPathNode(string? classType, string nodeId,
            Dictionary<string, HashSet<string>> inputKeysByNode,
            Dictionary<string, Dictionary<string, string>> edgeIndex)
        {
            if (string.IsNullOrEmpty(classType)) return false;

            if (classType.IndexOf("Guider", StringComparison.OrdinalIgnoreCase) >= 0 ||
                classType.IndexOf("\u5F15\u5BFC", StringComparison.Ordinal) >= 0)
                return true;

            foreach (var hint in ConditioningPathClassHints)
                if (classType.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;

            if (inputKeysByNode.TryGetValue(nodeId, out var keys))
                foreach (var tk in TextInputKeys)
                    if (keys.Contains(tk)) return true;

            // Fallback: if this node has outgoing edges, it connects something —
            // treat it as on-path rather than dead-ending on opaque intermediate nodes.
            if (edgeIndex != null && edgeIndex.TryGetValue(nodeId, out var edges) && edges.Count > 0)
                return true;

            return false;
        }

        private static bool IsMetaPrompt(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return true;
            return text.StartsWith("You are", StringComparison.OrdinalIgnoreCase) ||
                   text.StartsWith("Your task is", StringComparison.OrdinalIgnoreCase) ||
                   text.StartsWith("Think step by step", StringComparison.OrdinalIgnoreCase) ||
                   text.IndexOf("You are an expert prompt engineer", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   text.IndexOf("Your task is to expand", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   text.IndexOf("Think step by step about", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   text.IndexOf("Then output a single", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   text.StartsWith("System:", StringComparison.OrdinalIgnoreCase) ||
                   text.StartsWith("Instruction:", StringComparison.OrdinalIgnoreCase) ||
                   text.StartsWith("Assistant:", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSamplerNode(string? classType, HashSet<string> inputKeys)
        {
            if (!string.IsNullOrEmpty(classType))
            {
                if (KnownSamplerClassTypes.Contains(classType)) return true;
                if (NonSamplerClassTypes.Contains(classType)) return false;
            }
            if (MatchesSamplerContract(inputKeys)) return true;
            if (!string.IsNullOrEmpty(classType))
            {
                if (classType.IndexOf("Sampler", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    classType.IndexOf("\u91C7\u6837\u5668", StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        private static bool MatchesSamplerContract(HashSet<string> inputKeys)
        {
            bool hasDiffusion = false, hasConditioning = false, hasExecution = false;
            foreach (var key in inputKeys)
            {
                if (DiffusionInputKeys.Contains(key)) hasDiffusion = true;
                if (ConditioningInputKeys.Contains(key)) hasConditioning = true;
                if (ExecutionInputKeys.Contains(key)) hasExecution = true;
                if (hasDiffusion && hasConditioning && hasExecution) return true;
            }
            return false;
        }

        private static bool IsPlausiblePromptText(string text, string? classType, HashSet<string>? inputKeys)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length < 2) return false;
            if (IsMetaPrompt(text)) return false;
            if (IsLikelyNoise(text)) return false;
            if (classType != null)
                foreach (var hint in TextNodeClassHints)
                    if (classType.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (inputKeys != null)
                foreach (var key in inputKeys)
                    if (TextInputKeys.Contains(key)) return true;
            return text.Length >= 20;
        }

        private static bool IsLikelyNoise(string text)
        {
            if (text.Length == 0) return true;
            bool allNumeric = true; int alphaCount = 0; int pathSepCount = 0; int limit = Math.Min(text.Length, 60);
            for (int i = 0; i < limit; i++)
            {
                char c = text[i];
                if (c == '/' || c == '\\') pathSepCount++;
                if (char.IsLetter(c)) alphaCount++;
                if (!char.IsDigit(c) && c != '.' && c != '-' && c != ' ' && c != ',' && c != '_') allNumeric = false;
            }
            if (allNumeric && alphaCount == 0) return true;
            foreach (var ext in new[] { ".safetensors", ".ckpt", ".pt", ".pth", ".bin", ".gguf", ".sft" })
                if (text.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;
            if (pathSepCount >= 2 && alphaCount < 5) return true;
            return false;
        }

        private static bool TryGetClassType(JsonElement node, out string classType)
        {
            classType = string.Empty;
            if (node.TryGetProperty("class_type", out var v) && v.ValueKind == JsonValueKind.String)
            { classType = v.GetString() ?? string.Empty; return true; }
            return false;
        }

        private static string Truncate(string text, int maxLength)
            => text.Length <= maxLength ? text : text.Substring(0, maxLength);
    }
}