using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Nostos.Backend.Endpoints;
using Nostos.Backend.Services.Portability.Migration;
using Nostos.Backend.Services.Portability.Transfers;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// Every row of the plan's section 4.4 error table maps to one HTTP status and
/// one stable code. Removing a mapping makes the corresponding case fail.
/// </summary>
public sealed class MigrationHttpErrorMappingTests
{
    public static TheoryData<string, string, int, string> StoreCases => new()
    {
        { MigrationJobStoreErrorCodes.NotFound, "job", 404, "migration_not_found" },
        { MigrationJobStoreErrorCodes.LeaseConflict, "job", 409, "migration_lease_conflict" },
        { MigrationJobStoreErrorCodes.InvalidState, "job", 409, "migration_invalid_state" },
        { MigrationJobStoreErrorCodes.CannotCancel, "job", 409, "migration_cannot_cancel" },
        { MigrationJobStoreErrorCodes.NotRetryable, "job", 409, "migration_not_retryable" },
    };

    public static TheoryData<string, int, string> TransferCases => new()
    {
        { MigrationTransferException.InvalidRequest, 400, "migration_invalid_request" },
        { MigrationTransferException.MetadataRequired, 400, "migration_invalid_request" },
        { MigrationTransferException.InvalidState, 409, "migration_invalid_state" },
        { MigrationTransferException.ReservationRequired, 409, "migration_reservation_required" },
        { MigrationTransferException.Expired, 410, "migration_session_expired" },
        { MigrationTransferException.IdentityMismatch, 409, "migration_file_identity_mismatch" },
        { MigrationTransferException.RangeInvalid, 416, "migration_chunk_range_invalid" },
        { MigrationTransferException.HashMismatch, 422, "migration_chunk_hash_mismatch" },
        { MigrationTransferException.ChunkConflict, 409, "migration_chunk_conflict" },
        { MigrationTransferException.MissingChunks, 409, "migration_invalid_state" },
        { MigrationTransferException.StorageExhausted, 507, "migration_storage_exhausted" },
        { MigrationTransferException.ImportPreparationUnavailable, 409, "migration_import_preparation_unavailable" },
        { MigrationTransferException.ExportArtifactUnavailable, 409, "migration_export_artifact_unavailable" },
    };

    [Theory]
    [MemberData(nameof(StoreCases))]
    public async Task Store_errors_map_to_the_plan_status_and_code(
        string storeCode,
        string _,
        int expectedStatus,
        string expectedCode)
    {
        var exception = new MigrationJobStoreException(storeCode, "internal detail that must not leak");

        var (status, code, message) = await ExecuteAsync(MigrationHttpErrors.From(exception));

        status.Should().Be(expectedStatus);
        code.Should().Be(expectedCode);
        message.Should().NotContain("internal detail");
    }

    [Theory]
    [MemberData(nameof(TransferCases))]
    public async Task Engine_errors_map_to_the_plan_status_and_code(
        string engineCode,
        int expectedStatus,
        string expectedCode)
    {
        var exception = new MigrationTransferException(engineCode, "engine detail that must not leak");

        var (status, code, message) = await ExecuteAsync(MigrationHttpErrors.From(exception));

        status.Should().Be(expectedStatus);
        code.Should().Be(expectedCode);
        message.Should().NotContain("engine detail");
    }

    [Fact]
    public async Task Capacity_reservation_failures_map_to_reservation_required()
    {
        var (status, code, _) = await ExecuteAsync(
            MigrationHttpErrors.From(TransferReservationException.AdmissionContended(3)));

        status.Should().Be(409);
        code.Should().Be("migration_reservation_required");
    }

    [Fact]
    public async Task Unknown_exception_types_fail_closed_without_leaking_detail()
    {
        var (status, code, message) = await ExecuteAsync(
            MigrationHttpErrors.From(new InvalidOperationException(@"C:\secret\path\library.db")));

        status.Should().Be(500);
        code.Should().Be("unexpected_error");
        message.Should().NotContain("secret").And.NotContain("library.db");
    }

    private static async Task<(int Status, string Code, string Message)> ExecuteAsync(IResult result)
    {
        var services = new ServiceCollection();
        services.AddOptions();
        services.AddLogging();
        var context = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
        };
        await using var body = new MemoryStream();
        context.Response.Body = body;
        await result.ExecuteAsync(context);
        body.Position = 0;
        var payload = await JsonSerializer.DeserializeAsync<MigrationErrorResponse>(
            body,
            MigrationHttpHarness.Json);
        payload.Should().NotBeNull();
        return (context.Response.StatusCode, payload!.Error, payload.Message);
    }
}
