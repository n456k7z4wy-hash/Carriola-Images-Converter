using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ImageMagick;

namespace CarriolaConverter;

internal static class ImagePipeline
{
    private const long MiB = 1024L * 1024;
    private static readonly Lazy<RuntimeState> Runtime = new(CreateRuntime);
    public static int Workers => Runtime.Value.Workers;
    public const uint PreviewSide = 2048;

    private sealed record RuntimeState(int Workers, SemaphoreSlim NativeGate, SemaphoreSlim LargeGate);
    private static RuntimeState CreateRuntime()
    {
        long available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (available <= 0) available = 2L * 1024 * MiB;
        long budget = Math.Clamp(available / 4, 128 * MiB, 2L * 1024 * MiB);
        ResourceLimits.Memory = (ulong)budget;
        ResourceLimits.MaxMemoryRequest = (ulong)Math.Min(budget, 128 * MiB);
        ResourceLimits.Area = (ulong)(budget / 16);
        ResourceLimits.Disk = 8UL * 1024 * 1024 * 1024;
        ResourceLimits.Width = ResizeMath.MaxSide;
        ResourceLimits.Height = ResizeMath.MaxSide;
        ResourceLimits.ListLength = 256;
        ResourceLimits.Thread = 1;
        int workers = Math.Clamp(Math.Min(Environment.ProcessorCount / 2, (int)(budget / (256 * MiB))), 1, 3);
        return new(workers, new SemaphoreSlim(workers, workers), new SemaphoreSlim(1, 1));
    }

    public static IReadOnlyList<ExportFormat> GetFormats()
    {
        var runtime = Runtime.Value;
        ExportFormat[] formats =
        {
            new("WEBP", "webp", MagickFormat.WebP, true, true),
            new("JPG", "jpg", MagickFormat.Jpeg, true, false),
            new("PNG", "png", MagickFormat.Png, false, true),
            new("AVIF", "avif", MagickFormat.Avif, true, true),
            new("TIFF", "tiff", MagickFormat.Tiff, false, true),
            new("BMP", "bmp", MagickFormat.Bmp, false, false),
            new("HEIC", "heic", MagickFormat.Heic, true, false)
        };
        var available = new List<ExportFormat>();
        runtime.NativeGate.Wait();
        try
        {
            foreach (var format in formats)
            {
                if (MagickFormatInfo.Create(format.Format)?.SupportsWriting != true) continue;
                try
                {
                    // AVIF precisa validar também o caminho de qualidade 100.
                    uint[] qualities = format.Format == MagickFormat.Avif ? new[] { 85u, 100u } : new[] { 85u };
                    foreach (uint quality in qualities)
                    {
                        using var image = new MagickImage(MagickColors.White, 16, 16);
                        PrepareOutput(image, new ConversionOptions("", format, quality, false));
                        using var output = new MemoryStream();
                        image.Write(output, format.Format);
                    }
                    available.Add(format);
                }
                catch (MagickException) { }
                catch (NotSupportedException) { }
            }
        }
        finally { runtime.NativeGate.Release(); }
        return available;
    }

    public static MagickReadSettings ReadSettings(string path)
    {
        var settings = new MagickReadSettings { FrameIndex = 0, FrameCount = 1 };
        var info = MagickFormatInfo.Create(path);
        if (info?.SupportsReading == true) settings.Format = info.Format;
        return settings;
    }

    // Sempre adquire os semáforos na mesma ordem: nativo, depois imagem grande.
    public static async Task<IDisposable> AcquireAsync(Stream input, MagickReadSettings settings, CancellationToken token,
        bool forceLarge = false)
    {
        var runtime = Runtime.Value;
        await runtime.NativeGate.WaitAsync(token).ConfigureAwait(false);
        bool large = false;
        try
        {
            using (var probe = new MagickImage())
            {
                probe.Progress += (_, e) => e.Cancel = token.IsCancellationRequested;
                probe.Ping(input, settings);
                token.ThrowIfCancellationRequested();
                ResizeMath.ValidateDimensions(probe.Width, probe.Height);
                if (forceLarge || (ulong)probe.Width * probe.Height > 24_000_000)
                {
                    await runtime.LargeGate.WaitAsync(token).ConfigureAwait(false);
                    large = true;
                }
            }
            input.Position = 0;
            return new Lease(runtime.NativeGate, large ? runtime.LargeGate : null);
        }
        catch
        {
            if (large) runtime.LargeGate.Release();
            runtime.NativeGate.Release();
            throw;
        }
    }

    private sealed class Lease(SemaphoreSlim native, SemaphoreSlim? large) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            large?.Release();
            native.Release();
        }
    }

    public static void Normalize(MagickImage image)
    {
        image.AutoOrient();
        if (image.GetColorProfile() is not null) image.TransformColorSpace(ColorProfiles.SRGB);
        else if (image.ColorSpace == ColorSpace.CMYK) image.ColorSpace = ColorSpace.sRGB;
    }

    public static void PrepareOutput(MagickImage image, ConversionOptions options)
    {
        var dimensions = ResizeMath.Calculate(image.Width, image.Height, options.Resize);
        if (dimensions.Width != image.Width || dimensions.Height != image.Height)
            image.Resize(new MagickGeometry(dimensions.Width, dimensions.Height) { IgnoreAspectRatio = true });
        if (!options.Output.SupportsAlpha)
        {
            image.BackgroundColor = new MagickColor(options.Background);
            image.Alpha(AlphaOption.Remove);
        }
        if (options.Output.Format is MagickFormat.Jpeg or MagickFormat.WebP or MagickFormat.Bmp) image.Depth = 8;
        if (options.Output.HasQuality) image.Quality = options.Quality;
        if (options.Output.Format == MagickFormat.Avif && options.Quality == 100)
        {
            // libheif 1.23.2 + AOM: o ajuste automático IQ conflita com lossless.
            // RGB com matriz identidade usa um caminho compatível, mantendo a qualidade 100.
            // O prefixo é HEIC porque esse coder também escreve AVIF no ImageMagick.
            image.ColorSpace = ColorSpace.sRGB;
            image.ColorType = image.HasAlpha ? ColorType.TrueColorAlpha : ColorType.TrueColor;
            image.Settings.SetDefine(MagickFormat.Heic, "chroma", "444");
            image.Settings.SetDefine(MagickFormat.Heic, "cicp", "1/13/0/1");
        }
        if (options.Output.Format == MagickFormat.Tiff) image.Settings.Compression = CompressionMethod.Zip;
    }

    public static void Validate(ConversionOptions options)
    {
        if (options.Quality is < 1 or > 100) throw new ArgumentException("A qualidade deve ficar entre 1 e 100.");
        if (!IsColor(options.Background)) throw new ArgumentException("Escolha uma cor de fundo válida.");
        if (MagickFormatInfo.Create(options.Output.Format)?.SupportsWriting != true)
            throw new NotSupportedException("O formato de saída não está disponível.");
    }

    public static bool IsColor(string? value)
    {
        if (value is null || value.Length != 7 || value[0] != '#') return false;
        for (int i = 1; i < 7; i++) if (!Uri.IsHexDigit(value[i])) return false;
        return true;
    }

    public static byte[] DisplayPng(MagickImage image, uint maxSide)
    {
        using var display = image.Clone();
        if (Math.Max(display.Width, display.Height) > maxSide)
            display.Resize(new MagickGeometry(maxSide, maxSide));
        display.Depth = 8;
        display.Strip();
        using var memory = new MemoryStream();
        display.Write(memory, MagickFormat.Png);
        return memory.ToArray();
    }

    public static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read,
        FileShare.Read, 64 * 1024, FileOptions.SequentialScan);

    public static async Task<ThumbnailResult> ThumbnailAsync(string path, CancellationToken token)
    {
        return await Task.Run(async () =>
        {
            using var input = OpenRead(path);
            var settings = ReadSettings(path);
            using var lease = await AcquireAsync(input, settings, token).ConfigureAwait(false);
            using var image = new MagickImage();
            image.Progress += (_, e) => e.Cancel = token.IsCancellationRequested;
            image.Read(input, settings);
            token.ThrowIfCancellationRequested();
            Normalize(image);
            var dimensions = new ImageDimensions(image.Width, image.Height);
            var bytes = DisplayPng(image, 128);
            token.ThrowIfCancellationRequested();
            return new ThumbnailResult(bytes, dimensions);
        }, token).ConfigureAwait(false);
    }

    public static async Task<PreviewResult> PreviewAsync(string path, ConversionOptions options,
        string cacheFolder, CancellationToken token)
    {
        return await Task.Run(async () =>
        {
            Validate(options);
            string temporary = Path.Combine(cacheFolder, "preview-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using var input = OpenRead(path);
                long sourceBytes = input.Length;
                var settings = ReadSettings(path);
                using var lease = await AcquireAsync(input, settings, token,
                    options.Resize?.AllowEnlarge == true).ConfigureAwait(false);
                byte[] beforePng;
                ImageDimensions before;
                ImageDimensions after;
                long outputBytes;
                using (var image = new MagickImage())
                {
                    image.Progress += (_, e) => e.Cancel = token.IsCancellationRequested;
                    image.Read(input, settings);
                    token.ThrowIfCancellationRequested();
                    Normalize(image);
                    before = new(image.Width, image.Height);
                    beforePng = DisplayPng(image, PreviewSide);
                    PrepareOutput(image, options);
                    after = new(image.Width, image.Height);
                    token.ThrowIfCancellationRequested();
                    using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    image.Write(output, options.Output.Format);
                    outputBytes = output.Length;
                }
                token.ThrowIfCancellationRequested();
                // A comparação usa o arquivo realmente codificado, inclusive seus artefatos de compressão.
                using var decoded = new MagickImage();
                decoded.Progress += (_, e) => e.Cancel = token.IsCancellationRequested;
                using var encodedInput = OpenRead(temporary);
                decoded.Read(encodedInput, new MagickReadSettings
                    { Format = options.Output.Format, FrameIndex = 0, FrameCount = 1 });
                Normalize(decoded);
                byte[] afterPng = DisplayPng(decoded, PreviewSide);
                token.ThrowIfCancellationRequested();
                bool reduced = Math.Max(before.Width, before.Height) > PreviewSide
                    || Math.Max(after.Width, after.Height) > PreviewSide;
                return new PreviewResult(beforePng, afterPng, before, after, sourceBytes, outputBytes, reduced);
            }
            finally { TryDelete(temporary); }
        }, token).ConfigureAwait(false);
    }

    public static async Task NormalizeClipboardAsync(string source, string destination, CancellationToken token)
    {
        await Task.Run(async () =>
        {
            using var input = OpenRead(source);
            var settings = new MagickReadSettings { FrameCount = 1, FrameIndex = 0 };
            using var lease = await AcquireAsync(input, settings, token).ConfigureAwait(false);
            using var image = new MagickImage();
            image.Progress += (_, e) => e.Cancel = token.IsCancellationRequested;
            image.Read(input, settings);
            token.ThrowIfCancellationRequested();
            Normalize(image);
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            image.Write(output, MagickFormat.Png);
            token.ThrowIfCancellationRequested();
        }, token).ConfigureAwait(false);
    }

    public static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
