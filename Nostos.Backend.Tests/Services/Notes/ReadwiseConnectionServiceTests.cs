using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Notes.Imports;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Services.Notes;

/// <summary>
/// The per-library Readwise connection (issue #1525): the token is checked
/// before storage, saved only as Data-Protection ciphertext, never returned,
/// and the updatedAfter watermark survives a same-token reconnect but resets
/// for a different account.
/// </summary>
public sealed class ReadwiseConnectionServiceTests : IDisposable
{
    private const string Token = "a-real-looking-readwise-token";
    private const string OtherToken = "a-different-readwise-token";

    private readonly SqliteTestFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task A_library_starts_disconnected()
    {
        await using var db = _fixture.CreateContext();

        var status = await Service(db).GetStatusAsync();

        status.Should().Be(new ReadwiseConnectionStatusDto(false, null, null));
    }

    [Fact]
    public async Task Connecting_stores_ciphertext_only_and_never_returns_the_token()
    {
        await using var db = _fixture.CreateContext();
        var service = Service(db);

        var status = await service.ConnectAsync($"  {Token}  ");

        status.Connected.Should().BeTrue();
        var row = await db.ReadwiseConnections.SingleAsync();
        row.TokenEncrypted.Should().NotBe(Token);
        row.TokenEncrypted.Should().NotContain(Token);
        row.TokenEncrypted.Should().NotBeNullOrWhiteSpace();
        (await service.GetTokenAsync()).Should().Be(Token);
        status.ToString().Should().NotContain(Token);
    }

    [Fact]
    public async Task A_rejected_token_is_not_stored()
    {
        await using var db = _fixture.CreateContext();
        var handler = new StubHttpMessageHandler().SetFallback(
            _ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var act = () => Service(db, handler).ConnectAsync(Token);

        await act.Should().ThrowAsync<ReadwiseInvalidTokenException>();
        (await db.ReadwiseConnections.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("two words")]
    public async Task An_obviously_wrong_token_is_refused_before_any_request(string token)
    {
        await using var db = _fixture.CreateContext();
        var handler = new StubHttpMessageHandler();
        var service = Service(db, handler);

        var act = () => service.ConnectAsync(token);

        await act.Should().ThrowAsync<ArgumentException>();
        handler.RecordedRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task Reconnecting_the_same_token_keeps_the_watermark()
    {
        await using var db = _fixture.CreateContext();
        var service = Service(db);
        await service.ConnectAsync(Token);
        var watermark = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        await service.MarkImportedAsync(watermark);

        var status = await service.ConnectAsync(Token);

        status.ImportedThroughUtc.Should().Be(watermark);
        status.LastImportedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task A_different_token_resets_the_watermark()
    {
        await using var db = _fixture.CreateContext();
        var service = Service(db);
        await service.ConnectAsync(Token);
        await service.MarkImportedAsync(new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc));

        var status = await service.ConnectAsync(OtherToken);

        status.ImportedThroughUtc.Should().BeNull();
        status.LastImportedAtUtc.Should().BeNull();
        (await service.GetTokenAsync()).Should().Be(OtherToken);
    }

    [Fact]
    public async Task Marking_an_import_advances_the_watermark()
    {
        await using var db = _fixture.CreateContext();
        var service = Service(db);
        await service.ConnectAsync(Token);
        var watermark = new DateTime(2026, 11, 2, 8, 30, 0, DateTimeKind.Utc);

        await service.MarkImportedAsync(watermark);

        var status = await service.GetStatusAsync();
        status.ImportedThroughUtc.Should().Be(watermark);
        status.LastImportedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task Disconnecting_removes_the_row_and_is_idempotent()
    {
        await using var db = _fixture.CreateContext();
        var service = Service(db);
        await service.ConnectAsync(Token);

        (await service.DisconnectAsync()).Should().BeTrue();
        (await service.DisconnectAsync()).Should().BeFalse();

        (await db.ReadwiseConnections.CountAsync()).Should().Be(0);
        var act = () => service.GetTokenAsync();
        await act.Should().ThrowAsync<ReadwiseNotConnectedException>();
    }

    [Fact]
    public async Task A_token_encrypted_with_another_key_ring_is_a_reconnect_not_a_crash()
    {
        await using var db = _fixture.CreateContext();
        await Service(db, dataProtection: new EphemeralDataProtectionProvider()).ConnectAsync(Token);

        var otherHost = Service(db, dataProtection: new EphemeralDataProtectionProvider());

        var act = () => otherHost.GetTokenAsync();
        var exception = await act.Should().ThrowAsync<ReadwiseNotConnectedException>();
        exception.Which.Message.Should().NotContain(Token);
    }

    [Fact]
    public async Task An_import_after_a_disconnect_does_not_recreate_the_row()
    {
        await using var db = _fixture.CreateContext();
        var service = Service(db);
        await service.ConnectAsync(Token);
        await service.DisconnectAsync();

        await service.MarkImportedAsync(DateTime.UtcNow);

        (await db.ReadwiseConnections.CountAsync()).Should().Be(0);
    }

    private static ReadwiseConnectionService Service(
        NostosDbContext db,
        StubHttpMessageHandler? handler = null,
        IDataProtectionProvider? dataProtection = null)
    {
        handler ??= new StubHttpMessageHandler().SetFallback(
            _ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var api = new ReadwiseApiClient(
            new StubHttpClientFactory(handler),
            Options.Create(new ReadwiseOptions { BaseUrl = "https://readwise.test" }),
            NullLogger<ReadwiseApiClient>.Instance);
        return new ReadwiseConnectionService(
            db,
            api,
            dataProtection ?? new EphemeralDataProtectionProvider(),
            NullLogger<ReadwiseConnectionService>.Instance);
    }
}
