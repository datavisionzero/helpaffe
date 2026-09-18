namespace Helpaffe.Application.Attachments;

public interface IAttachmentStorage
{
    Task SaveAsync(string storageKey, Stream content, CancellationToken cancellationToken);
    Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken);
    Task DeleteAsync(string storageKey, CancellationToken cancellationToken);
}
