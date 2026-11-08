using Microsoft.Extensions.DependencyInjection;
using PhotosWindows.Application.Abstractions;

namespace PhotosWindows.Imaging;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPhotosWindowsImaging(this IServiceCollection services)
    {
        services.AddSingleton<IImageRenderer, SkiaImageRenderer>();
        services.AddSingleton<IPerceptualHasher, PerceptualHasher>();
        services.AddSingleton<PhotosWindows.Application.Places.IMapRenderer, Maps.SkiaMapRenderer>();
        return services;
    }
}
