using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
namespace CarriolaConverter;
internal static class Checkerboard
{
    private static Task<BitmapImage>? _bitmap;
    // Pequeno PNG incorporado: dispensa alterar os recursos do projeto.
    private const string Pixels = "iVBORw0KGgoAAAANSUhEUgAAAIAAAACACAIAAABMXPacAAABgklEQVR4nO3XsQ2AQAwEQZdEDfRf1FfwGdLCM44nsLTRzbW5e3P8s37e9tDfvACxFyD2AsRegNgLEHsBYj9fefRUL0DsBYi9ALEXIPYCxF6A2AsQe0Ms9gLEXoDYCxB7AWIvQOwFiL0AsTfEYi9A7AWIvQCxFyD2AsRegNgLEHtDLPYCxF6A2AsQewFiL0DsBYi9ALE3xGIvQOwFiL0AsRcg9gLEXoDYCxB7Qyz2AsRegNgLEHsBYi9A7AWIvQCxN8RiL0DsBYi9ALEXIPYCxF6A2AsQe0Ms9gLEXoDYCxB7AWIvQOwFiL0AsTfEYi9A7AWIvQCxFyD2AsRegNgLEHtDLPYCxF6A2AsQewFiL0DsBYi9ALE3xGIvQOwFiL0AsRcg9gLEXoDYCxB7Qyz2AsRegNgLEHsBYi9A7AWIvQCxN8RiL0DsBYi9ALEXIPYCxF6A2AsQe0Ms9gLEXoDYCxB7AWIvQOwFiL0AsTfEYi9A7AWIvQCxFyD2AsRegNgLEPsF5u/Cd0MBzZAAAAAASUVORK5CYII=";
    public static async Task<ImageBrush> CreateBrushAsync()
    {
        _bitmap ??= BitmapLoader.FromPngAsync(Convert.FromBase64String(Pixels));
        return new ImageBrush { ImageSource = await _bitmap, Stretch = Stretch.UniformToFill };
    }
}
