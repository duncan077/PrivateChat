using ChatServer.Data.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace ChatServer.Data;

public class AppDbContext : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>
{
    public DbSet<ChatRoomEntity> ChatRooms => Set<ChatRoomEntity>();
    public DbSet<ChatRoomMember> ChatRoomMembers => Set<ChatRoomMember>();
    public DbSet<MessageEntity> Messages => Set<MessageEntity>();
    public DbSet<AttachmentEntity> Attachments => Set<AttachmentEntity>();
    public DbSet<PrivateMessageEntity> PrivateMessages => Set<PrivateMessageEntity>();

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<ChatRoomEntity>(e =>
        {
            e.HasKey(r => r.Id);
            e.HasOne(r => r.Owner).WithMany().HasForeignKey(r => r.OwnerId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(r => r.Members).WithOne(m => m.Room).HasForeignKey(m => m.RoomId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(r => r.Messages).WithOne(m => m.Room).HasForeignKey(m => m.RoomId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ChatRoomMember>(e =>
        {
            e.HasKey(m => m.Id);
            e.HasIndex(m => new { m.RoomId, m.UserId }).IsUnique();
            e.HasOne(m => m.User).WithMany(u => u.RoomMemberships).HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<MessageEntity>(e =>
        {
            e.HasKey(m => m.Id);
            e.HasOne(m => m.Sender).WithMany(u => u.Messages).HasForeignKey(m => m.SenderId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(m => m.Attachments).WithOne(a => a.Message).HasForeignKey(a => a.MessageId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<AttachmentEntity>(e =>
        {
            e.HasKey(a => a.Id);
        });

        builder.Entity<PrivateMessageEntity>(e =>
        {
            e.HasKey(m => m.Id);
            e.HasOne(m => m.Sender).WithMany(u => u.SentPrivateMessages).HasForeignKey(m => m.SenderId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(m => m.Recipient).WithMany(u => u.ReceivedPrivateMessages).HasForeignKey(m => m.RecipientId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(m => m.Attachments).WithOne(a => a.PrivateMessage).HasForeignKey(a => a.PrivateMessageId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
