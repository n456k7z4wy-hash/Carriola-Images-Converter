using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CarriolaConverter;

internal sealed class SessionCache : IDisposable
{
    private readonly FileStream _lock;
    private readonly HashSet<string> _owned = new(StringComparer.OrdinalIgnoreCase);
    public string Folder { get; }

    public SessionCache(string baseFolder)
    {
        Folder = Path.Combine(baseFolder, "Cache", "session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Folder);
        _lock = new FileStream(Path.Combine(Folder, "session.lock"), FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.None);
        _ = Task.Run(() => CleanOldSessions(Path.GetDirectoryName(Folder)!));
    }

    public async Task<ImportFile> ImportBitmapAsync(Stream source, CancellationToken token)
    {
        string id = Guid.NewGuid().ToString("N");
        string raw = Path.Combine(Folder, "clipboard-" + id + ".source");
        string png = Path.Combine(Folder, $"Colada_{DateTime.Now:yyyyMMdd_HHmmss}_{id[..6]}.png");
        try
        {
            await using (var output = new FileStream(raw, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                var buffer = new byte[64 * 1024];
                long total = 0;
                int count;
                while ((count = await source.ReadAsync(buffer.AsMemory(), token)) > 0)
                {
                    total += count;
                    if (total > 1024L * 1024 * 1024) throw new IOException("A imagem copiada excede o limite de 1 GB.");
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                }
            }
            await ImagePipeline.NormalizeClipboardAsync(raw, png, token);
            token.ThrowIfCancellationRequested();
            lock (_owned) _owned.Add(png);
            return new(png, new FileInfo(png).Length, true);
        }
        catch { ImagePipeline.TryDelete(png); throw; }
        finally { ImagePipeline.TryDelete(raw); }
    }

    public void Release(string path)
    {
        lock (_owned)
        {
            if (!_owned.Contains(path)) return;
            ImagePipeline.TryDelete(path);
            if (!File.Exists(path)) _owned.Remove(path);
        }
    }

    public void Dispose()
    {
        _lock.Dispose();
        try { Directory.Delete(Folder, recursive: true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private void CleanOldSessions(string root)
    {
        try
        {
            foreach (string folder in Directory.EnumerateDirectories(root, "session-*"))
            {
                if (folder == Folder || !Guid.TryParseExact(Path.GetFileName(folder)[8..], "N", out _)) continue;
                if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) continue;
                if (Directory.GetLastWriteTimeUtc(folder) > DateTime.UtcNow.AddMinutes(-5)) continue;
                try
                {
                    using (new FileStream(Path.Combine(folder, "session.lock"), FileMode.OpenOrCreate,
                        FileAccess.ReadWrite, FileShare.None)) { }
                    // Somente arquivos diretamente criados em nosso cache; não segue subpastas.
                    foreach (string file in Directory.EnumerateFiles(folder)) File.Delete(file);
                    if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
                }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
