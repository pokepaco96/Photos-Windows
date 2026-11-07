using Microsoft.Extensions.DependencyInjection;
using PhotosWindows.Application.Abstractions;

namespace PhotosWindows.MachineLearning;

public static class ServiceCollectionExtensions
{
    /// <summary>Local face detection and descriptors (ONNX Runtime, CPU). The models are installed by the user.</summary>
    public static IServiceCollection AddPhotosWindowsMachineLearning(this IServiceCollection services)
    {
        services.AddSingleton<FaceModelManager>();
        services.AddSingleton<IFaceModelManager>(sp => sp.GetRequiredService<FaceModelManager>());
        services.AddSingleton<IFaceDetector, YuNetFaceDetector>();
        services.AddSingleton<IFaceEmbedder, SFaceEmbedder>();
        return services;
    }
}
