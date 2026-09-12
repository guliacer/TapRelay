using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TapRelay
{
    /// <summary>
    /// Reads PNG tEXt / iTXt metadata chunks from a byte buffer,
    /// extracting keyword → text pairs (e.g. "prompt" / "workflow" blocks).
    ///
    /// ComfyUI embeds its API execution graph in a "prompt" chunk and the UI
    /// workflow in a "workflow" chunk when saving PNG output.  Multiple chunks
    /// with the same keyword can exist (custom nodes or re-save tools append
    /// metadata), so this reader returns ALL of them.
    /// </summary>
    internal static class ComfyUIPngChunkReader
    {
        /// <summary>PNG 8-byte signature.</summary>
        private static readonly byte[] PngSignature = { 137, 80, 78, 71, 13, 10, 26, 10 };

        /// <summary>
        /// Extracts all tEXt / iTXt keyword→text pairs from a PNG byte array.
        /// Returns an empty list when the input is not a valid PNG.
        /// </summary>
        public static List<PngTextChunk> ReadTextChunks(byte[] bytes)
        {
            var results = new List<PngTextChunk>();
            if (bytes == null || bytes.Length < 8 + 4) return results;

            // Verify PNG signature
            for (int i = 0; i < 8; i++)
                if (bytes[i] != PngSignature[i]) return results;

            int offset = 8;
            while (offset + 8 <= bytes.Length)
            {
                int length = ReadBigEndianInt32(bytes, offset);
                string type = Encoding.ASCII.GetString(bytes, offset + 4, 4);

                int dataStart = offset + 8;
                int dataEnd = dataStart + length;
                if (dataEnd + 4 > bytes.Length) break; // truncated

                if (type == "tEXt" || type == "iTXt")
                {
                    string keyword, text;
                    if (type == "tEXt")
                        ReadTextChunk(bytes, dataStart, length, out keyword, out text);
                    else
                        ReadItxtChunk(bytes, dataStart, length, out keyword, out text);

                    if (!string.IsNullOrEmpty(keyword))
                        results.Add(new PngTextChunk { Keyword = keyword, Text = text, ChunkType = type });
                }

                offset = dataEnd + 4; // skip CRC
            }

            return results;
        }

        /// <summary>
        /// Convenience: extract "prompt" and "workflow" blocks from a PNG,
        /// returning them as JSON strings (null if not found).
        /// For duplicate chunks the last one wins.
        /// </summary>
        public static (string? promptJson, string? workflowJson) ExtractComfyuiBlocks(byte[] pngBytes)
        {
            string? prompt = null;
            string? workflow = null;

            foreach (var chunk in ReadTextChunks(pngBytes))
            {
                if (string.Equals(chunk.Keyword, "prompt", StringComparison.Ordinal))
                    prompt = chunk.Text;
                else if (string.Equals(chunk.Keyword, "workflow", StringComparison.Ordinal))
                    workflow = chunk.Text;
            }

            return (prompt, workflow);
        }

        // ── tEXt: keyword\0text ──
        private static void ReadTextChunk(byte[] bytes, int offset, int length, out string keyword, out string text)
        {
            keyword = string.Empty;
            text = string.Empty;

            int nullPos = -1;
            for (int i = offset; i < offset + length; i++)
            {
                if (bytes[i] == 0) { nullPos = i; break; }
            }
            if (nullPos < 0) return;

            int kwLen = nullPos - offset;
            if (kwLen <= 0) return;

            keyword = Latin1ToString(bytes, offset, kwLen);
            int textStart = nullPos + 1;
            int textLen = (offset + length) - textStart;
            if (textLen > 0)
                text = Latin1ToString(bytes, textStart, textLen);
        }

        // ── iTXt: keyword\0compressionFlag\0compressionMethod\0languageTag\0translatedKeyword\0text ──
        private static void ReadItxtChunk(byte[] bytes, int offset, int length, out string keyword, out string text)
        {
            keyword = string.Empty;
            text = string.Empty;

            // Find up to 5 null separators
            var nulls = new List<int>(5);
            for (int i = offset; i < offset + length && nulls.Count < 5; i++)
            {
                if (bytes[i] == 0) nulls.Add(i);
            }
            if (nulls.Count < 2) return; // need at least keyword\0flag

            int kwLen = nulls[0] - offset;
            if (kwLen <= 0) return;
            keyword = Latin1ToString(bytes, offset, kwLen);

            // After translatedKeyword (index 4 if present, else last null)
            int textSepIdx = nulls.Count >= 5 ? 4 : nulls.Count > 2 ? nulls.Count - 1 : 1;
            int textStart = nulls[textSepIdx] + 1;
            int textLen = (offset + length) - textStart;
            if (textLen < 0) return;

            // Compression flag at nulls[0]+1
            byte compressed = bytes[nulls[0] + 1];
            if (compressed == 0 && textLen > 0)
            {
                text = Encoding.UTF8.GetString(bytes, textStart, textLen);
            }
            else
            {
                // Compressed iTXt is rare in ComfyUI; treat as Latin-1 fallback
                if (textLen > 0)
                    text = Latin1ToString(bytes, textStart, textLen);
            }
        }

        private static int ReadBigEndianInt32(byte[] bytes, int offset)
        {
            return (bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3];
        }

        private static string Latin1ToString(byte[] bytes, int offset, int length)
        {
            var chars = new char[length];
            for (int i = 0; i < length; i++)
                chars[i] = (char)(bytes[offset + i] & 0xFF);
            return new string(chars);
        }
    }

    internal struct PngTextChunk
    {
        public string Keyword;
        public string Text;
        public string ChunkType;
    }
}