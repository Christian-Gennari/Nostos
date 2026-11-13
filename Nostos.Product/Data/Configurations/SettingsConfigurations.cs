using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nostos.Backend.Data.Models;

namespace Nostos.Backend.Data.Configurations;

// --- AI PROVIDER SETTINGS (assistant-milestone plan) ---
// A single server-wide settings row. The primary key is fixed and a
// CHECK constraint pins it, so no second row can ever be written. Every
// value column is nullable: NULL means "no override", which is what lets
// an explicit `enabled: false` be told apart from "never set".
//
// Alignment defaults are resolved in the service rather than stored
// as database defaults. Existing rows therefore retain NULL in the
// new columns and transparently use voice STT and multipart transport.
internal sealed class AiProviderSettingsModelConfiguration : IEntityTypeConfiguration<AiProviderSettingsModel>
{
    public void Configure(EntityTypeBuilder<AiProviderSettingsModel> builder)
    {
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_AiProviderSettings_SingletonId",
            $"\"Id\" = {AiProviderSettingsModel.SingletonId}"));

        builder.Property(x => x.AlignmentTranscriptionEnabled)
            .IsRequired(false);
        builder.Property(x => x.AlignmentTranscriptionBaseUrl)
            .IsRequired(false);
        builder.Property(x => x.AlignmentTranscriptionModel)
            .IsRequired(false);
        builder.Property(x => x.AlignmentTranscriptionApiKeyEncrypted)
            .IsRequired(false);
        builder.Property(x => x.AlignmentTranscriptionUseVoiceTranscription)
            .IsRequired(false);
        builder.Property(x => x.AlignmentTranscriptionApiStyle)
            .IsRequired(false);
    }
}

// --- ASSISTANT SETTINGS (issue #262 §7) ---
// The same one-row shape as the provider settings above: a fixed primary
// key pinned by a CHECK constraint, so a second row can never be written.
// The value column is nullable: NULL means "never chosen", which is what
// keeps a stored `verbatim` distinguishable from the default.
internal sealed class AssistantSettingsModelConfiguration : IEntityTypeConfiguration<AssistantSettingsModel>
{
    public void Configure(EntityTypeBuilder<AssistantSettingsModel> builder)
    {
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_AssistantSettings_SingletonId",
            $"\"Id\" = {AssistantSettingsModel.SingletonId}"));
    }
}
