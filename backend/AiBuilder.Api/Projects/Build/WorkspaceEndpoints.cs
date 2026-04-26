using System.IO.Compression;
using Microsoft.AspNetCore.Http.Features;

namespace AiBuilder.Api.Projects.Build;

public static class WorkspaceEndpoints
{
    // Size cap for the file-view endpoint. Anything bigger is 413'd so we
    // don't accidentally ship a 100MB node_modules blob down to the browser.
    private const long MaxFileBytes = 1_000_000; // 1 MB

    // Shared exclusion list. Tree walk, zip export, and anything else that
    // traverses the workspace for user-visible purposes must read from here
    // so they stay consistent. `.git` is owned by the iteration machinery,
    // the rest is build output / dependency caches.
    private static readonly HashSet<string> WorkspaceIgnored = new(StringComparer.Ordinal)
    {
        ".git", "node_modules", "bin", "obj", ".aibuilder", "dist", "publish",
    };

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

        // Streams a zip of the workspace straight to the response — no temp
        // file. Uses the same ignore list as the tree walk so the zip only
        // contains what Patrick actually sees in the browser.
        group.MapGet("/zip", async (string id, ProjectStore projects, WorkspaceManager ws, HttpContext ctx, CancellationToken ct) =>
        {
            var project = await projects.GetSafeAsync(id, ct);
            if (project is null) { ctx.Response.StatusCode = 404; return; }
            var root = ws.ResolvePath(project.pierAppName);
            if (!Directory.Exists(root)) { ctx.Response.StatusCode = 404; return; }

            var filename = $"{project.pierAppName}-{DateTime.UtcNow:yyyyMMdd-HHmm}.zip";
            ctx.Response.ContentType = "application/zip";
            ctx.Response.Headers["Content-Disposition"] = $"attachment; filename=\"{filename}\"";
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";

            // Kestrel blocks synchronous writes on the response body by
            // default, and ZipArchive only has a sync Write API (the
            // central directory is flushed on Dispose). Enable sync I/O
            // for this one request so the archive can be streamed out.
            var sync = ctx.Features.Get<IHttpBodyControlFeature>();
            if (sync is not null) sync.AllowSynchronousIO = true;

            using (var archive = new ZipArchive(ctx.Response.Body, ZipArchiveMode.Create, leaveOpen: true))
            {
                WriteDirectoryToZip(archive, root, root, ct);
            }
            await ctx.Response.Body.FlushAsync(ct);
        });
    }

    private static void WalkTree(string root, string current, List<WorkspaceNodeDto> output)
    {
        foreach (var dir in Directory.EnumerateDirectories(current).OrderBy(d => d, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(dir);
            if (WorkspaceIgnored.Contains(name)) continue;
            var rel = Path.GetRelativePath(root, dir).Replace('\\', '/');
            output.Add(new WorkspaceNodeDto(rel, name, true, null));
            WalkTree(root, dir, output);
        }
        foreach (var file in Directory.EnumerateFiles(current).OrderBy(f => f, StringComparer.Ordinal))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            output.Add(new WorkspaceNodeDto(rel, Path.GetFileName(file), false, new FileInfo(file).Length));
        }
    }

    private static void WriteDirectoryToZip(ZipArchive archive, string root, string current, CancellationToken ct)
    {
        foreach (var dir in Directory.EnumerateDirectories(current).OrderBy(d => d, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (WorkspaceIgnored.Contains(Path.GetFileName(dir))) continue;
            WriteDirectoryToZip(archive, root, dir, ct);
        }
        foreach (var file in Directory.EnumerateFiles(current).OrderBy(f => f, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            var entry = archive.CreateEntry(rel, CompressionLevel.Optimal);
            try
            {
                using var entryStream = entry.Open();
                using var fileStream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                fileStream.CopyTo(entryStream);
            }
            catch (IOException)
            {
                // Another process holds the file exclusively — skip it rather
                // than tearing down the whole download.
            }
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
