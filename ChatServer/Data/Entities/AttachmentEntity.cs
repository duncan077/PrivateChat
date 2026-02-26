namespace ChatServer.Data.Entities;

public class AttachmentEntity
{
    public Guid Id { get; set; }
    public Guid? MessageId { get; set; }
    public Guid? PrivateMessageId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string Url { get; set; } = string.Empty;
    public MessageEntity? Message { get; set; }
    public PrivateMessageEntity? PrivateMessage { get; set; }
}
