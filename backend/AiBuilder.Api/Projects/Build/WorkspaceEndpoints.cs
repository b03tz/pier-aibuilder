namespace AiBuilder.Api.Projects.Build;

public static class WorkspaceEndpoints
{
    // Size cap for the file-view endpoint. Anything bigger is 413'd so we
    // don't accidentally ship a 100MB node_modules blob down to the browser.
    private const long MaxFileBytes = 1_000_000; // 1 MB

    public sealed record WorkspaceFileDto(string Path, string Content, int Bytes);
    public sealed record WorkspaceNodeDto(string Path, string Name, bool IsDir, long? Size);

    public static void MapWorkspace(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects/{id}/workspace").RequireAuthorization().WithTags("workspace");

        group.MapGet("/tree", async (string id, ProjectStore projects, WorkspaceManager ws, CancellationToken ct) =>
        {
            var project = await projects.GetSafeAsync(id, ct);
            if (project is null) return Results.NotFound();
            var root = ws.ResolvePath(project.pierAppName);
            if (!Directory.Exists(root)) return Results.Ok(Array.Empty<WorkspaceNodeDto>());

            var nodes = new List<WorkspaceNodeDto>();
            WalkTree(root, root, nodes);
            return Results.Ok(nodes);
        });

        group.MapGet("/file", async (string id, string path, ProjectStore projects, WorkspaceManager ws, CancellationToken ct) =>
        {
            var project = await projects.GetSafeAsync(id, ct);
            if (project is null) return Results.NotFound();
            var root = ws.ResolvePath(project.pierAppName);
            var resolved = SafeResolve(root, path);
            if (resolved is null) return Results.BadRequest(new { error = "path-outside-workspace" });
            if (!File.Exists(resolved)) return Results.NotFound();

            var fi = new FileInfo(resolved);
            if (fi.Length > MaxFileBytes)
                return Results.Json(new { error = "file-too-large", bytes = fi.Length, cap = MaxFileBytes },
                    statusCode: 413);

            var bytes = await File.ReadAllBytesAsync(resolved, ct);
            if (!LooksLikeText(bytes))
                return Results.Json(new { error = "binary-file", bytes = bytes.Length },
                    statusCode: 415);

            return Results.Ok(new WorkspaceFileDto(path, System.Text.Encoding.UTF8.GetString(bytes), bytes.Length));
        });
    }

    private static void WalkTree(string root, string current, List<WorkspaceNodeDto> output)
    {
        foreach (var dir in Directory.EnumerateDirectories(current).OrderBy(d => d))
        {
            var name = Path.GetFileName(dir);
            if (name is ".git" or "node_modules" or "bin" or "obj" or ".aibuilder" or "dist" or "publish")
                continue;
            var rel = Path.GetRelativePath(root, dir).Replace('\\', '/');
            output.Add(new WorkspaceNodeDto(rel, name, true, null));
            WalkTree(root, dir, output);
        }
        foreach (var file in Directory.EnumerateFiles(current).OrderBy(f => f))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            output.Add(new WorkspaceNodeDto(rel, Path.GetFileName(file), false, new FileInfo(file).Length));
        }
    }

    private static string? SafeResolve(string root, string rel)
    {
        if (string.IsNullOrWhiteSpace(rel)) return null;
        var combined = Path.GetFullPath(Path.Combine(root, rel));
        var rootFull = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        return combined.StartsWith(rootFull, StringComparison.Ordinal) ? combined : null;
    }

    private static bool LooksLikeText(byte[] bytes)
    {
        // Cheap heuristic: no NUL bytes in the first 8 KB.
        var n = Math.Min(bytes.Length, 8192);
        for (int i = 0; i < n; i++) if (bytes[i] == 0) return false;
        return true;
    }
}
