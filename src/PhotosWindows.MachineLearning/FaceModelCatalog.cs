using PhotosWindows.Application.Abstractions;

namespace PhotosWindows.MachineLearning;

/// <summary>
/// The face models Photos Windows can use. Sources are pinned to a commit of the official opencv_zoo mirror on
/// Hugging Face and every file must match its SHA-256. Licences and provenance: docs/research/FACE_MODELS_REVIEW.md.
/// </summary>
public static class FaceModelCatalog
{
    public static ModelDescriptor YuNet { get; } = new(
        Id: "yunet-2023mar",
        DisplayName: "YuNet (face detection)",
        FileName: "face_detection_yunet_2023mar.onnx",
        Source: new Uri("https://huggingface.co/opencv/face_detection_yunet/resolve/3cc26e7f1014a5ee5d74a42acee58bafc9d0a310/face_detection_yunet_2023mar.onnx"),
        SizeBytes: 232_589,
        Sha256: "8f2383e4dd3cfbb4553ea8718107fc0423210dc964f9f4280604804ed2552fa4",
        License: "MIT",
        LicenseUrl: new Uri("https://github.com/opencv/opencv_zoo/blob/main/models/face_detection_yunet/LICENSE"),
        Provenance: "Shiqi Yu, OpenCV Model Zoo. Trained on WIDER FACE (dataset annotations licensed CC BY-NC-ND).");

    public static ModelDescriptor SFace { get; } = new(
        Id: "sface-2021dec",
        DisplayName: "SFace (face descriptor for grouping)",
        FileName: "face_recognition_sface_2021dec.onnx",
        Source: new Uri("https://huggingface.co/opencv/face_recognition_sface/resolve/3d7082438a6e4551e840c9b2bb60b71e8da4b524/face_recognition_sface_2021dec.onnx"),
        SizeBytes: 38_696_353,
        Sha256: "0ba9fbfa01b5270c96627c4ef784da859931e02f04419c829e83484087c34e79",
        License: "Apache-2.0",
        LicenseUrl: new Uri("https://github.com/opencv/opencv_zoo/blob/main/models/face_recognition_sface/LICENSE"),
        Provenance: "Yaoyao Zhong, OpenCV Model Zoo. The SFace paper trains on CASIA-WebFace, VGGFace2 and MS-Celeb-1M; the model card does not say which.");

    public static IReadOnlyList<ModelDescriptor> All { get; } = [YuNet, SFace];
}
