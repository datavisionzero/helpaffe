using Helpaffe.Application.Attachments;

namespace Helpaffe.Infrastructure.Attachments;

public sealed class LocalAttachmentStorage(string rootPath) : IAttachmentStorage
{
    private readonly string _rootPath = Path.GetFullPath(
        string.IsNullOrWhiteSpace(rootPath)
            ? throw new ArgumentException("An attachment storage root is required.", nameof(rootPath))
            : rootPath);

    public async Task SaveAsync(string storageKey, Stream content, CancellationToken cancellationToken)
    {
        var target = Resolve(storageKey);
        var directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var output = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await content.CopyToAsync(output, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }
            File.Move(temporary, target, overwrite: false);
        }
        catch
        {
            File.Delete(temporary);
            throw;
        }
    }

    public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Stream stream = new FileStream(
            Resolve(storageKey),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(Resolve(storageKey));
        return Task.CompletedTask;
    }

    private string Resolve(string storageKey)
    {
        if (storageKey.Length != 64 || storageKey.Any(value => !char.IsAsciiHexDigit(value)))
            throw new ArgumentException("The attachment storage key is invalid.", nameof(storageKey));
        var normalized = storageKey.ToLowerInvariant();
        var path = Path.GetFullPath(Path.Combine(_rootPath, normalized[..2], normalized[2..4], normalized));
        if (!path.StartsWith(_rootPath + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("The attachment path escapes the configured storage root.");
        return path;
    }
}
