using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Heartbeat.Collection.Hub.Collectors.Packages;

namespace Heartbeat.Collection.Headless.Tests;

/// <summary>Offline HTTP registry; production download, validation and installation still run.</summary>
internal sealed class PackageRegistryHandler : HttpMessageHandler
{
    private readonly Dictionary<string, byte[]> _responses;
    private readonly string _artifactPath;
    public bool HoldDownload { get; set; }
    public TaskCompletionSource DownloadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource AllowDownload { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PackageRegistryHandler(string source)
    {
        var package = LocalCollectorPackage.Load(source).Manifest;
        var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
        var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        using var archive = new MemoryStream();
        ZipFile.CreateFromDirectory(source, archive);
        var bytes = archive.ToArray();
        var releasePath = $"/v1/packages/{package.PackageId}/versions/{package.Version}/{os}-{arch}/release.json";
        var fileName = $"{package.PackageId}-{package.Version}-{os}-{arch}.zip";
        _artifactPath = releasePath.Replace("release.json", fileName);
        _responses = new()
        {
            ["/v1/catalog.json"] = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1,
                packages = new[] { new {
                    packageId = package.PackageId,
                    displayName = package.Presentation!.DisplayName,
                    summary = package.Presentation.Summary,
                    latest = new[] { new {
                        version = package.Version, target = new { os, arch },
                        releaseUrl = "https://registry.example.invalid" + releasePath
                    } }
                } }
            }),
            [releasePath] = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1, packageId = package.PackageId, version = package.Version,
                target = new { os, arch },
                artifact = new {
                    fileName, url = "https://registry.example.invalid" + _artifactPath,
                    length = bytes.LongLength, sha256 = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes))
                }
            }),
            [_artifactPath] = bytes
        };
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path == _artifactPath && HoldDownload)
        {
            DownloadEntered.TrySetResult();
            await AllowDownload.Task.WaitAsync(cancellationToken);
        }
        return _responses.TryGetValue(path, out var content)
            ? new(HttpStatusCode.OK) { Content = new ByteArrayContent(content) }
            : new(HttpStatusCode.NotFound);
    }
}
