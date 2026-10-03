using System;
using System.ComponentModel.DataAnnotations;

namespace Nostos.Backend.Data.Models;

public enum WritingType
{
  Folder,
  Document
}

public class WritingModel
{
  public Guid Id { get; set; } = Guid.NewGuid();

  [Required]
  public string Name { get; set; } = "Untitled";

  public WritingType Type { get; set; }

  // Content is null for Folders, populated for Documents
  public string? Content { get; set; }

  // --- RECURSIVE "TREE" STRUCTURE ---
  public Guid? ParentId { get; set; }
  public WritingModel? Parent { get; set; }

  public ICollection<WritingModel> Children { get; set; } = new List<WritingModel>();

  // Metadata
  public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
  public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
