using System;
using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore; // Required for [Owned]

using Nostos.Shared.Enums;

namespace Nostos.Backend.Data.Models;

public abstract class BookModel
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Where this book is in its life cycle.
    ///
    /// <see cref="BookStatus.Ready"/> is 0 so that every row which already exists
    /// is Ready the instant the column appears — an additive migration with that
    /// default needs no backfill script.
    ///
    /// Acquisition and local-upload flows can create the row before bytes arrive.
    /// UploadPending is reserved for an explicitly expected local file; Ready +
    /// HasFile=false remains a legitimate metadata-only book. Read models must use
    /// lifecycle state rather than inferring incompleteness from file fields.
    /// </summary>
    public BookStatus Status { get; set; } = BookStatus.Ready;

    /// <summary>
    /// Why an import failed, or the lifecycle milestone it reached. Null while Ready.
    /// </summary>
    public string? StatusMessage { get; set; }

    [Required]
    public string Title { get; set; } = string.Empty;

    // We keep Author on the root as it's a primary query field
    public string? Author { get; set; }

    // --- 1. Publication Metadata (Grouped) ---
    public BookMetadata Metadata { get; set; } = new();

    // --- 2. Reading Progress & User State (Grouped) ---
    public ReadingProgress Progress { get; set; } = new();

    // --- 3. File System Info (Grouped) ---
    public FileInfoDetails FileDetails { get; set; } = new();

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Normalized identity fields, maintained ONLY by the library service.
    // Backed by filtered unique indexes (see NostosDbContext).
    public string? NormalizedIsbn { get; set; }
    public string? NormalizedAsin { get; set; }

    // --- RELATIONSHIPS ---
    public Guid WorkId { get; set; }
    public WorkModel? Work { get; set; }

    /// <summary>
    /// Authoritative multi-collection membership. A book may belong to any
    /// number of collections; this is the single source of truth.
    ///
    /// The former nullable <c>CollectionId</c> column has been dropped: it could
    /// only ever hold one value, so it was incapable of representing the model
    /// and had to be kept in sync by hand on every write.
    /// </summary>
    public ICollection<BookCollectionModel> BookCollections { get; set; } = new List<BookCollectionModel>();

    /// <summary>
    /// Set when this book's file was acquired from an external provider rather
    /// than uploaded by hand. One generic navigation, not provider-named
    /// columns, so the core library stays provider-agnostic; at most one
    /// acquisition per book, since a book has exactly one primary file.
    /// </summary>
    public BookAcquisitionModel? Acquisition { get; set; }
}

[Owned]
public class BookMetadata
{
    public string? Subtitle { get; set; }
    public string? Description { get; set; } // The official book blurb
    public string? Editor { get; set; }
    public string? Translator { get; set; }
    public string? Publisher { get; set; }
    public string? PlaceOfPublication { get; set; }
    public string? PublishedDate { get; set; }
    public string? Language { get; set; }
    public string? Categories { get; set; }
    public string? Edition { get; set; }
    public string? Series { get; set; }
    public string? VolumeNumber { get; set; }
}

[Owned]
public class ReadingProgress
{
    public string? LastLocation { get; set; } // CFI, Page, or Timestamp
    public int ProgressPercent { get; set; } = 0;

    [Range(0, 5)]
    public int Rating { get; set; } = 0;
    public bool IsFavorite { get; set; } = false;
    public string? PersonalReview { get; set; }

    public DateTime? LastReadAt { get; set; }
    public DateTime? FinishedAt { get; set; }
}

[Owned]
public class FileInfoDetails
{
    public bool HasFile { get; set; } = false;
    public string? FileName { get; set; }
    public string? CoverFileName { get; set; }

    // Store chapters as a JSON string
    public string? ChaptersJson { get; set; }

    // Store epub.js locations (heavy JSON string) (For instant calculation of progress state, cache basically)
    public string? LocationsJson { get; set; }
}

public class PhysicalBookModel : BookModel
{
    public string? Isbn { get; set; }
    public int? PageCount { get; set; }
}

public class EBookModel : BookModel
{
    public string? Isbn { get; set; }
    public int? PageCount { get; set; }
}

public class AudioBookModel : BookModel
{
    public string? Asin { get; set; }
    public string? Duration { get; set; }
    public string? Narrator { get; set; }
}
