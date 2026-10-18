using Cove.Core.Entities;
using Cove.Core.Entities.Auth;

using Microsoft.EntityFrameworkCore;

namespace EmpPrep.Data;

/// <summary>
/// A named set of videos prepared together. With one video it is a single upload; with several it
/// is a pack — one torrent and one EMP post for all of them.
/// </summary>
public sealed class PrepBatch
{
    public Guid Id { get; set; }
    public int UserId { get; set; }
    public string Name { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    // ── Pack post (used when the batch has more than one video) ──
    /// <summary>What the pack's scenes have in common, written by the user. EMP requires it.</summary>
    public string? Theme { get; set; }
    public string? PostTitleOverride { get; set; }
    public string? TagsOverride { get; set; }
    public string? DescriptionOverride { get; set; }
    /// <summary>The scene whose cover image is the pack's cover.</summary>
    public Guid? CoverItemId { get; set; }

    // ── Pack torrent ──
    /// <summary>The hard-linked folder the pack torrent describes — what the client seeds from.</summary>
    public string? PackFolder { get; set; }
    public string? TorrentPath { get; set; }
    public string? InfoHash { get; set; }
    public long? TorrentTotalBytes { get; set; }
    public int? TorrentPieceLength { get; set; }
    public int? TorrentPieceCount { get; set; }
}

/// <summary>One video inside a batch, plus whatever prep artifacts have been produced for it.</summary>
public sealed class PrepItem
{
    public Guid Id { get; set; }
    public Guid BatchId { get; set; }
    public int VideoId { get; set; }
    public int Position { get; set; }
    public DateTime AddedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>pending | screens | ready | error</summary>
    public string Status { get; set; } = PrepItemStatus.Pending;

    /// <summary>Resolved release name — the torrent's root name once built.</summary>
    public string? ReleaseName { get; set; }

    // Edits the user made in the review pane. Null means "use the value derived from the library",
    // so clearing an override falls back to Cove's metadata rather than to an empty string.
    public string? TitleOverride { get; set; }
    /// <summary>User edit of the EMP upload form's title.</summary>
    public string? PostTitleOverride { get; set; }
    public string? TagsOverride { get; set; }
    public string? DescriptionOverride { get; set; }

    /// <summary>JSON array of {seconds, path, url} for the extracted frames.</summary>
    public string ScreenshotsJson { get; set; } = "[]";

    public string? TorrentPath { get; set; }
    public string? InfoHash { get; set; }
    public long? TorrentTotalBytes { get; set; }
    /// <summary>Piece length the torrent was built with, in bytes.</summary>
    public int? TorrentPieceLength { get; set; }
    public int? TorrentPieceCount { get; set; }
    public string? LastError { get; set; }
}

public static class PrepItemStatus
{
    public const string Pending = "pending";
    public const string Screens = "screens";
    public const string Ready = "ready";
    public const string Error = "error";
}

public static class EmpPrepSchema
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<PrepBatch>(entity =>
        {
            entity.ToTable("empprep_batches");
            entity.HasKey(batch => batch.Id);
            // Batches are per-account: another user's queue is not this user's business, and
            // deleting an account should not leave orphaned prep state behind.
            entity.HasOne<User>().WithMany().HasForeignKey(batch => batch.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.Property(batch => batch.Name).HasMaxLength(200);
            entity.Property(batch => batch.InfoHash).HasMaxLength(40);
            entity.HasIndex(batch => new { batch.UserId, batch.UpdatedAtUtc });
        });

        model.Entity<PrepItem>(entity =>
        {
            entity.ToTable("empprep_items");
            entity.HasKey(item => item.Id);
            entity.HasOne<PrepBatch>().WithMany().HasForeignKey(item => item.BatchId).OnDelete(DeleteBehavior.Cascade);
            // A video removed from the library takes its queue entry with it — the prep artifacts
            // reference a file that no longer exists, so keeping the row would only mislead.
            entity.HasOne<Video>().WithMany().HasForeignKey(item => item.VideoId).OnDelete(DeleteBehavior.Cascade);
            entity.Property(item => item.Status).HasMaxLength(16);
            entity.Property(item => item.InfoHash).HasMaxLength(40);
            entity.HasIndex(item => new { item.BatchId, item.Position });
            // The same video twice in one batch would produce two identical torrents.
            entity.HasIndex(item => new { item.BatchId, item.VideoId }).IsUnique();
        });
    }

    public const string Sql = """
        CREATE TABLE IF NOT EXISTS empprep_batches (
          "Id" uuid PRIMARY KEY,
          "UserId" integer NOT NULL REFERENCES users("Id") ON DELETE CASCADE,
          "Name" varchar(200) NOT NULL,
          "CreatedAtUtc" timestamptz NOT NULL,
          "UpdatedAtUtc" timestamptz NOT NULL);

        CREATE INDEX IF NOT EXISTS "IX_empprep_batches_UserId_UpdatedAtUtc"
          ON empprep_batches ("UserId", "UpdatedAtUtc");

        CREATE TABLE IF NOT EXISTS empprep_items (
          "Id" uuid PRIMARY KEY,
          "BatchId" uuid NOT NULL REFERENCES empprep_batches("Id") ON DELETE CASCADE,
          "VideoId" integer NOT NULL REFERENCES videos("Id") ON DELETE CASCADE,
          "Position" integer NOT NULL,
          "AddedAtUtc" timestamptz NOT NULL,
          "UpdatedAtUtc" timestamptz NOT NULL,
          "Status" varchar(16) NOT NULL
            CHECK ("Status" IN ('pending','screens','ready','error')),
          "ReleaseName" text,
          "TitleOverride" text,
          "TagsOverride" text,
          "DescriptionOverride" text,
          "ScreenshotsJson" text NOT NULL DEFAULT '[]',
          "TorrentPath" text,
          "InfoHash" varchar(40),
          "TorrentTotalBytes" bigint,
          "LastError" text);

        CREATE INDEX IF NOT EXISTS "IX_empprep_items_BatchId_Position"
          ON empprep_items ("BatchId", "Position");

        CREATE UNIQUE INDEX IF NOT EXISTS "IX_empprep_items_BatchId_VideoId"
          ON empprep_items ("BatchId", "VideoId");
        """;

    /// <summary>Turns a batch into a pack: its post edits and its torrent.</summary>
    public const string PackSql = """
        ALTER TABLE empprep_batches ADD COLUMN IF NOT EXISTS "Theme" text;
        ALTER TABLE empprep_batches ADD COLUMN IF NOT EXISTS "PostTitleOverride" text;
        ALTER TABLE empprep_batches ADD COLUMN IF NOT EXISTS "TagsOverride" text;
        ALTER TABLE empprep_batches ADD COLUMN IF NOT EXISTS "DescriptionOverride" text;
        ALTER TABLE empprep_batches ADD COLUMN IF NOT EXISTS "CoverItemId" uuid;
        ALTER TABLE empprep_batches ADD COLUMN IF NOT EXISTS "PackFolder" text;
        ALTER TABLE empprep_batches ADD COLUMN IF NOT EXISTS "TorrentPath" text;
        ALTER TABLE empprep_batches ADD COLUMN IF NOT EXISTS "InfoHash" varchar(40);
        ALTER TABLE empprep_batches ADD COLUMN IF NOT EXISTS "TorrentTotalBytes" bigint;
        ALTER TABLE empprep_batches ADD COLUMN IF NOT EXISTS "TorrentPieceLength" integer;
        ALTER TABLE empprep_batches ADD COLUMN IF NOT EXISTS "TorrentPieceCount" integer;
        """;

    /// <summary>Stores the user's edit of the EMP upload form's title.</summary>
    public const string PostTitleSql = """
        ALTER TABLE empprep_items ADD COLUMN IF NOT EXISTS "PostTitleOverride" text;
        """;

    /// <summary>Records the piece size a torrent was built with, so the page can show it.</summary>
    public const string PieceInfoSql = """
        ALTER TABLE empprep_items ADD COLUMN IF NOT EXISTS "TorrentPieceLength" integer;
        ALTER TABLE empprep_items ADD COLUMN IF NOT EXISTS "TorrentPieceCount" integer;
        """;
}
