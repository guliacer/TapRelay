using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace TapRelay
{
    /// <summary>
    /// Converts a ComfyUI UI workflow block into the API prompt graph format
    /// that ComfyUIPromptParser expects.
    ///
    /// Workflow block (from PNG "workflow" chunk or app.graph.serialize()):
    ///   { nodes: [{ id, type, inputs: [{name, link}], widgets_values }],
    ///     links: [ [linkId, srcId, srcSlot, dstId, dstSlot, linkType] ] }
    ///
    /// API prompt graph:
    ///   { "nodeId": { class_type: "...", inputs: { key: value | [nodeId, slot] } } }
    ///
    /// Key advantage: workflow's widgets_values captures the text the user
    /// actually typed at save time, while the API prompt graph may carry
    /// stale batch values.  SuYan prioritizes workflow > prompt for this reason.
    /// </summary>
    internal static class ComfyUIWorkflowConverter
    {
        /// <summary>Widget-index → input-key mappings for common text nodes.</summary>
        private static readonly Dictionary<string, string[]> WidgetToInputMap =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "CLIPTextEncode", new[] { "text" } },
            { "CLIPTextEncodeSDXL", new[] { "text_g", "text_l" } },
            { "CLIPTextEncodeSD3", new[] { "text" } },
            { "CLIPTextEncodeFlux", new[] { "text" } },
            { "CLIPTextEncodeHunyuanDiT", new[] { "text" } },
            { "GGCLIPText", new[] { "\u6587\u672C" } },
            { "GGCLIPTextEncode", new[] { "\u6587\u672C" } },
            { "ShowText|pysssss", new[] { "text" } },
            { "DisplayText", new[] { "text" } },
            { "PromptOutput", new[] { "text" } },
        };

        public static string? ConvertToApiGraph(string workflowJson)
        {
            if (string.IsNullOrWhiteSpace(workflowJson)) return null;
            try
            {
                using var doc = JsonDocument.Parse(workflowJson);
                return ConvertToApiGraph(doc.RootElement);
            }
            catch (JsonException) { return null; }
        }

        public static string? ConvertToApiGraph(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("nodes", out var nodesEl) || nodesEl.ValueKind != JsonValueKind.Array)
                return null;

            // ── Parse link array: (dstId, dstSlot) → (srcId, srcSlot) ──
            // Format: [linkId, srcId, srcSlot, dstId, dstSlot, linkType]
            var linkMap = new Dictionary<(int dstId, int dstSlot), (int srcId, int srcSlot)>();
            if (root.TryGetProperty("links", out var linksEl) && linksEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var l in linksEl.EnumerateArray())
                {
                    if (l.ValueKind != JsonValueKind.Array || l.GetArrayLength() < 5) continue;
                    int srcId = GetInt(l[1]);
                    int srcSlot = GetInt(l[2]);
                    int dstId = GetInt(l[3]);
                    int dstSlot = GetInt(l[4]);
                    if (dstId > 0) linkMap[(dstId, dstSlot)] = (srcId, srcSlot);
                }
            }

            // ── Build per-node slot→name map ──
            var nodeSlotNames = new Dictionary<int, List<string>>();
            var nodeSlotLinks = new Dictionary<int, Dictionary<int, int>>();

            foreach (var node in nodesEl.EnumerateArray())
            {
                if (node.ValueKind != JsonValueKind.Object) continue;
                int id = GetNodeId(node);
                if (id < 0) continue;

                var slotNames = new List<string>();
                var slotLinks = new Dictionary<int, int>();
                if (node.TryGetProperty("inputs", out var inputsEl) && inputsEl.ValueKind == JsonValueKind.Array)
                {
                    int si = 0;
                    foreach (var inp in inputsEl.EnumerateArray())
                    {
                        string name = "";
                        if (inp.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String)
                            name = nEl.GetString() ?? "";
                        slotNames.Add(name);

                        if (inp.TryGetProperty("link", out var lkEl) && lkEl.ValueKind == JsonValueKind.Number)
                        {
                            int linkId = lkEl.GetInt32();
                            if (linkId > 0) slotLinks[si] = linkId;
                        }
                        si++;
                    }
                }
                nodeSlotNames[id] = slotNames;
                nodeSlotLinks[id] = slotLinks;
            }

            // ── Build result JSON string ──
            var sb = new StringBuilder();
            sb.Append('{');
            bool firstNode = true;

            foreach (var node in nodesEl.EnumerateArray())
            {
                if (node.ValueKind != JsonValueKind.Object) continue;
                int id = GetNodeId(node);
                if (id < 0) continue;

                string classType = "";
                if (node.TryGetProperty("type", out var ctEl) && ctEl.ValueKind == JsonValueKind.String)
                    classType = ctEl.GetString() ?? "";

                if (!firstNode) sb.Append(',');
                firstNode = false;
                sb.Append('"'); sb.Append(Esc(id.ToString())); sb.Append("\":{\"class_type\":\"");
                sb.Append(Esc(classType)); sb.Append("\",\"inputs\":{");

                bool firstInput = true;

                // 1. Widget values → input keys
                if (node.TryGetProperty("widgets_values", out var wvEl) && wvEl.ValueKind == JsonValueKind.Array)
                {
                    string[]? wk = null;
                    WidgetToInputMap.TryGetValue(classType, out wk);

                    int wi = 0;
                    foreach (var wv in wvEl.EnumerateArray())
                    {
                        string? key = (wk != null && wi < wk.Length) ? wk[wi] : null;

                        if (key == null && wi == 0 && classType.IndexOf("text", StringComparison.OrdinalIgnoreCase) >= 0)
                            key = "text";   // first widget of text-like nodes

                        if (key != null)
                        {
                            if (!firstInput) sb.Append(',');
                            firstInput = false;
                            sb.Append('"'); sb.Append(Esc(key)); sb.Append("\":");
                            sb.Append(wv.GetRawText());
                        }
                        wi++;
                    }
                }

                // 2. Link edges → [nodeId, slot] references
                if (nodeSlotLinks.TryGetValue(id, out var slotLinks) &&
                    nodeSlotNames.TryGetValue(id, out var slotNames))
                {
                    for (int si = 0; si < slotNames.Count; si++)
                    {
                        if (!slotLinks.TryGetValue(si, out var linkId)) continue;
                        if (!linkMap.TryGetValue((id, si), out var upstream)) continue;

                        string key = si < slotNames.Count ? slotNames[si] : "";
                        if (string.IsNullOrEmpty(key)) continue;

                        if (!firstInput) sb.Append(',');
                        firstInput = false;
                        sb.Append('"'); sb.Append(Esc(key)); sb.Append("\":[");
                        sb.Append('"'); sb.Append(Esc(upstream.srcId.ToString())); sb.Append("\",");
                        sb.Append(upstream.srcSlot); sb.Append(']');
                    }
                }

                sb.Append("}}");
            }

            sb.Append('}');
            return sb.ToString();
        }

        private static int GetNodeId(JsonElement node)
        {
            if (node.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number)
                return idEl.GetInt32();
            return -1;
        }

        private static int GetInt(JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.Number) return el.GetInt32();
            if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var v)) return v;
            return 0;
        }

        private static string Esc(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }
    }
}