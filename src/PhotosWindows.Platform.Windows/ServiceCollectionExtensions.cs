using Microsoft.Extensions.DependencyInjection;
using PhotosWindows.Application.Abstractions;

namespace PhotosWindows.Platform.Windows;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPhotosWindowsPlatform(this IServiceCollection services)
    {
        services.AddSingleton<IRasterDecoder, WicRasterDecoder>();
        services.AddSingleton<IVideoFrameExtractor, WindowsVideoFrameExtractor>();
        return services;
    }
}
