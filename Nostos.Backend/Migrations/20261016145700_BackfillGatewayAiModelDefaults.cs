using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nostos.Backend.Migrations
{
    /// <summary>
    /// The Assistant, Speech and Embedding model defaults used to be names only
    /// particular gateways understand (<c>gemini/…</c>, <c>groq/…</c>,
    /// <c>alibaba/…</c>), and Settings showed them as if the owner had chosen
    /// them. They are now empty. A server whose stored settings already point
    /// a connection at an endpoint or key, but never stored a model, has been
    /// running on that default; this writes it in, so that server keeps doing
    /// exactly what it did. Connections the owner never set up stay empty.
    /// </summary>
    public partial class BackfillGatewayAiModelDefaults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "AiProviderSettings"
                SET "LlmModel" = 'gemini/gemini-3.5-flash-lite'
                WHERE ("LlmModel" IS NULL OR TRIM("LlmModel") = '')
                  AND ((TRIM(COALESCE("LlmBaseUrl", '')) <> '') OR "LlmApiKeyEncrypted" IS NOT NULL);
                """);

            migrationBuilder.Sql("""
                UPDATE "AiProviderSettings"
                SET "SttModel" = 'groq/whisper-large-v3-turbo'
                WHERE ("SttModel" IS NULL OR TRIM("SttModel") = '')
                  AND ((TRIM(COALESCE("SttBaseUrl", '')) <> '') OR "SttApiKeyEncrypted" IS NOT NULL);
                """);

            migrationBuilder.Sql("""
                UPDATE "AiProviderSettings"
                SET "EmbeddingModel" = 'alibaba/qwen3-embedding-0-6b'
                WHERE ("EmbeddingModel" IS NULL OR TRIM("EmbeddingModel") = '')
                  AND ((TRIM(COALESCE("EmbeddingBaseUrl", '')) <> '') OR "EmbeddingApiKeyEncrypted" IS NOT NULL);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to undo. The values written are the defaults the earlier
            // code falls back to, so after a rollback they behave exactly as
            // the missing values did. Clearing them could also erase a model
            // the owner typed in since.
        }
    }
}
