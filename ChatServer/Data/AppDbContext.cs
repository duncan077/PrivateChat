using ChatServer.Data.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Shared.Models;

namespace ChatServer.Data;

public class AppDbContext : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>
{
    public DbSet<ChatRoomEntity> ChatRooms => Set<ChatRoomEntity>();
    public DbSet<ChatRoomMember> ChatRoomMembers => Set<ChatRoomMember>();
    public DbSet<MessageEntity> Messages => Set<MessageEntity>();
    public DbSet<AttachmentEntity> Attachments => Set<AttachmentEntity>();
    public DbSet<PrivateMessageEntity> PrivateMessages => Set<PrivateMessageEntity>();
    public DbSet<ReactionEntity> Reactions => Set<ReactionEntity>();
    public DbSet<CustomAssetEntity> CustomAssets => Set<CustomAssetEntity>();

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<ChatRoomEntity>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.Name).HasMaxLength(256);
            e.Property(r => r.Description).HasMaxLength(1024);
            e.HasOne(r => r.Owner).WithMany().HasForeignKey(r => r.OwnerId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(r => r.Members).WithOne(m => m.Room).HasForeignKey(m => m.RoomId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(r => r.Messages).WithOne(m => m.Room).HasForeignKey(m => m.RoomId).OnDelete(DeleteBehavior.Cascade);
        });

        // Bounded strings, so these stop being longtext. Without a length EF maps every
        // string to longtext: unindexable without a prefix, stored off-page once large,
        // and with no length enforced at the database at all - the 64/2048 guards in
        // UpdateProfileAsync were the only thing keeping these small.
        //
        // The limits are deliberately looser than those guards so that converting an
        // existing column cannot truncate a row written before they existed.
        // Message bodies stay longtext on purpose.
        builder.Entity<AppUser>(e =>
        {
            e.Property(u => u.GlobalRole)
                .HasConversion<int>()
                .HasDefaultValue(UserRole.Member);

            e.Property(u => u.DisplayName).HasMaxLength(256);
            e.Property(u => u.AvatarUrl).HasMaxLength(2048);
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

            // Matches the room history query exactly: WHERE RoomId = ? AND IsDeleted = 0
            // ORDER BY SentAt DESC LIMIT n. Without SentAt in the index the server
            // ref-scans every message in the room and filesorts the lot before applying
            // the limit, so the cost grew with room history on every room open.
            // Leading with RoomId means this also serves the plain RoomId lookups that
            // IX_Messages_RoomId used to.
            e.HasIndex(m => new { m.RoomId, m.IsDeleted, m.SentAt });

            // SET NULL, deliberately not RESTRICT. Deleting a room is a hard delete that
            // leans on the Room->Messages cascade, and InnoDB evaluates other FKs per row
            // as that cascade runs - a RESTRICT self-reference would abort the whole room
            // deletion (errno 1451) as soon as one surviving message replied to another.
            e.HasOne(m => m.ReplyTo).WithMany().HasForeignKey(m => m.ReplyToMessageId).OnDelete(DeleteBehavior.SetNull);

            // Backs message search. FULLTEXT on longtext needs no prefix length (that
            // rule is BTREE-only). Adding the first FULLTEXT index to an InnoDB table
            // rebuilds it, so expect the migration to take a moment on a large history.
            //
            // HasColumnType is required, not decorative: indexing a string column makes
            // Pomelo's convention give it a length, and the scaffolder then emits an
            // ALTER to varchar(255) that would truncate every longer message body.
            e.Property(m => m.Content).HasColumnType("longtext");
            e.HasIndex(m => m.Content).IsFullText();
        });

        builder.Entity<AttachmentEntity>(e =>
        {
            e.HasKey(a => a.Id);
            e.Property(a => a.FileName).HasMaxLength(512);
            e.Property(a => a.ContentType).HasMaxLength(256);
            e.Property(a => a.Url).HasMaxLength(2048);
        });

        builder.Entity<PrivateMessageEntity>(e =>
        {
            e.HasKey(m => m.Id);
            e.HasOne(m => m.Sender).WithMany(u => u.SentPrivateMessages).HasForeignKey(m => m.SenderId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(m => m.Recipient).WithMany(u => u.ReceivedPrivateMessages).HasForeignKey(m => m.RecipientId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(m => m.Attachments).WithOne(a => a.PrivateMessage).HasForeignKey(a => a.PrivateMessageId).OnDelete(DeleteBehavior.Cascade);

            // A conversation is the OR of two ordered pairs, so one index cannot serve
            // both halves - hence the mirrored pair. Each covers its half of the
            // predicate and carries SentAt so the ORDER BY ... DESC LIMIT n is satisfied
            // by the index rather than by filesorting the whole conversation.
            // The second also serves MarkPrivateMessageReadAsync's (Recipient, Sender)
            // lookup, and each leads with the column its old single-column index did.
            e.HasIndex(m => new { m.SenderId, m.RecipientId, m.SentAt });
            e.HasIndex(m => new { m.RecipientId, m.SenderId, m.SentAt });

            // Same reasoning as the room-message self-FK; inert in practice because
            // private messages are only ever soft-deleted.
            e.HasOne(m => m.ReplyTo).WithMany().HasForeignKey(m => m.ReplyToMessageId).OnDelete(DeleteBehavior.SetNull);

            // See the room-message index: the explicit column type keeps the indexed
            // body from being narrowed to varchar(255).
            e.Property(m => m.Content).HasColumnType("longtext");
            e.HasIndex(m => m.Content).IsFullText();
        });

        builder.Entity<ReactionEntity>(e =>
        {
            e.HasKey(r => r.Id);

            // utf8mb4_bin is load-bearing, not a preference. Under the server default
            // (utf8mb4_general_ci) every supplementary-plane character compares equal,
            // so 👍 and 🎉 are the "same" value and the unique index below would reject
            // a user's second distinct emoji on a message. A binary collation compares
            // the actual bytes, which is also what we want for :name: tokens.
            e.Property(r => r.Emoji).HasMaxLength(32).UseCollation("utf8mb4_bin");

            // One reaction per (message, user, emoji). The toggle relies on these to
            // make a double-click converge instead of stacking duplicates. Each leads
            // with its parent column, so they also serve the FKs - no extra indexes.
            e.HasIndex(r => new { r.MessageId, r.UserId, r.Emoji }).IsUnique();
            e.HasIndex(r => new { r.PrivateMessageId, r.UserId, r.Emoji }).IsUnique();

            e.HasOne(r => r.Message).WithMany().HasForeignKey(r => r.MessageId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(r => r.PrivateMessage).WithMany().HasForeignKey(r => r.PrivateMessageId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<CustomAssetEntity>(e =>
        {
            e.HasKey(a => a.Id);
            e.Property(a => a.Name).HasMaxLength(24);
            e.Property(a => a.Url).HasMaxLength(2048);
            e.Property(a => a.Kind).HasConversion<int>();

            // One :name: per kind, so a token resolves to exactly one image.
            e.HasIndex(a => new { a.Kind, a.Name }).IsUnique();
        });
    }
}
