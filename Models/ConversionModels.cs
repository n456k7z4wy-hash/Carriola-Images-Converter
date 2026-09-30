using System;
using System.Globalization;
using ImageMagick;

namespace CarriolaConverter;

public enum JobState { Queued, Reading, Converting, Writing, Completed, Failed, Cancelled }
public enum ResizeMode { Original, LongestSide, Width, Height, Percentage, FitBox }
public enum QueueFilter { All, Pending, Completed, Failed }

public sealed record ExportFormat(string Name, string Extension, MagickFormat Format, bool HasQuality, bool SupportsAlpha);
public sealed record ResizeOptions(ResizeMode Mode = ResizeMode.Original, uint Value = 1920, uint Height = 1080, bool AllowEnlarge = false);
public sealed record ConversionOptions(string Folder, ExportFormat Output, uint Quality, bool Rename,
    ResizeOptions? Resize = null, string Background = "#FFFFFF");
public sealed record WorkItem(Guid Id, string Source, int Number, string? DisplayName = null);
public sealed record ConversionUpdate(Guid Id, JobState State, double Percent, string Message,
    string? OutputPath = null, long? SourceBytes = null, long? OutputBytes = null);
public sealed record ConversionSummary(int Completed, int Failed, int Cancelled);
public sealed record ImageDimensions(uint Width, uint Height);
public sealed record ConversionResult(string Path, long SourceBytes, long OutputBytes);
public sealed record PreviewResult(byte[] BeforePng, byte[] AfterPng, ImageDimensions Before,
    ImageDimensions After, long SourceBytes, long OutputBytes, bool Reduced);
public sealed record ThumbnailResult(byte[] Png, ImageDimensions Dimensions);
public sealed record ImportFile(string Path, long? Size, bool IsTemporary = false);
public sealed record ImportResult(ImportFile[] Files, int Skipped, int Inaccessible, bool ReachedLimit);

public static class ResizeMath
{
    public const ulong MaxPixels = 250_000_000;
    public const uint MaxSide = 65_536;

    public static ImageDimensions Calculate(uint width, uint height, ResizeOptions? options)
    {
        ValidateDimensions(width, height);
        options ??= new ResizeOptions();
        if (options.Mode == ResizeMode.Original) return new(width, height);
        if (!Enum.IsDefined(options.Mode) || options.Value == 0 || options.Height == 0)
            throw new ArgumentException("Informe dimensões maiores que zero.");
        double scale = options.Mode switch
        {
            ResizeMode.LongestSide => options.Value / (double)Math.Max(width, height),
            ResizeMode.Width => options.Value / (double)width,
            ResizeMode.Height => options.Value / (double)height,
            ResizeMode.Percentage => options.Value / 100.0,
            ResizeMode.FitBox => Math.Min(options.Value / (double)width, options.Height / (double)height),
            _ => 1
        };
        if (!options.AllowEnlarge) scale = Math.Min(1, scale);
        double w = Math.Max(1, Math.Round(width * scale));
        double h = Math.Max(1, Math.Round(height * scale));
        if (!double.IsFinite(w) || !double.IsFinite(h) || w > MaxSide || h > MaxSide || w * h > MaxPixels)
            throw new ArgumentException("O tamanho escolhido excede o limite de 250 megapixels ou 65.536 pixels por lado.");
        return new((uint)w, (uint)h);
    }

    public static void ValidateDimensions(uint width, uint height)
    {
        if (width == 0 || height == 0 || width > MaxSide || height > MaxSide || (ulong)width * height > MaxPixels)
            throw new ArgumentException("A imagem tem dimensões inválidas ou excede o limite configurado de 250 megapixels.");
    }
}

public static class FileSizes
{
    public static string Format(long? value)
    {
        if (value is null) return "—";
        double bytes = Math.Max(0, value.Value);
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int unit = 0;
        while (bytes >= 1000 && unit < units.Length - 1) { bytes /= 1000; unit++; }
        return bytes.ToString(unit == 0 ? "0" : "0.#", CultureInfo.CurrentCulture) + " " + units[unit];
    }

    public static string Difference(long? before, long? after)
    {
        if (before is null || after is null || before <= 0) return "";
        double percent = Math.Abs((1 - after.Value / (double)before.Value) * 100);
        if (before == after) return "mesmo tamanho";
        string amount = percent < 0.1 ? "menos de 0,1" : percent.ToString("0.#", CultureInfo.CurrentCulture);
        return amount + (after < before ? "% menor" : "% maior");
    }
}
