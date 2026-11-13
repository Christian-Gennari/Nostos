using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nostos.Backend.Data.Models;

namespace Nostos.Backend.Data.Configurations;

// --- MCP ACCESS TOKENS (issue #1469) ---
// A token is found by its hash, so the hash is unique. The CHECKs pin
// the bounds the service validates, in SQL both providers accept.
internal sealed class McpAccessTokenModelConfiguration : IEntityTypeConfiguration<McpAccessTokenModel>
{
    public void Configure(EntityTypeBuilder<McpAccessTokenModel> builder)
    {
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Name).IsRequired().HasMaxLength(McpAccessTokenModel.MaxNameLength);
        builder.Property(t => t.TokenHash).IsRequired().HasMaxLength(McpAccessTokenModel.HashLength);
        builder.Property(t => t.Scope).IsRequired().HasMaxLength(16);
        builder.HasIndex(t => t.TokenHash).IsUnique();
        builder.ToTable(t =>
        {
            t.HasCheckConstraint(
                "CK_McpAccessTokens_Name",
                "length(\"Name\") > 0 AND length(\"Name\") <= 80");
            t.HasCheckConstraint(
                "CK_McpAccessTokens_TokenHash",
                "length(\"TokenHash\") = 64");
            t.HasCheckConstraint(
                "CK_McpAccessTokens_Scope",
                "\"Scope\" IN ('read', 'read_write')");
        });
    }
}

// --- MCP OAUTH GRANTS (issue #1470) ---
// Deleting a grant deletes its tokens: that is what revoking an app
// in Settings does, and what reusing a spent refresh token triggers.
internal sealed class McpOAuthGrantModelConfiguration : IEntityTypeConfiguration<McpOAuthGrantModel>
{
    public void Configure(EntityTypeBuilder<McpOAuthGrantModel> builder)
    {
        builder.HasKey(g => g.Id);
        builder.Property(g => g.ClientId).IsRequired().HasMaxLength(McpOAuthGrantModel.MaxClientIdLength);
        builder.Property(g => g.ClientName).IsRequired().HasMaxLength(McpOAuthGrantModel.MaxClientNameLength);
        builder.Property(g => g.Scope).IsRequired().HasMaxLength(16);
        builder.HasMany(g => g.Tokens).WithOne(t => t.Grant!).HasForeignKey(t => t.GrantId).OnDelete(DeleteBehavior.Cascade);
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_McpOAuthGrants_Scope",
            "\"Scope\" IN ('read', 'read_write')"));
    }
}

internal sealed class McpOAuthTokenModelConfiguration : IEntityTypeConfiguration<McpOAuthTokenModel>
{
    public void Configure(EntityTypeBuilder<McpOAuthTokenModel> builder)
    {
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Kind).IsRequired().HasMaxLength(16);
        builder.Property(t => t.TokenHash).IsRequired().HasMaxLength(McpAccessTokenModel.HashLength);
        builder.HasIndex(t => t.TokenHash).IsUnique();
        builder.HasIndex(t => t.GrantId);
        builder.ToTable(t =>
        {
            t.HasCheckConstraint(
                "CK_McpOAuthTokens_Kind",
                "\"Kind\" IN ('access', 'refresh')");
            t.HasCheckConstraint(
                "CK_McpOAuthTokens_TokenHash",
                "length(\"TokenHash\") = 64");
        });
    }
}

// --- CARREL SYNC (WP8) ---
// A device token is found by its hash, so the hash is unique. Pairing
// codes and live file paths are kept unique among their live rows by
// the service (a provider-neutral filtered index is not available on
// both SQLite and PostgreSQL), with indexes for the lookups.
internal sealed class CarrelDeviceModelConfiguration : IEntityTypeConfiguration<CarrelDeviceModel>
{
    public void Configure(EntityTypeBuilder<CarrelDeviceModel> builder)
    {
        builder.HasKey(d => d.Id);
        builder.Property(d => d.OwnerKey).IsRequired().HasMaxLength(200);
        builder.Property(d => d.Name).IsRequired().HasMaxLength(CarrelDeviceModel.MaxNameLength);
        builder.Property(d => d.PublicKey).HasMaxLength(CarrelDeviceModel.MaxPublicKeyLength);
        builder.Property(d => d.TokenHash).IsRequired().HasMaxLength(CarrelDeviceModel.HashLength);
        builder.HasIndex(d => d.TokenHash).IsUnique();
        builder.HasIndex(d => d.OwnerKey);
        builder.ToTable(t =>
        {
            t.HasCheckConstraint(
                "CK_CarrelDevices_Name",
                "length(\"Name\") > 0 AND length(\"Name\") <= 100");
            t.HasCheckConstraint(
                "CK_CarrelDevices_TokenHash",
                "length(\"TokenHash\") = 64");
        });
    }
}

internal sealed class CarrelPairingModelConfiguration : IEntityTypeConfiguration<CarrelPairingModel>
{
    public void Configure(EntityTypeBuilder<CarrelPairingModel> builder)
    {
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Code).IsRequired().HasMaxLength(7);
        builder.Property(p => p.DeviceName).IsRequired().HasMaxLength(CarrelPairingModel.MaxDeviceNameLength);
        builder.Property(p => p.DevicePublicKey).IsRequired().HasMaxLength(CarrelPairingModel.MaxPublicKeyLength);
        builder.Property(p => p.Status).IsRequired().HasMaxLength(16);
        builder.Property(p => p.OwnerKey).HasMaxLength(200);
        builder.Property(p => p.TokenEncrypted).HasMaxLength(4096);
        builder.HasIndex(p => p.Code);
        builder.HasIndex(p => new { p.Status, p.ExpiresAtUtc });
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_CarrelPairings_Status",
            "\"Status\" IN ('pending', 'approved', 'denied', 'expired')"));
    }
}

internal sealed class CarrelFileModelConfiguration : IEntityTypeConfiguration<CarrelFileModel>
{
    public void Configure(EntityTypeBuilder<CarrelFileModel> builder)
    {
        builder.HasKey(f => f.FileId);
        builder.Property(f => f.OwnerKey).IsRequired().HasMaxLength(200);
        builder.Property(f => f.Path).IsRequired().HasMaxLength(CarrelFileModel.MaxPathLength);
        builder.Property(f => f.Hash).IsRequired().HasMaxLength(CarrelFileModel.HashLength);
        builder.HasIndex(f => new { f.OwnerKey, f.DraftId }).IsUnique();
        builder.HasIndex(f => new { f.OwnerKey, f.Cursor });
        builder.HasIndex(f => new { f.OwnerKey, f.Path });
        builder.ToTable(t =>
        {
            t.HasCheckConstraint(
                "CK_CarrelFiles_Hash",
                "length(\"Hash\") = 64");
            t.HasCheckConstraint(
                "CK_CarrelFiles_Rev",
                "\"Rev\" > 0");
        });
    }
}

internal sealed class CarrelLibraryItemModelConfiguration : IEntityTypeConfiguration<CarrelLibraryItemModel>
{
    public void Configure(EntityTypeBuilder<CarrelLibraryItemModel> builder)
    {
        builder.HasKey(i => new { i.OwnerKey, i.NoteId });
        builder.Property(i => i.Signature).IsRequired().HasMaxLength(CarrelLibraryItemModel.HashLength);
        builder.HasIndex(i => new { i.OwnerKey, i.Cursor });
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_CarrelLibraryItems_Signature",
            "length(\"Signature\") = 64"));
    }
}

internal sealed class CarrelSyncStateModelConfiguration : IEntityTypeConfiguration<CarrelSyncStateModel>
{
    public void Configure(EntityTypeBuilder<CarrelSyncStateModel> builder)
    {
        builder.HasKey(s => s.OwnerKey);
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_CarrelSyncStates_FilesCursor", "\"FilesCursor\" >= 0");
            t.HasCheckConstraint("CK_CarrelSyncStates_LibraryCursor", "\"LibraryCursor\" >= 0");
        });
    }
}
