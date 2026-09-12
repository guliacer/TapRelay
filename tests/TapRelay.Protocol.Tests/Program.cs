using System;
using System.Collections.Generic;
using System.Text.Json;
using TapRelay;

internal static class Program
{
    private static int _passed;
    private static readonly List<string> Failures = new List<string>();

    private static int Main()
    {
        Test("connection QR payload carries the configured host and port", () =>
        {
            if (!ConnectionQrProtocol.TryCreatePayload("ws://notify.example.com:2233", out var payload, out _))
            {
                return false;
            }
            return payload == "taprelay://connect?host=notify.example.com&port=2233";
        });


        Test("network settings preserve public advertised endpoint separately from local bind", () =>
        {
            var config = new NotifyServerConfig
            {
                ListenAddress = "0.0.0.0",
                ListenPort = 1122,
                AdvertisedAddress = "public.example.com",
                AdvertisedPort = 443
            }.Normalize();
            return config.ListenAddress == "0.0.0.0" &&
                   config.ListenPort == 1122 &&
                   config.AdvertisedAddress == "public.example.com" &&
                   config.EffectiveAdvertisedPort == 443;
        });

        Test("network settings reject an invalid port", () =>
        {
            try
            {
                new NotifyServerConfig { ListenPort = 65536 }.Normalize();
                return false;
            }
            catch (ArgumentException)
            {
                return true;
            }
        });

        Test("approval request gets safe defaults", () =>
        {
            using var json = JsonDocument.Parse("{\"eventType\":\"approval.requested\",\"source\":\"codex\",\"toolName\":\"Bash\"}");
            return TaskEvent.TryParse(json.RootElement, out var taskEvent, out _) &&
                   taskEvent.RequiresAction &&
                   taskEvent.Status == "waiting" &&
                   taskEvent.RequestId == taskEvent.EventId &&
                   taskEvent.ExpiresAtUnixMs > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        });

        Test("unsupported event types are rejected", () =>
        {
            using var json = JsonDocument.Parse("{\"eventType\":\"tool.input.raw\",\"source\":\"codex\"}");
            return !TaskEvent.TryParse(json.RootElement, out _, out var error) &&
                   error == "unsupported eventType";
        });

        Test("event payload keeps the live event envelope", () =>
        {
            using var json = JsonDocument.Parse("{\"eventId\":\"event-1\",\"eventType\":\"tool.started\",\"source\":\"codex\",\"sessionId\":\"session-1\",\"toolName\":\"Bash\"}");
            if (!TaskEvent.TryParse(json.RootElement, out var taskEvent, out _)) return false;
            var payload = taskEvent.ToPayload();
            return (string)payload["type"] == "event" &&
                   (string)payload["eventId"] == "event-1" &&
                   (string)payload["eventType"] == "tool.started" &&
                   (string)payload["source"] == "codex" &&
                   payload.ContainsKey("time");
        });

        Test("approval resolution carries the original request", () =>
        {
            using var json = JsonDocument.Parse("{\"eventId\":\"event-2\",\"eventType\":\"approval.requested\",\"source\":\"codex\",\"requestId\":\"request-2\",\"toolName\":\"Bash\"}");
            if (!TaskEvent.TryParse(json.RootElement, out var taskEvent, out _)) return false;
            var resolution = taskEvent.CreateResolution("allow");
            var payload = resolution.ToPayload();
            return resolution.EventType == "approval.resolved" &&
                   resolution.Status == "approved" &&
                   resolution.RequestId == "request-2" &&
                   resolution.ApprovalDecision == "allow" &&
                   (bool)payload["requiresAction"] == false;
        });

        Test("comfyui extracts KSampler positive prompt via CLIPTextEncode reference", () =>
        {
            const string graph = "{" +
                "\"1\":{\"class_type\":\"KSampler\",\"inputs\":{\"positive\":[\"2\",0],\"negative\":[\"3\",0]}}," +
                "\"2\":{\"class_type\":\"CLIPTextEncode\",\"inputs\":{\"text\":\"a beautiful cat, highly detailed\"}}," +
                "\"3\":{\"class_type\":\"CLIPTextEncode\",\"inputs\":{\"text\":\"blurry, low quality\"}}," +
                "\"4\":{\"class_type\":\"VAEDecode\",\"inputs\":{\"samples\":[\"1\",0]}}" +
                "}";
            var positive = ComfyUIPromptParser.ExtractKSamplerPositive(graph);
            return positive == "a beautiful cat, highly detailed";
        });

        Test("comfyui extracts KSamplerAdvanced positive prompt", () =>
        {
            const string graph = "{" +
                "\"10\":{\"class_type\":\"KSamplerAdvanced\",\"inputs\":{\"positive\":[\"20\",0]}}," +
                "\"20\":{\"class_type\":\"CLIPTextEncode\",\"inputs\":{\"text\":\"masterpiece, best quality, intricate\"}}" +
                "}";
            return ComfyUIPromptParser.ExtractKSamplerPositive(graph) == "masterpiece, best quality, intricate";
        });

        Test("comfyui returns empty when no KSampler positive is present", () =>
        {
            const string graph = "{\"5\":{\"class_type\":\"VAEDecode\",\"inputs\":{\"samples\":[\"1\",0]}}}";
            return ComfyUIPromptParser.ExtractKSamplerPositive(graph) == string.Empty;
        });

        Test("comfyui tolerates malformed prompt JSON", () =>
        {
            return ComfyUIPromptParser.ExtractKSamplerPositive("not json {{") == string.Empty &&
                   ComfyUIPromptParser.ExtractKSamplerPositive("") == string.Empty &&
                   ComfyUIPromptParser.ExtractKSamplerPositive("   ") == string.Empty;
        });

        Test("comfyui falls back to inline string positive when not a node reference", () =>
        {
            const string graph = "{\"7\":{\"class_type\":\"KSampler\",\"inputs\":{\"positive\":\"direct prompt text\"}}}";
            return ComfyUIPromptParser.ExtractKSamplerPositive(graph) == "direct prompt text";
        });

        Test("comfyui extracts positive from GuliNodes GGCLIPText with 文本 key", () =>
        {
            const string graph = "{" +
                "\"605\":{\"class_type\":\"KSampler\",\"inputs\":{\"positive\":[\"780\",0]}}," +
                "\"780\":{\"class_type\":\"GGCLIPText\",\"inputs\":{\"CLIP名称\":\"flux1.safetensors\",\"类型\":\"flux\",\"文本\":\"时尚摄影，浅景深，柔光\"}}" +
                "}";
            return ComfyUIPromptParser.ExtractKSamplerPositive(graph) == "时尚摄影，浅景深，柔光";
        });

        Test("comfyui follows multi-hop chain through intermediate node to text", () =>
        {
            // Simulates: KSampler → EditTextEncode_EditUtils → GGCLIPText
            const string graph = "{" +
                "\"100\":{\"class_type\":\"KSampler\",\"inputs\":{\"positive\":[\"200\",0],\"negative\":[\"300\",0]}}," +
                "\"200\":{\"class_type\":\"EditTextEncode_EditUtils\",\"inputs\":{\"text\":[\"400\",0],\"clip\":[\"500\",0]}}," +
                "\"400\":{\"class_type\":\"GGCLIPText\",\"inputs\":{\"CLIP名称\":\"flux.safetensors\",\"文本\":\"8K高清，电影感，浅景深\"}}" +
                "}";
            return ComfyUIPromptParser.ExtractKSamplerPositive(graph) == "8K高清，电影感，浅景深";
        });

        Test("comfyui follows three-hop chain through two intermediates", () =>
        {
            // Simulates: KSamplerAdvanced → CR Text Concatenate → GGTextDisplayCopy → GGCLIPText
            const string graph = "{" +
                "\"1\":{\"class_type\":\"KSamplerAdvanced\",\"inputs\":{\"positive\":[\"2\",0]}}," +
                "\"2\":{\"class_type\":\"CR Text Concatenate\",\"inputs\":{\"text1\":[\"3\",0],\"text2\":\" fallback\"}}," +
                "\"3\":{\"class_type\":\"GGTextDisplayCopy\",\"inputs\":{\"文本内容\":[\"4\",0]}}," +
                "\"4\":{\"class_type\":\"CLIPTextEncode\",\"inputs\":{\"text\":\"soft cinematic lighting, 85mm lens\"}}" +
                "}";
            return ComfyUIPromptParser.ExtractKSamplerPositive(graph) == "soft cinematic lighting, 85mm lens";
        });

        Test("comfyui extracts from third-party SamplerCustom", () =>
        {
            const string graph = "{" +
                "\"10\":{\"class_type\":\"SamplerCustom\",\"inputs\":{\"positive\":[\"20\",0]}}," +
                "\"20\":{\"class_type\":\"CLIPTextEncode\",\"inputs\":{\"text\":\"anime style, vibrant colors\"}}" +
                "}";
            return ComfyUIPromptParser.ExtractKSamplerPositive(graph) == "anime style, vibrant colors";
        });

        Test("comfyui detects sampler by heuristic class_type containing Sampler", () =>
        {
            const string graph = "{" +
                "\"1\":{\"class_type\":\"MyCustomFluxSampler\",\"inputs\":{\"positive\":[\"2\",0]}}," +
                "\"2\":{\"class_type\":\"UnknownTextNode\",\"inputs\":{\"prompt_text\":\"cyberpunk cityscape at night\"}}" +
                "}";
            return ComfyUIPromptParser.ExtractKSamplerPositive(graph) == "cyberpunk cityscape at night";
        });

        Test("comfyui stops at first text even when multiple branches exist", () =>
        {
            // KSampler positive → NodeX (no text, has two refs: clip→NodeY, text→NodeZ)
            // NodeZ has text "winner", NodeY has text "should not be picked"
            // BFS should find NodeZ's text first (it's on the text ref path)
            const string graph = "{" +
                "\"s\":{\"class_type\":\"KSampler\",\"inputs\":{\"positive\":[\"x\",0]}}," +
                "\"x\":{\"class_type\":\"IntermediateNode\",\"inputs\":{\"clip\":[\"y\",0],\"text_ref\":[\"z\",0]}}," +
                "\"y\":{\"class_type\":\"CLIPTextEncode\",\"inputs\":{\"text\":\"should not be picked\"}}," +
                "\"z\":{\"class_type\":\"CLIPTextEncode\",\"inputs\":{\"text\":\"winner: picked by BFS\"}}" +
                "}";
            return ComfyUIPromptParser.ExtractKSamplerPositive(graph) == "winner: picked by BFS";
        });

        Test("comfyui avoids infinite loop on circular references", () =>
        {
            const string graph = "{" +
                "\"a\":{\"class_type\":\"KSampler\",\"inputs\":{\"positive\":[\"b\",0]}}," +
                "\"b\":{\"class_type\":\"LoopingNode\",\"inputs\":{\"ref\":[\"a\",0],\"text\":\"still works\"}}" +
                "}";
            return ComfyUIPromptParser.ExtractKSamplerPositive(graph) == "still works";
        });

        Test("comfyui SamplerCustomAdvanced follows guider chain to prompt", () =>
        {
            // Simulates: SamplerCustomAdvanced.guider → BasicGuider.positive → CLIPTextEncode
            const string graph = "{" +
                "\"1\":{\"class_type\":\"SamplerCustomAdvanced\",\"inputs\":{\"noise\":[\"10\",0],\"guider\":[\"2\",0],\"sampler\":[\"11\",0],\"sigmas\":[\"12\",0],\"latent_image\":[\"13\",0]}}," +
                "\"2\":{\"class_type\":\"BasicGuider\",\"inputs\":{\"model\":[\"3\",0],\"positive\":[\"5\",0],\"negative\":[\"6\",0]}}," +
                "\"5\":{\"class_type\":\"CLIPTextEncode\",\"inputs\":{\"text\":\"ancient Chinese palace, ink wash style\",\"clip\":[\"7\",0]}}," +
                "\"6\":{\"class_type\":\"CLIPTextEncode\",\"inputs\":{\"text\":\"blurry, low quality\",\"clip\":[\"7\",0]}}" +
                "}";
            return ComfyUIPromptParser.ExtractKSamplerPositive(graph) == "ancient Chinese palace, ink wash style";
        });

        Test("comfyui SamplerCustomAdvanced with CFGGuider grabs positive", () =>
        {
            const string graph = "{" +
                "\"1\":{\"class_type\":\"SamplerCustomAdvanced\",\"inputs\":{\"noise\":[\"10\",0],\"guider\":[\"2\",0],\"sampler\":[\"11\",0],\"sigmas\":[\"12\",0],\"latent_image\":[\"13\",0]}}," +
                "\"2\":{\"class_type\":\"CFGGuider\",\"inputs\":{\"model\":[\"3\",0],\"positive\":[\"5\",0],\"negative\":[\"6\",0],\"cfg\":7.5}}," +
                "\"5\":{\"class_type\":\"CLIPTextEncode\",\"inputs\":{\"text\":\"cyberpunk neon city, raining\",\"clip\":[\"7\",0]}}," +
                "\"6\":{\"class_type\":\"CLIPTextEncode\",\"inputs\":{\"text\":\"blurry, low quality\",\"clip\":[\"7\",0]}}" +
                "}";
            return ComfyUIPromptParser.ExtractKSamplerPositive(graph) == "cyberpunk neon city, raining";
        });

        Test("comfyui ignores model name and picks actual prompt", () =>
        {
            // CheckpointLoaderSimple.ckpt_name is filtered out by IsLikelyNoise
            const string graph = "{" +
                "\"1\":{\"class_type\":\"KSampler\",\"inputs\":{\"positive\":[\"3\",0],\"negative\":[\"4\",0],\"model\":[\"2\",0],\"latent_image\":[\"5\",0]}}," +
                "\"2\":{\"class_type\":\"CheckpointLoaderSimple\",\"inputs\":{\"ckpt_name\":\"dreamshaper_8.safetensors\"}}," +
                "\"3\":{\"class_type\":\"CLIPTextEncode\",\"inputs\":{\"text\":\"一只可爱的橘猫坐在窗台上\",\"clip\":[\"2\",1]}}" +
                "}";
            return ComfyUIPromptParser.ExtractKSamplerPositive(graph) == "一只可爱的橘猫坐在窗台上";
        });

        Test("comfyui workflow block converts and extracts positive", () =>
        {
            const string workflow = "{" +
                "\"nodes\":[" +
                "{\"id\":1,\"type\":\"CheckpointLoaderSimple\",\"inputs\":[],\"widgets_values\":[\"models/flux.safetensors\"]}," +
                "{\"id\":2,\"type\":\"CLIPTextEncode\",\"inputs\":[{\"name\":\"clip\",\"link\":1}],\"widgets_values\":[\"一位古风发髻美女，灰色长发，仰拍视角，面部特写\"]}," +
                "{\"id\":3,\"type\":\"CLIPTextEncode\",\"inputs\":[{\"name\":\"clip\",\"link\":1}],\"widgets_values\":[\"low quality, bad anatomy\"]}," +
                "{\"id\":4,\"type\":\"KSampler\",\"inputs\":[{\"name\":\"model\",\"link\":2},{\"name\":\"positive\",\"link\":3},{\"name\":\"negative\",\"link\":4},{\"name\":\"latent_image\",\"link\":5}],\"widgets_values\":[123,20,8,\"euler\",\"normal\",1]}" +
                "]," +
                "\"links\":[" +
                "[1,1,1,2,0,\"MODEL\"]," +
                "[2,1,2,4,0,\"MODEL\"]," +
                "[3,2,0,4,1,\"CONDITIONING\"]," +
                "[4,3,0,4,2,\"CONDITIONING\"]," +
                "[5,5,0,4,3,\"LATENT\"]" +
                "]}";
            var result = ComfyUIPromptParser.ExtractBestPositive(null, workflow);
            return result == "一位古风发髻美女，灰色长发，仰拍视角，面部特写";
        });

        Test("comfyui workflow prioritised over prompt graph", () =>
        {
            // prompt block has stale text, workflow has real text
            const string prompt = "{" +
                "\"1\":{\"class_type\":\"KSampler\",\"inputs\":{\"positive\":[\"2\",0],\"latent_image\":[\"3\",0],\"model\":[\"4\",0]}}," +
                "\"2\":{\"class_type\":\"CLIPTextEncode\",\"inputs\":{\"text\":\"stale prompt\"}}" +
                "}";
            const string workflow = "{" +
                "\"nodes\":[" +
                "{\"id\":1,\"type\":\"KSampler\",\"inputs\":[{\"name\":\"positive\",\"link\":2},{\"name\":\"latent_image\",\"link\":3},{\"name\":\"model\",\"link\":1}],\"widgets_values\":[1,20,8]}," +
                "{\"id\":2,\"type\":\"CLIPTextEncode\",\"inputs\":[{\"name\":\"clip\",\"link\":1}],\"widgets_values\":[\"最新修改的提示词\"]}," +
                "{\"id\":3,\"type\":\"EmptyLatentImage\",\"inputs\":[]}" +
                "]," +
                "\"links\":[" +
                "[1,2,1,1,0,\"CLIP\"]," +
                "[2,2,0,1,0,\"CONDITIONING\"]," +
                "[3,3,0,1,1,\"LATENT\"]" +
                "]}";
            var result = ComfyUIPromptParser.ExtractBestPositive(prompt, workflow);
            return result == "最新修改的提示词";
        });

        Test("comfyui png chunk reader extracts prompt and workflow blocks", () =>
        {
            // Build a minimal valid PNG with tEXt chunks
            var png = BuildTestPng(new Dictionary<string, string>
            {
                ["prompt"] = "{\"1\":{\"class_type\":\"KSampler\",\"inputs\":{\"positive\":[\"2\",0]}},\"2\":{\"class_type\":\"CLIPTextEncode\",\"inputs\":{\"text\":\"png embedded prompt\"}}}",
                ["workflow"] = "{\"nodes\":[],\"links\":[]}"
            });

            var (promptJson, workflowJson) = ComfyUIPngChunkReader.ExtractComfyuiBlocks(png);
            return promptJson != null && promptJson.Contains("png embedded prompt")
                && workflowJson != null && workflowJson.Contains("\"nodes\"");
        });

        Test("comfyui png chunk reader returns nulls for non-png", () =>
        {
            var (promptJson, workflowJson) = ComfyUIPngChunkReader.ExtractComfyuiBlocks(new byte[] { 0, 1, 2, 3 });
            return promptJson == null && workflowJson == null;
        });

        Test("comfyui workflow handles nodes without widgets_values", () =>
        {
            const string workflow = "{" +
                "\"nodes\":[" +
                "{\"id\":1,\"type\":\"KSampler\",\"inputs\":[{\"name\":\"positive\",\"link\":1},{\"name\":\"latent_image\",\"link\":2},{\"name\":\"model\",\"link\":3}]}," +
                "{\"id\":2,\"type\":\"CLIPTextEncode\",\"inputs\":[{\"name\":\"clip\",\"link\":4}],\"widgets_values\":[\"widgets value wins\"]}" +
                "]," +
                "\"links\":[" +
                "[1,2,0,1,0,\"CONDITIONING\"]," +
                "[2,4,0,1,1,\"LATENT\"]," +
                "[3,5,0,1,2,\"MODEL\"]" +
                "]}";
            var result = ComfyUIPromptParser.ExtractBestPositive(null, workflow);
            return result == "widgets value wins";
        });

        Test("secret masker hides sk- and github keys", () =>
        {
            var masked = SecretMasker.Mask("run with sk-abcdef1234567890abcdef123456 and ghp_abcdefghijklmnop1234");
            return masked.IndexOf("sk-abcdef", StringComparison.Ordinal) < 0 &&
                   masked.IndexOf("ghp_abcdef", StringComparison.Ordinal) < 0 &&
                   masked.IndexOf("[\u5df2\u9690\u85cf]", StringComparison.Ordinal) >= 0;
        });

        Test("secret masker hides key=value secrets but keeps the key name", () =>
        {
            var masked = SecretMasker.Mask("export API_KEY=supersecret12345 && run");
            return masked.IndexOf("supersecret12345", StringComparison.Ordinal) < 0 &&
                   masked.IndexOf("API_KEY", StringComparison.Ordinal) >= 0;
        });

        Test("secret masker leaves ordinary text intact", () =>
        {
            const string text = "需要确认：执行 npm run build";
            return SecretMasker.Mask(text) == text;
        });

        Test("approval posture round-trips through config normalization", () =>
        {
            var config = new NotifyServerConfig { ApprovalPosture = ApprovalPosture.Notify }.Normalize();
            var cloned = config.Clone();
            return cloned.ApprovalPosture == ApprovalPosture.Notify &&
                   new NotifyServerConfig().Normalize().ApprovalPosture == ApprovalPosture.Full;
        });

        Console.WriteLine($"TapRelay protocol tests: {_passed} passed, {Failures.Count} failed");
        foreach (var failure in Failures) Console.WriteLine("  FAIL: " + failure);
        return Failures.Count == 0 ? 0 : 1;
    }

    private static void Test(string name, Func<bool> assertion)
    {
        try
        {
            if (assertion())
            {
                _passed++;
                return;
            }
        }
        catch (Exception error)
        {
            Failures.Add(name + " -> " + error);
            return;
        }
        Failures.Add(name);
    }

    private static byte[] BuildTestPng(Dictionary<string, string> textChunks)
    {
        // Minimal PNG: signature + IHDR + tEXt chunks + IEND
        var ms = new System.IO.MemoryStream();

        // PNG signature
        ms.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);

        // IHDR
        WritePngChunk(ms, "IHDR", BuildIhdr());

        // tEXt chunks
        foreach (var kv in textChunks)
        {
            var data = new System.IO.MemoryStream();
            foreach (char c in kv.Key) data.WriteByte((byte)c);
            data.WriteByte(0);
            foreach (char c in kv.Value) data.WriteByte((byte)c);
            WritePngChunk(ms, "tEXt", data.ToArray());
        }

        // IEND
        WritePngChunk(ms, "IEND", Array.Empty<byte>());

        return ms.ToArray();
    }

    private static byte[] BuildIhdr()
    {
        var ms = new System.IO.MemoryStream();
        WriteBigEndian(ms, 1);  // width
        WriteBigEndian(ms, 1);  // height
        ms.WriteByte(8);        // bit depth
        ms.WriteByte(2);        // color type (RGB)
        ms.WriteByte(0);        // compression
        ms.WriteByte(0);        // filter
        ms.WriteByte(0);        // interlace
        return ms.ToArray();
    }

    private static void WritePngChunk(System.IO.MemoryStream ms, string type, byte[] data)
    {
        WriteBigEndian(ms, data.Length);
        ms.Write(System.Text.Encoding.ASCII.GetBytes(type), 0, 4);
        ms.Write(data, 0, data.Length);
        uint crc = Crc32(System.Text.Encoding.ASCII.GetBytes(type), data);
        WriteBigEndian(ms, (int)crc);
    }

    private static void WriteBigEndian(System.IO.MemoryStream ms, int value)
    {
        ms.WriteByte((byte)(value >> 24));
        ms.WriteByte((byte)(value >> 16));
        ms.WriteByte((byte)(value >> 8));
        ms.WriteByte((byte)value);
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in type) crc = Crc32Update(crc, b);
        foreach (var b in data) crc = Crc32Update(crc, b);
        return crc ^ 0xFFFFFFFF;
    }

    private static uint Crc32Update(uint crc, byte b)
    {
        crc ^= b;
        for (int i = 0; i < 8; i++)
            crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        return crc;
    }
}
