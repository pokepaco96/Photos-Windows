using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;
using PhotosWindows.Application.Abstractions;

namespace PhotosWindows.Platform.Windows;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPhotosWindowsPlatform(this IServiceCollection services)
    {
        services.AddSingleton<IRasterDecoder, WicRasterDecoder>();
        services.AddSingleton<IVideoFrameExtractor, WindowsVideoFrameExtractor>();
        // Replaces the no-op provider of the core unless a (QA) provider was registered before.
        services.Replace(ServiceDescriptor.Singleton<PhotosWindows.Application.Devices.IPortableMediaDeviceProvider, WinRtPortableDeviceProvider>());
        return services;
    }
}
