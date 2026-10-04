using System.Text.Json;
using FluentAssertions;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Migration;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

public sealed class ActivationAdmissionTests
{
    [Theory]
    [InlineData(MigrationDestinationStatus.Empty, false, null)]
    [InlineData(MigrationDestinationStatus.Empty, true, null)]
    [InlineData(MigrationDestinationStatus.Populated, false, MigrationActivationErrorCodes.ConfirmationRequired)]
    [InlineData(MigrationDestinationStatus.Populated, true, null)]
    public void ReplacementRequiresConfirmationOnlyWhenPopulated(MigrationDestinationStatus destination, bool confirm, string? error)
    {
        Action validate = () => MigrationActivationAdmission.Validate(MigrationDirection.Import,
            MigrationJobState.ReadyToActivate, "r1", new("r1", confirm), destination, "r1");
        AssertOutcome(validate, error);
    }

    [Theory]
    [InlineData("r1", "r2", "r1")]
    [InlineData("r1", "r1", "r2")]
    [InlineData("r1", "r2", "r2")]
    [InlineData("r1", "R1", "r1")]
    [InlineData("", "", "")]
    public void StaleOrEmptyRevision_AlwaysRefusesEvenWithConfirmation(string stored, string requested, string current)
    {
        Action validate = () => MigrationActivationAdmission.ValidateReplacement(stored, requested, true,
            MigrationDestinationStatus.Populated, current);
        AssertOutcome(validate, MigrationActivationErrorCodes.DestinationConflict);
    }

    [Fact]
    public void AdmissionIsImportOnlyAndReadyOnly_AndAuthoritativeRecheckRejectsLaterWrites()
    {
        foreach (var direction in Enum.GetValues<MigrationDirection>())
        foreach (var state in Enum.GetValues<MigrationJobState>())
        {
            Action validate = () => MigrationActivationAdmission.Validate(direction, state, "r1",
                new("r1", true), MigrationDestinationStatus.Populated, "r1");
            AssertOutcome(validate, direction == MigrationDirection.Import && state == MigrationJobState.ReadyToActivate
                ? null : MigrationJobStoreErrorCodes.InvalidState);
        }
        MigrationActivationAdmission.Validate(MigrationDirection.Import, MigrationJobState.ReadyToActivate,
            "r1", new("r1", true), MigrationDestinationStatus.Populated, "r1");
        Action recheck = () => MigrationActivationAdmission.ValidateReplacement("r1", "r1", true,
            MigrationDestinationStatus.Populated, "r2");
        AssertOutcome(recheck, MigrationActivationErrorCodes.DestinationConflict);
    }

    [Fact]
    public void RecoveryConfirmationIsBoundToCurrentRevision()
    {
        Action unconfirmed = () => MigrationActivationAdmission.ValidateRecoveryRestore(new("r1", false), "r1");
        AssertOutcome(unconfirmed, MigrationActivationErrorCodes.ConfirmationRequired);
        Action stale = () => MigrationActivationAdmission.ValidateRecoveryRestore(new("r1", true), "r2");
        AssertOutcome(stale, MigrationActivationErrorCodes.DestinationConflict);
        MigrationActivationAdmission.ValidateRecoveryRestore(new("r1", true), "r1");
    }

    [Fact]
    public void PublicDtos_RoundTrip_AndContainOnlyProviderNeutralFacts()
    {
        var request = new MigrationActivateRequest("opaque-revision", true);
        JsonSerializer.Deserialize<MigrationActivateRequest>(JsonSerializer.Serialize(request)).Should().Be(request);
        var restore = new MigrationRecoveryRestoreRequest("opaque-revision", true);
        JsonSerializer.Deserialize<MigrationRecoveryRestoreRequest>(JsonSerializer.Serialize(restore)).Should().Be(restore);
        var response = new MigrationRecoveryStatusResponse(Guid.NewGuid(), MigrationRecoveryStatus.Available,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(7), 123, new(Books: 2));
        JsonSerializer.Deserialize<MigrationRecoveryStatusResponse>(JsonSerializer.Serialize(response)).Should().Be(response);
        foreach (var type in new[] { typeof(MigrationActivateRequest), typeof(MigrationRecoveryRestoreRequest), typeof(MigrationRecoveryStatusResponse) })
            type.GetProperties().Select(p => p.Name).Should().NotContain(n =>
                n.Contains("Path") || n.Contains("Account") || n.Contains("Provider") || n.Contains("Tenant"));
    }

    private static void AssertOutcome(Action action, string? code)
    {
        if (code is null) action.Should().NotThrow();
        else action.Should().Throw<MigrationActivationException>().Which.Code.Should().Be(code);
    }
}
