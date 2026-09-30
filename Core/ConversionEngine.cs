using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ImageMagick;

namespace CarriolaConverter;

public sealed class ConversionEngine
{
    private static readonly SemaphoreSlim BatchGate = new(1, 1);
    public static IReadOnlyList<ExportFormat> GetFormats() => ImagePipeline.GetFormats();

    public Task<ConversionSummary> RunAsync(IReadOnlyList<WorkItem> jobs, ConversionOptions options,
        IProgress<ConversionUpdate> progress, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(progress);
        if (string.IsNullOrWhiteSpace(options.Folder)) throw new ArgumentException("Escolha uma pasta de destino.");
        var snapshot = jobs.ToArray();
        var batchOptions = options with { Folder = Path.GetFullPath(options.Folder) };
        return Task.Run(async () =>
        {
            var states = new JobState[snapshot.Length];
            bool acquired = false;
            try
            {
                await BatchGate.WaitAsync(token).ConfigureAwait(false);
                acquired = true;
                ImagePipeline.Validate(batchOptions);
                token.ThrowIfCancellationRequested();
                Directory.CreateDirectory(batchOptions.Folder);
                await Parallel.ForEachAsync(Enumerable.Range(0, snapshot.Length), new ParallelOptions
                { MaxDegreeOfParallelism = ImagePipeline.Workers, CancellationToken = token }, async (index, cancellation) =>
                {
                    var job = snapshot[index];
                    var reporter = new Reporter(job.Id, progress);
                    try
                    {
                        var result = await ConvertOneAsync(job, batchOptions, reporter, cancellation).ConfigureAwait(false);
                        states[index] = JobState.Completed;
                        reporter.Send(JobState.Completed, 100, "Concluído", result.Path, result.SourceBytes, result.OutputBytes);
                    }
                    catch (Exception) when (cancellation.IsCancellationRequested)
                    {
                        states[index] = JobState.Cancelled;
                        reporter.Send(JobState.Cancelled, reporter.Percent, "Cancelado");
                    }
                    catch (Exception ex) when (ex is MagickException or IOException or UnauthorizedAccessException
                        or NotSupportedException or ArgumentException or OutOfMemoryException)
                    {
                        states[index] = JobState.Failed;
                        reporter.Send(JobState.Failed, reporter.Percent, DescribeError(ex));
                    }
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            finally
            {
                if (acquired) BatchGate.Release();
                for (int i = 0; i < snapshot.Length; i++)
                {
                    if (states[i] != JobState.Queued) continue;
                    states[i] = token.IsCancellationRequested ? JobState.Cancelled : JobState.Failed;
                    progress.Report(new(snapshot[i].Id, states[i], 0,
                        states[i] == JobState.Cancelled ? "Cancelado" : "Lote interrompido"));
                }
            }
            return new ConversionSummary(states.Count(x => x == JobState.Completed),
                states.Count(x => x == JobState.Failed), states.Count(x => x == JobState.Cancelled));
        });
    }

    private static async Task<ConversionResult> ConvertOneAsync(WorkItem job, ConversionOptions options,
        Reporter reporter, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        reporter.Send(JobState.Reading, 0, "Lendo imagem");
        using var input = ImagePipeline.OpenRead(job.Source);
        reporter.SourceBytes = input.Length;
        var settings = ImagePipeline.ReadSettings(job.Source);
        using var lease = await ImagePipeline.AcquireAsync(input, settings, token,
            options.Resize?.AllowEnlarge == true).ConfigureAwait(false);
        using var image = new MagickImage();
        JobState stage = JobState.Reading;
        double start = 1;
        double span = 29;
        string message = "Lendo imagem";
        image.Progress += (_, e) =>
        {
            e.Cancel = token.IsCancellationRequested;
            double percent = (double)e.Progress;
            if (!double.IsFinite(percent)) return;
            reporter.Send(stage, start + Math.Clamp(percent, 0, 100) / 100 * span, message);
        };
        image.Read(input, settings);
        token.ThrowIfCancellationRequested();
        stage = JobState.Converting; start = 30; span = 19; message = "Preparando imagem";
        reporter.Send(stage, start, message);
        ImagePipeline.Normalize(image);
        ImagePipeline.PrepareOutput(image, options);
        token.ThrowIfCancellationRequested();
        string temporary = Path.Combine(options.Folder, $".carriola-{Guid.NewGuid():N}.tmp");
        bool ownsTemporary = false;
        try
        {
            stage = JobState.Writing; start = 50; span = 49; message = "Salvando imagem";
            reporter.Send(stage, start, message);
            long outputBytes;
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 64 * 1024, FileOptions.SequentialScan))
            {
                ownsTemporary = true;
                image.Write(output, options.Output.Format);
                output.Flush(flushToDisk: true);
                outputBytes = output.Length;
            }
            token.ThrowIfCancellationRequested();
            string final = Commit(temporary, job, options, token);
            ownsTemporary = false;
            return new(final, input.Length, outputBytes);
        }
        finally { if (ownsTemporary) ImagePipeline.TryDelete(temporary); }
    }

    private static string Commit(string temporary, WorkItem job, ConversionOptions options, CancellationToken token)
    {
        string stem = Path.GetFileNameWithoutExtension(job.DisplayName ?? job.Source);
        if (stem.Length > 140) stem = stem[..140];
        stem = stem.TrimEnd(' ', '.');
        if (string.IsNullOrWhiteSpace(stem)) stem = "imagem";
        if (options.Rename) stem += $"_convertida_{job.Number:D2}";
        for (int n = 0; n < 100_000; n++)
        {
            token.ThrowIfCancellationRequested();
            string suffix = n == 0 ? "" : $"_{n:D2}";
            string path = Path.Combine(options.Folder, $"{stem}{suffix}.{options.Output.Extension}");
            try { File.Move(temporary, path, overwrite: false); return path; }
            catch (IOException) when (File.Exists(path) || Directory.Exists(path)) { }
        }
        throw new IOException("Não foi possível encontrar um nome disponível.");
    }

    public static string DescribeError(Exception error) => error switch
    {
        OutOfMemoryException => "Memória insuficiente para converter esta imagem.",
        UnauthorizedAccessException => "Sem permissão para ler ou salvar este arquivo.",
        MagickException => "Não foi possível converter a imagem. " + error.Message,
        _ => error.Message
    };

    private sealed class Reporter(Guid id, IProgress<ConversionUpdate> target)
    {
        private readonly object _sync = new();
        private long _last;
        private double _percent;
        private JobState _state = JobState.Queued;
        public long? SourceBytes { get; set; }
        public double Percent { get { lock (_sync) return _percent; } }
        public void Send(JobState state, double percent, string message, string? output = null,
            long? sourceBytes = null, long? outputBytes = null)
        {
            lock (_sync)
            {
                _percent = Math.Max(_percent, Math.Clamp(percent, 0, 100));
                long now = Stopwatch.GetTimestamp();
                bool terminal = state is JobState.Completed or JobState.Failed or JobState.Cancelled;
                if (!terminal && state == _state && Stopwatch.GetElapsedTime(_last, now).TotalMilliseconds < 100) return;
                _last = now; _state = state;
                target.Report(new(id, state, _percent, message, output, sourceBytes ?? SourceBytes, outputBytes));
            }
        }
    }
}
