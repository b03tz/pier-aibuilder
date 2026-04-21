using System.IO.Compression;

namespace AiBuilder.Api.Projects.Deploy;

public static class ZipBuilder
{
    // Writes a zip of `sourceDir` to `zipPath`. Zip contents are relative
    // paths under sourceDir (so the top-level of the zip mirrors what Pier
    // expects — framework-dependent publish output for backend, static site
    // files for frontend).
    public static void CreateFromDirectory(string sourceDir, string zipPath)
    {
        if (File.Exists(zipPath)) File.Delete(zipPath);
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
        ZipFile.CreateFromDirectory(sourceDir, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
    }
}
