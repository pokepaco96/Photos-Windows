using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using PhotosWindows.Application.Abstractions;

namespace PhotosWindows.MachineLearning;

/// <summary>
/// Installs the face models into &lt;app data&gt;/models. The only network access of the whole feature: a plain GET of
/// the fixed model URLs, started by the user. No photo, face, descriptor or identifier is ever sent. A file is used
/// only if its SHA-256 matches the catalog.
/// </summary>
public sealed class FaceModelManager : IFaceModelManager, IDisposable
{
    private readonly string _folder;
    private readonly HttpClient _http;
    private readonly ILogger<FaceModelManager> _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (long Length, DateTime Modified, bool Valid)> _verified = new(StringComparer.Ordinal);

    public FaceModelManager(IAppPaths paths, ILogger<FaceModelManager> logger, HttpMessageHandler? handler = null)
    {
        _folder = Path.Combine(paths.AppDataPath, "models");
        _logger = logger;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromMinutes(10);
        // A generic client name; nothing that identifies the user or the library.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("PhotosWindows/0.1");
    }

    public IReadOnlyList<ModelDescriptor> Models => FaceModelCatalog.All;

    public string Folder => _folder;

    public event EventHandler? Changed;

    public bool AllInstalled => GetStatus().All(s => s.State == ModelState.Installed);

    public IReadOnlyList<ModelStatus> GetStatus() => Models.Select(GetStatus).ToList();

    public ModelStatus GetStatus(ModelDescriptor model)
    {
        var path = Path.Combine(_folder, model.FileName);
        var info = new FileInfo(path);
        if (!info.Exists) return new ModelStatus(model, ModelState.NotInstalled, null);
        lock (_gate)
        {
            if (!_verified.TryGetValue(path, out var cached) || cached.Length != info.Length || cached.Modified != info.LastWriteTimeUtc)
            {
                var valid = info.Length == model.SizeBytes && HashFile(path) == model.Sha256;
                cached = (info.Length, info.LastWriteTimeUtc, valid);
                _verified[path] = cached;
                if (!valid) _logger.LogWarning("Model file {File} does not match its checksum and will not be used.", model.FileName);
            }

            return new ModelStatus(model, cached.Valid ? ModelState.Installed : ModelState.Invalid, cached.Valid ? path : null);
        }
    }

    public async Task InstallAsync(IProgress<(long Done, long Total)>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_folder);
        var missing = GetStatus().Where(s => s.State != ModelState.Installed).Select(s => s.Model).ToList();
        var total = missing.Sum(m => m.SizeBytes);
        long done = 0;
        foreach (var model in missing)
        {
            _logger.LogInformation("Downloading model {Model} ({Bytes} bytes) from {Host}.", model.Id, model.SizeBytes, model.Source.Host);
            var partial = Path.Combine(_folder, model.FileName + ".partial");
            try
            {
                using var response = await _http.GetAsync(model.Source, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                {
                    var buffer = new byte[1 << 16];
                    long written = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        written += read;
                        if (written > model.SizeBytes) throw new InvalidDataException($"The download of {model.FileName} is larger than expected.");
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        progress?.Report((done + written, total));
                    }
                }

                Accept(model, partial);
                done += model.SizeBytes;
            }
            finally
            {
                if (File.Exists(partial)) File.Delete(partial);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<IReadOnlyList<string>> InstallFromFilesAsync(IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_folder);
        var installed = new List<string>();
        foreach (var source in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(source);
            var model = Models.FirstOrDefault(m => m.SizeBytes == info.Length && m.Sha256 == HashFile(source));
            if (model is null)
            {
                _logger.LogWarning("{File} is not one of the supported model files (size or checksum differ).", info.Name);
                continue;
            }

            var partial = Path.Combine(_folder, model.FileName + ".partial");
            try
            {
                await using (var input = File.OpenRead(source))
                await using (var output = File.Create(partial))
                {
                    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                }

                Accept(model, partial);
                installed.Add(model.Id);
            }
            finally
            {
                if (File.Exists(partial)) File.Delete(partial);
            }
        }

        if (installed.Count > 0) Changed?.Invoke(this, EventArgs.Empty);
        return installed;
    }

    public void Uninstall()
    {
        lock (_gate)
        {
            _verified.Clear();
            foreach (var model in Models)
            {
                var path = Path.Combine(_folder, model.FileName);
                if (File.Exists(path)) File.Delete(path);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => _http.Dispose();

    /// <summary>Checks the downloaded/copied file and moves it into place; a mismatch is deleted, never used.</summary>
    private void Accept(ModelDescriptor model, string partial)
    {
        var hash = HashFile(partial);
        if (hash != model.Sha256 || new FileInfo(partial).Length != model.SizeBytes)
        {
            throw new InvalidDataException($"{model.FileName} does not match the expected checksum; it was discarded.");
        }

        var target = Path.Combine(_folder, model.FileName);
        File.Move(partial, target, overwrite: true);
        lock (_gate) _verified.Remove(target);
        _logger.LogInformation("Model {Model} installed and verified.", model.Id);
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
