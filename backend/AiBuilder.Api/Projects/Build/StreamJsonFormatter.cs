using System.Text.Json;

namespace AiBuilder.Api.Projects.Build;

// Turns a single line of `claude -p --output-format stream-json --verbose`
// into one or more human-readable log lines. If the line isn't parseable
// JSON we pass it through unchanged — belt-and-braces fallback.
public static class StreamJsonFormatter
{
    public static IEnumerable<string> Format(string jsonLine)
    {
        if (string.IsNullOrWhiteSpace(jsonLine)) yield break;
        JsonDocument? doc;
        try { doc = JsonDocument.Parse(jsonLine); }
        catch { doc = null; }
        if (doc is null) { yield return jsonLine; yield break; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                yield return jsonLine;
                yield break;
            }

            var type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() : null;

            switch (type)
            {
                case "system":
                    var subtype = root.TryGetProperty("subtype", out var st) ? st.GetString() : null;
                    yield return $"[system] {subtype ?? "event"}";
                    yield break;

                case "assistant":
                    foreach (var line in FormatAssistant(root))
                        yield return line;
                    yield break;

                case "user":
                    foreach (var line in FormatToolResult(root))
                        yield return line;
                    yield break;

                case "result":
                    var resSub = root.TryGetProperty("subtype", out var rs) ? rs.GetString() : null;
                    var resText = root.TryGetProperty("result", out var rr) ? rr.GetString() : null;
                    yield return $"[result] {resSub ?? "done"}{(string.IsNullOrEmpty(resText) ? "" : ": " + Trim(resText!, 500))}";
                    yield break;

                default:
                    yield return jsonLine;
                    yield break;
            }
        }
    }

    private static IEnumerable<string> FormatAssistant(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var msg)) { yield return "[assistant] (no message)"; yield break; }
        if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        { yield return "[assistant] (no content)"; yield break; }

        foreach (var block in content.EnumerateArray())
        {
            var btype = block.TryGetProperty("type", out var bt) ? bt.GetString() : null;
            switch (btype)
            {
                case "text":
                    var text = block.TryGetProperty("text", out var bx) ? bx.GetString() ?? "" : "";
                    foreach (var l in text.Split('\n')) yield return $"[assistant] {l}";
                    break;
                case "tool_use":
                    var name = block.TryGetProperty("name", out var bn) ? bn.GetString() : null;
                    var input = block.TryGetProperty("input", out var bi) ? bi.ToString() : "";
                    yield return $"[tool:{name}] {Trim(CompactJson(input), 400)}";
                    break;
                case "thinking":
                    var th = block.TryGetProperty("thinking", out var bth) ? bth.GetString() ?? "" : "";
                    yield return $"[thinking] {Trim(th.Replace('\n', ' '), 300)}";
                    break;
                default:
                    yield return $"[assistant:{btype}] {Trim(block.ToString(), 300)}";
                    break;
            }
        }
    }

    private static IEnumerable<string> FormatToolResult(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var msg)) yield break;
        if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) yield break;
        foreach (var block in content.EnumerateArray())
        {
            var btype = block.TryGetProperty("type", out var bt) ? bt.GetString() : null;
            if (btype != "tool_result") continue;
            var tcontent = block.TryGetProperty("content", out var tc) ? tc : default;
            string text;
            if (tcontent.ValueKind == JsonValueKind.String) text = tcontent.GetString() ?? "";
            else if (tcontent.ValueKind == JsonValueKind.Array)
            {
                var pieces = new List<string>();
                foreach (var p in tcontent.EnumerateArray())
                    if (p.TryGetProperty("text", out var px) && px.ValueKind == JsonValueKind.String)
                        pieces.Add(px.GetString()!);
                text = string.Join("\n", pieces);
            }
            else text = block.ToString();
            foreach (var l in Trim(text, 800).Split('\n')) yield return $"[tool-result] {l}";
        }
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";
    private static string CompactJson(string s) => s.Replace('\n', ' ').Replace("\r", "");
}
