using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CarriolaConverter;

public sealed class PreferencesStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _revision;
    public PreferencesStore(string folder) => _path = Path.Combine(folder, "preferences.json");

    public async Task<(AppPreferences Value, string? Warning)> LoadAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!File.Exists(_path)) return (new(), null);
            if (new FileInfo(_path).Length > 1_048_576)
                return (new(), "O arquivo de preferências é muito grande. Foram usados os valores iniciais.");
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read,
                16 * 1024, FileOptions.Asynchronous);
            var value = await JsonSerializer.DeserializeAsync(stream, PreferencesJsonContext.Default.AppPreferences);
            return (value ?? new(), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { return (new(), "Não foi possível recuperar as preferências: " + ex.Message); }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(AppPreferences preferences)
    {
        long revision = Interlocked.Increment(ref _revision);
        await _gate.WaitAsync();
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (revision != Volatile.Read(ref _revision)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 16 * 1024, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, preferences, PreferencesJsonContext.Default.AppPreferences);
                await stream.FlushAsync();
            }
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            _gate.Release();
        }
    }
}
