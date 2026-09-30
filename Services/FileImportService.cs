using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ImageMagick;

namespace CarriolaConverter;

internal static class FileImportService
{
    public const int QueueLimit = 10_000;

    public static Task<ImportResult> ScanAsync(IEnumerable<string> sources, bool recursive,
        string? outputFolder, int capacity, CancellationToken token) => Task.Run(() =>
    {
        _ = ImagePipeline.Workers;
        var files = new List<ImportFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var readable = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        int skipped = 0, inaccessible = 0;
        bool limited = false;
        string? output = CanonicalDirectory(outputFolder);

        void AddFile(string path, bool explicitFile)
        {
            token.ThrowIfCancellationRequested();
            if (!seen.Add(path)) { skipped++; return; }
            if (files.Count >= capacity) { limited = true; return; }
            try
            {
                // Arquivos escolhidos individualmente podem ter extensão incomum.
                // Para pastas, incluímos apenas extensões reconhecidas pelo leitor.
                if (!explicitFile)
                {
                    string extension = Path.GetExtension(path);
                    if (!readable.TryGetValue(extension, out bool supports))
                    {
                        supports = MagickFormatInfo.Create(path)?.SupportsReading == true;
                        readable[extension] = supports;
                    }
                    if (!supports) { skipped++; return; }
                }
                files.Add(new(path, new FileInfo(path).Length));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or MagickException or ArgumentException)
            { inaccessible++; }
        }

        foreach (string raw in sources)
        {
            if (limited) break;
            token.ThrowIfCancellationRequested();
            string path;
            try { path = Path.GetFullPath(raw); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
            { inaccessible++; continue; }
            if (File.Exists(path)) { AddFile(path, true); continue; }
            if (!Directory.Exists(path)) { inaccessible++; continue; }
            var folders = new Stack<string>();
            folders.Push(path);
            while (folders.Count > 0 && !limited)
            {
                token.ThrowIfCancellationRequested();
                string folder = folders.Pop();
                if (!seenFolders.Add(folder)) continue;
                try
                {
                    if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) { skipped++; continue; }
                    foreach (string entry in Directory.EnumerateFileSystemEntries(folder))
                    {
                        token.ThrowIfCancellationRequested();
                        if (files.Count >= capacity) { limited = true; break; }
                        FileAttributes attributes;
                        try { attributes = File.GetAttributes(entry); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        { inaccessible++; continue; }
                        if ((attributes & FileAttributes.Directory) == 0) AddFile(entry, false);
                        else if (recursive && (attributes & FileAttributes.ReparsePoint) == 0
                            && !string.Equals(CanonicalDirectory(entry), output, StringComparison.OrdinalIgnoreCase))
                            folders.Push(entry);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { inaccessible++; }
            }
        }
        return new ImportResult(files.ToArray(), skipped, inaccessible, limited);
    }, token);

    private static string? CanonicalDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return null; }
    }
}
