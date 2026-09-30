using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CarriolaConverter;

internal sealed class ThumbnailService
{
    private const int Capacity = 64;
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly Dictionary<Guid, (CancellationTokenSource Cancel, Task Work)> _running = new();
    private readonly LinkedList<QueueItem> _cache = new();
    private bool _enabled = true;

    // As entradas e imagens são alteradas somente na thread da UI.
    public void Request(QueueItem item)
    {
        if (!_enabled || _running.ContainsKey(item.Id)) return;
        if (item.Thumbnail is not null)
        {
            _cache.Remove(item); _cache.AddLast(item); return;
        }
        if (item.ThumbnailAttempted) return;
        var cancellation = new CancellationTokenSource();
        // Um pequeno adiamento garante que a entrada exista antes de o trabalho completar.
        Task work = LoadAsync(item, cancellation);
        _running.Add(item.Id, (cancellation, work));
    }

    private async Task LoadAsync(QueueItem item, CancellationTokenSource cancellation)
    {
        await Task.Yield();
        bool acquired = false;
        try
        {
            await _one.WaitAsync(cancellation.Token);
            acquired = true;
            var result = await ImagePipeline.ThumbnailAsync(item.Source, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            var bitmap = await BitmapLoader.FromPngAsync(result.Png);
            cancellation.Token.ThrowIfCancellationRequested();
            item.SetThumbnail(bitmap, result.Dimensions);
            item.ThumbnailAttempted = true;
            _cache.Remove(item); _cache.AddLast(item);
            while (_cache.Count > Capacity)
            {
                var oldest = _cache.First!.Value;
                _cache.RemoveFirst();
                oldest.SetThumbnail(null);
                oldest.ThumbnailAttempted = false;
            }
        }
        catch (Exception) when (cancellation.IsCancellationRequested) { }
        catch (Exception) { item.ThumbnailAttempted = true; }
        finally
        {
            if (acquired) _one.Release();
            _running.Remove(item.Id);
            cancellation.Dispose();
        }
    }

    public void Cancel(QueueItem item)
    { if (_running.TryGetValue(item.Id, out var entry)) entry.Cancel.Cancel(); }

    public Task Remove(QueueItem item)
    {
        Task work = _running.TryGetValue(item.Id, out var entry) ? entry.Work : Task.CompletedTask;
        Cancel(item);
        _cache.Remove(item);
        item.SetThumbnail(null);
        item.ThumbnailAttempted = false;
        return work;
    }

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        if (!enabled) foreach (var entry in _running.Values) entry.Cancel.Cancel();
    }

    public async Task StopAsync()
    {
        SetEnabled(false);
        await Task.WhenAll(_running.Values.Select(x => x.Work).ToArray());
        foreach (var item in _cache) item.SetThumbnail(null);
        _cache.Clear();
    }
}
