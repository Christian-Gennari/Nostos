using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Stylos;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Endpoints;

/// <summary>
/// Full-host HTTP coverage for the Stylos Sync Protocol v1 (WP8): pairing,
/// device tokens, the file feed against Writing Studio drafts, conflicts,
/// deletions, cursors and the library feed.
/// </summary>
public sealed class StylosSyncEndpointTests : IClassFixture<LibraryEndpointFactory>
{
    private readonly LibraryEndpointFactory _factory;

    public StylosSyncEndpointTests(LibraryEndpointFactory factory) => _factory = factory;

    private HttpClient Client => _factory.CreateClient();

    // ------------------------------------------------------------------
    // Pairing and device tokens
    // ------------------------------------------------------------------

    [Fact]
    public async Task Pair_start_returns_a_code_and_pending_status_omits_secrets()
    {
        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var response = await Client.PostAsJsonAsync(
            "/stylos/v1/pair/start",
            new { device_name = "  X250 ", device_pubkey = new string('a', 64) });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;
        root.GetProperty("code").GetString().Should().MatchRegex(
            "^[23456789ABCDEFGHJKMNPQRSTVWXYZ]{3}-[23456789ABCDEFGHJKMNPQRSTVWXYZ]{3}$");
        var expiresAt = root.GetProperty("expires_at").GetInt64();
        expiresAt.Should().BeInRange(before + 590, before + 610);
        var pairId = Guid.Parse(root.GetProperty("pair_id").GetString()!);
        pairId.Should().NotBe(Guid.Empty);

        var pending = await Client.GetAsync($"/stylos/v1/pair/{pairId}");
        pending.StatusCode.Should().Be(HttpStatusCode.OK);
        using var pendingBody = JsonDocument.Parse(await pending.Content.ReadAsStringAsync());
        pendingBody.RootElement.GetProperty("status").GetString().Should().Be("pending");
        pendingBody.RootElement.TryGetProperty("token", out _).Should().BeFalse(
            "a pending pairing has no token to leak");
        pendingBody.RootElement.TryGetProperty("server_cert_sha256", out _).Should().BeFalse();

        var lookup = await Client.GetAsync($"/api/stylos/pairing/{root.GetProperty("code").GetString()}");
        lookup.StatusCode.Should().Be(HttpStatusCode.OK);
        using var lookupBody = JsonDocument.Parse(await lookup.Content.ReadAsStringAsync());
        lookupBody.RootElement.GetProperty("deviceName").GetString().Should().Be("X250");
    }

    [Fact]
    public async Task Approve_issues_a_token_the_device_collects_and_lists_the_device()
    {
        var pair = await StartPairingAsync("Approved Device");
        var token = await ApproveAndCollectAsync(pair);

        token.Should().StartWith("stl_").And.HaveLength(47);

        var listed = await Client.GetFromJsonAsync<JsonElement>("/api/stylos/devices");
        var listedDevice = listed.GetProperty("devices").EnumerateArray()
            .Single(d => d.GetProperty("name").GetString() == "Approved Device");
        listedDevice.GetProperty("createdAt").GetDateTime().Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(2));

        // After one sync the list says when it was last used.
        (await AuthedClient(token).GetAsync("/stylos/v1/files")).EnsureSuccessStatusCode();
        var used = await Client.GetFromJsonAsync<JsonElement>("/api/stylos/devices");
        used.GetProperty("devices").EnumerateArray()
            .Single(d => d.GetProperty("name").GetString() == "Approved Device")
            .GetProperty("lastUsedAt").GetDateTime().Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(2));

        // Only a hash is stored; the token itself never appears in the database.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NostosDbContext>();
        var device = await db.StylosDevices.AsNoTracking()
            .SingleAsync(d => d.Name == "Approved Device");
        device.TokenHash.Should().HaveLength(64).And.NotContain(token);
        device.TokenHash.Should().Be(
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token))));

        var authed = AuthedClient(token);
        (await authed.GetAsync("/stylos/v1/files?since=0")).StatusCode.Should().Be(HttpStatusCode.OK);

        // The pairing row keeps the token as ciphertext, not plaintext.
        var pairing = await db.StylosPairings.AsNoTracking().SingleAsync(p => p.Code == pair.Code);
        pairing.Status.Should().Be(StylosPairingStatus.Approved);
        pairing.DeviceId.Should().Be(device.Id);
        pairing.TokenEncrypted.Should().NotBeNull().And.NotContain(token);
    }

    [Fact]
    public async Task A_pairing_must_be_approved_with_a_real_waiting_code()
    {
        (await Client.GetAsync("/api/stylos/pairing/ZZZ-ZZZ")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
        (await Client.PostAsync("/api/stylos/pairing/ZZZ-ZZZ/approve", null)).StatusCode
            .Should().Be(HttpStatusCode.NotFound);

        // Symbols the alphabet excludes (0, 1, I, L, O, U) are not a code.
        (await Client.GetAsync("/api/stylos/pairing/0AB-CDE")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
        (await Client.PostAsync("/api/stylos/pairing/0AB-CDE/approve", null)).StatusCode
            .Should().Be(HttpStatusCode.NotFound);

        var missing = Guid.NewGuid();
        (await Client.GetAsync($"/stylos/v1/pair/{missing}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Denying_a_code_reports_expired_and_creates_no_device()
    {
        var pair = await StartPairingAsync("Refused Device");

        var denied = await Client.PostAsync($"/api/stylos/pairing/{pair.Code}/deny", null);
        denied.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var status = await Client.GetFromJsonAsync<JsonElement>($"/stylos/v1/pair/{pair.PairId}");
        status.GetProperty("status").GetString().Should().Be("expired");
        status.TryGetProperty("token", out _).Should().BeFalse();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NostosDbContext>();
        (await db.StylosDevices.AnyAsync(d => d.Name == "Refused Device")).Should().BeFalse();
    }

    [Fact]
    public async Task An_unapproved_code_past_its_time_reports_expired()
    {
        var pair = await StartPairingAsync("Slow Device");

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NostosDbContext>();
            var row = await db.StylosPairings.SingleAsync(p => p.Id == pair.PairId);
            row.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        var status = await Client.GetFromJsonAsync<JsonElement>($"/stylos/v1/pair/{pair.PairId}");
        status.GetProperty("status").GetString().Should().Be("expired");
        (await Client.PostAsync($"/api/stylos/pairing/{pair.Code}/approve", null)).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Revoking_a_device_refuses_its_token_on_the_next_request()
    {
        var pair = await StartPairingAsync("Revoked Device");
        var token = await ApproveAndCollectAsync(pair);
        var authed = AuthedClient(token);
        (await authed.GetAsync("/stylos/v1/files")).StatusCode.Should().Be(HttpStatusCode.OK);

        var devices = await Client.GetFromJsonAsync<JsonElement>("/api/stylos/devices");
        var device = devices.GetProperty("devices").EnumerateArray()
            .First(d => d.GetProperty("name").GetString() == "Revoked Device");
        var id = device.GetProperty("id").GetGuid();

        var revoked = await Client.DeleteAsync($"/api/stylos/devices/{id}");
        revoked.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var refused = await authed.GetAsync("/stylos/v1/files");
        refused.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var body = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("error").GetString().Should().Contain("token");

        (await Client.DeleteAsync($"/api/stylos/devices/{id}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_approval_link_the_device_puts_in_its_QR_lands_on_the_pair_page()
    {
        // The redirect is the answer; following it would ask for the SPA shell,
        // which a test host does not serve.
        var client = _factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
            });

        var response = await client.GetAsync("/approve?code=K7Q-4MZ");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be("/pair?code=K7Q-4MZ");

        // Without a code the page still opens, so it can be typed there.
        var bare = await client.GetAsync("/approve");
        bare.StatusCode.Should().Be(HttpStatusCode.Redirect);
        bare.Headers.Location!.OriginalString.Should().Be("/pair");
    }

    [Fact]
    public async Task The_host_advertises_the_feature_so_Settings_can_show_Devices()
    {
        var capabilities = await Client.GetFromJsonAsync<JsonElement>("/api/runtime/capabilities");
        capabilities.GetProperty("supportsStylosDevices").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Every_file_and_library_route_refuses_a_missing_or_bogus_token()
    {
        var fileId = Guid.NewGuid();
        var requests = new (HttpMethod Method, string Url)[]
        {
            (HttpMethod.Get, "/stylos/v1/files"),
            (HttpMethod.Get, $"/stylos/v1/files/{fileId}"),
            (HttpMethod.Put, $"/stylos/v1/files/{fileId}"),
            (HttpMethod.Delete, $"/stylos/v1/files/{fileId}?base_rev=1"),
            (HttpMethod.Get, "/stylos/v1/library"),
        };

        foreach (var (method, url) in requests)
        {
            using var request = new HttpRequestMessage(method, url);
            if (method == HttpMethod.Put)
                request.Content = JsonContent.Create(new { path = "a.md", content = "x", base_rev = (long?)null });
            var anonymous = await Client.SendAsync(request);
            anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"{method} {url} without a token");

            using var bogus = new HttpRequestMessage(method, url);
            if (method == HttpMethod.Put)
                bogus.Content = JsonContent.Create(new { path = "a.md", content = "x", base_rev = (long?)null });
            bogus.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "stl_" + new string('x', 43));
            (await Client.SendAsync(bogus)).StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                $"{method} {url} with an unknown token");
        }
    }

    // ------------------------------------------------------------------
    // Files
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_new_file_becomes_a_nested_studio_draft_with_its_markdown_intact()
    {
        var token = await PairedTokenAsync();
        var client = AuthedClient(token);
        var fileId = Guid.NewGuid();
        var content = "## November\n\nA quote: [see this](nostos:cite/0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b?as=quote&src=0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5c).\n";

        var put = await client.PutAsJsonAsync($"/stylos/v1/files/{fileId}", new
        {
            path = "notes/2026/november.md",
            content,
            base_rev = (long?)null,
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        var success = (await put.Content.ReadFromJsonAsync<StylosPutResponse>())!;
        success.Rev.Should().Be(1);
        success.Hash.Should().Be(Sha256(content));

        var list = await client.GetFromJsonAsync<JsonElement>("/stylos/v1/files?since=0");
        var entry = list.GetProperty("files").EnumerateArray()
            .Single(f => f.GetProperty("file_id").GetGuid() == fileId);
        entry.GetProperty("path").GetString().Should().Be("notes/2026/november.md");
        entry.GetProperty("hash").GetString().Should().Be(Sha256(content));
        entry.GetProperty("rev").GetInt64().Should().Be(1);
        entry.GetProperty("deleted").GetBoolean().Should().BeFalse();
        entry.GetProperty("modified_at").GetInt64().Should().BeGreaterThan(0);

        var read = await client.GetFromJsonAsync<JsonElement>($"/stylos/v1/files/{fileId}");
        read.GetProperty("content").GetString().Should().Be(content);
        read.GetProperty("rev").GetInt64().Should().Be(1);
        read.GetProperty("hash").GetString().Should().Be(Sha256(content));

        // The Studio sees one document called "november" inside notes/2026.
        var writings = (await Client.GetFromJsonAsync<WritingDto[]>("/api/writings"))!;
        var draft = writings.Single(w => w.Name == "november");
        draft.Type.Should().Be("Document");
        var year = writings.Single(w => w.Id == draft.ParentId);
        year.Name.Should().Be("2026");
        var notes = writings.Single(w => w.Id == year.ParentId);
        notes.Name.Should().Be("notes");

        var stored = await Client.GetFromJsonAsync<WritingContentDto>($"/api/writings/{draft.Id}");
        stored!.Content.Should().Be(content, "content stays Markdown and nostos:cite links stay as written");
    }

    [Fact]
    public async Task A_draft_edited_in_the_studio_bumps_the_revision_the_device_pulls()
    {
        var token = await PairedTokenAsync();
        var client = AuthedClient(token);
        var fileId = Guid.NewGuid();
        await PutNewFileAsync(client, fileId, "studio-edit.md", "first");

        var before = await CursorAsync(client);
        var writings = await Client.GetFromJsonAsync<WritingDto[]>("/api/writings");
        var draft = writings!.Single(w => w.Name == "studio-edit");

        var edited = await Client.PutAsJsonAsync($"/api/writings/{draft.Id}", new
        {
            name = "studio-edit",
            content = "second, written in the Studio",
        });
        edited.StatusCode.Should().Be(HttpStatusCode.OK);

        var list = await client.GetFromJsonAsync<JsonElement>($"/stylos/v1/files?since={before}");
        var entry = list.GetProperty("files").EnumerateArray()
            .Single(f => f.GetProperty("file_id").GetGuid() == fileId);
        entry.GetProperty("rev").GetInt64().Should().Be(2);
        entry.GetProperty("hash").GetString().Should().Be(Sha256("second, written in the Studio"));

        var read = await client.GetFromJsonAsync<JsonElement>($"/stylos/v1/files/{fileId}");
        read.GetProperty("content").GetString().Should().Be("second, written in the Studio");
        read.GetProperty("rev").GetInt64().Should().Be(2);
    }

    [Fact]
    public async Task A_draft_renamed_in_the_studio_moves_the_file_path()
    {
        var token = await PairedTokenAsync();
        var client = AuthedClient(token);
        var fileId = Guid.NewGuid();
        await PutNewFileAsync(client, fileId, "before-rename.md", "text");
        var before = await CursorAsync(client);

        var writings = await Client.GetFromJsonAsync<WritingDto[]>("/api/writings");
        var draft = writings!.Single(w => w.Name == "before-rename");
        (await Client.PutAsJsonAsync($"/api/writings/{draft.Id}", new
        {
            name = "after-rename",
            content = "text",
        })).EnsureSuccessStatusCode();

        var list = await client.GetFromJsonAsync<JsonElement>($"/stylos/v1/files?since={before}");
        var entry = list.GetProperty("files").EnumerateArray()
            .Single(f => f.GetProperty("file_id").GetGuid() == fileId);
        entry.GetProperty("path").GetString().Should().Be("after-rename.md");
        entry.GetProperty("rev").GetInt64().Should().Be(2);
        entry.GetProperty("hash").GetString().Should().Be(Sha256("text"));
    }

    [Fact]
    public async Task A_stale_base_rev_is_a_conflict_and_the_server_changes_nothing()
    {
        var token = await PairedTokenAsync();
        var client = AuthedClient(token);
        var fileId = Guid.NewGuid();
        await PutNewFileAsync(client, fileId, "conflict.md", "server version");
        var before = await CursorAsync(client);

        var conflict = await client.PutAsJsonAsync($"/stylos/v1/files/{fileId}", new
        {
            path = "conflict.md",
            content = "device version",
            base_rev = 0,
        });

        conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var body = JsonDocument.Parse(await conflict.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("conflict").GetProperty("server_rev").GetInt64().Should().Be(1);
        body.RootElement.GetProperty("conflict").GetProperty("server_hash").GetString()
            .Should().Be(Sha256("server version"));
        body.RootElement.GetProperty("conflict").TryGetProperty("server_file_id", out _).Should().BeFalse();

        var read = await client.GetFromJsonAsync<JsonElement>($"/stylos/v1/files/{fileId}");
        read.GetProperty("content").GetString().Should().Be("server version");
        read.GetProperty("rev").GetInt64().Should().Be(1);

        var after = await client.GetFromJsonAsync<JsonElement>($"/stylos/v1/files?since={before}");
        after.GetProperty("files").GetArrayLength().Should().Be(0,
            "a refused write must not move the file or the cursor");
    }

    [Fact]
    public async Task A_new_file_id_with_an_existing_base_rev_reports_the_servers_rev_zero()
    {
        var token = await PairedTokenAsync();
        var client = AuthedClient(token);

        var conflict = await client.PutAsJsonAsync($"/stylos/v1/files/{Guid.NewGuid()}", new
        {
            path = "unknown-to-server.md",
            content = "text",
            base_rev = 3,
        });

        conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var body = JsonDocument.Parse(await conflict.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("conflict").GetProperty("server_rev").GetInt64().Should().Be(0);
        body.RootElement.GetProperty("conflict").GetProperty("server_hash").GetString().Should().BeEmpty();
        body.RootElement.GetProperty("conflict").TryGetProperty("server_file_id", out _).Should().BeFalse();
    }

    [Fact]
    public async Task A_null_base_rev_for_a_file_that_exists_is_a_conflict()
    {
        var token = await PairedTokenAsync();
        var client = AuthedClient(token);
        var fileId = Guid.NewGuid();
        await PutNewFileAsync(client, fileId, "exists.md", "one");

        var conflict = await client.PutAsJsonAsync($"/stylos/v1/files/{fileId}", new
        {
            path = "exists.md",
            content = "two",
            base_rev = (long?)null,
        });

        conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var body = JsonDocument.Parse(await conflict.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("conflict").GetProperty("server_rev").GetInt64().Should().Be(1);
    }

    [Fact]
    public async Task A_path_collision_names_the_file_that_owns_the_path()
    {
        var token = await PairedTokenAsync();
        var client = AuthedClient(token);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await PutNewFileAsync(client, first, "shared-path.md", "first file");

        var conflict = await client.PutAsJsonAsync($"/stylos/v1/files/{second}", new
        {
            path = "shared-path.md",
            content = "second file",
            base_rev = (long?)null,
        });

        conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var body = JsonDocument.Parse(await conflict.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("conflict").GetProperty("server_file_id").GetGuid()
            .Should().Be(first);
        body.RootElement.GetProperty("conflict").GetProperty("server_rev").GetInt64().Should().Be(1);

        var read = await client.GetFromJsonAsync<JsonElement>($"/stylos/v1/files/{first}");
        read.GetProperty("content").GetString().Should().Be("first file");
        (await client.GetAsync($"/stylos/v1/files/{second}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_needs_the_current_revision_then_leaves_a_tombstone()
    {
        var token = await PairedTokenAsync();
        var client = AuthedClient(token);
        var fileId = Guid.NewGuid();
        await PutNewFileAsync(client, fileId, "to-delete.md", "goes away");
        var before = await CursorAsync(client);

        var stale = await client.DeleteAsync($"/stylos/v1/files/{fileId}?base_rev=9");
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var staleBody = JsonDocument.Parse(await stale.Content.ReadAsStringAsync());
        staleBody.RootElement.GetProperty("conflict").GetProperty("server_rev").GetInt64().Should().Be(1);
        (await client.GetAsync($"/stylos/v1/files/{fileId}")).StatusCode.Should().Be(HttpStatusCode.OK);

        var deleted = await client.DeleteAsync($"/stylos/v1/files/{fileId}?base_rev=1");
        deleted.StatusCode.Should().Be(HttpStatusCode.OK);
        (await deleted.Content.ReadFromJsonAsync<StylosDeleteResponse>())!.Rev.Should().Be(2);

        var list = await client.GetFromJsonAsync<JsonElement>($"/stylos/v1/files?since={before}");
        var entry = list.GetProperty("files").EnumerateArray()
            .Single(f => f.GetProperty("file_id").GetGuid() == fileId);
        entry.GetProperty("deleted").GetBoolean().Should().BeTrue();
        entry.GetProperty("rev").GetInt64().Should().Be(2);
        entry.GetProperty("path").GetString().Should().Be("to-delete.md");

        (await client.GetAsync($"/stylos/v1/files/{fileId}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);

        // The draft is gone from the Studio too; keeping it would resurrect the file.
        var writings = await Client.GetFromJsonAsync<WritingDto[]>("/api/writings");
        writings!.Should().NotContain(w => w.Name == "to-delete");

        // A retried delete is idempotent.
        var again = await client.DeleteAsync($"/stylos/v1/files/{fileId}?base_rev=2");
        again.StatusCode.Should().Be(HttpStatusCode.OK);
        (await again.Content.ReadFromJsonAsync<StylosDeleteResponse>())!.Rev.Should().Be(2);

        var unknown = await client.DeleteAsync($"/stylos/v1/files/{Guid.NewGuid()}?base_rev=0");
        unknown.StatusCode.Should().Be(HttpStatusCode.OK);
        (await unknown.Content.ReadFromJsonAsync<StylosDeleteResponse>())!.Rev.Should().Be(0);
    }

    [Fact]
    public async Task Invalid_paths_bad_cursors_and_a_missing_delete_revision_are_refused()
    {
        var token = await PairedTokenAsync();
        var client = AuthedClient(token);
        var fileId = Guid.NewGuid();

        foreach (var path in new[] { "../escape.md", "/absolute.md", "notes/.hidden.md", "not-markdown.txt", "a//b.md" })
        {
            var response = await client.PutAsJsonAsync($"/stylos/v1/files/{fileId}", new
            {
                path,
                content = "x",
                base_rev = (long?)null,
            });
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, path);
        }

        var badCursor = await client.GetAsync("/stylos/v1/files?since=not-a-cursor");
        badCursor.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var cursorBody = JsonDocument.Parse(await badCursor.Content.ReadAsStringAsync());
        cursorBody.RootElement.GetProperty("error").GetString().Should().Be("invalid cursor");

        var badLibraryCursor = await client.GetAsync("/stylos/v1/library?since=-1");
        badLibraryCursor.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var noRev = await client.DeleteAsync($"/stylos/v1/files/{fileId}");
        noRev.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var revBody = JsonDocument.Parse(await noRev.Content.ReadAsStringAsync());
        revBody.RootElement.GetProperty("error").GetString().Should().Be("base_rev is required");
    }

    [Fact]
    public async Task An_oversized_body_is_refused()
    {
        var token = await PairedTokenAsync();
        var client = AuthedClient(token);

        var response = await client.PutAsJsonAsync($"/stylos/v1/files/{Guid.NewGuid()}", new
        {
            path = "too-big.md",
            content = new string('x', StylosSyncService.MaxFileBytes + 1),
            base_rev = (long?)null,
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("error").GetString().Should().Contain("larger than");
    }

    [Fact]
    public async Task The_cursor_only_moves_forward_and_a_published_cursor_returns_nothing_new()
    {
        var token = await PairedTokenAsync();
        var client = AuthedClient(token);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await PutNewFileAsync(client, first, "cursor-one.md", "one");
        await PutNewFileAsync(client, second, "cursor-two.md", "two");

        var list = await client.GetFromJsonAsync<JsonElement>("/stylos/v1/files?since=0");
        var cursor = long.Parse(list.GetProperty("cursor").GetString()!);
        var revisions = list.GetProperty("files").EnumerateArray()
            .Where(f => f.GetProperty("file_id").GetGuid() == first
                || f.GetProperty("file_id").GetGuid() == second)
            .Select(f => f.GetProperty("rev").GetInt64())
            .ToList();
        revisions.Should().Equal(1, 1);

        var none = await client.GetFromJsonAsync<JsonElement>($"/stylos/v1/files?since={cursor}");
        none.GetProperty("files").GetArrayLength().Should().Be(0);
        long.Parse(none.GetProperty("cursor").GetString()!).Should().Be(cursor);
    }

    // ------------------------------------------------------------------
    // Library
    // ------------------------------------------------------------------

    [Fact]
    public async Task The_library_exports_highlights_notes_and_passages_with_citations()
    {
        var token = await PairedTokenAsync();
        var client = AuthedClient(token);
        var book = await CreateBookAsync("Gilead", "Marilynne Robinson");

        var highlight = await CreateNoteAsync(book.Id, new
        {
            content = "",
            selectedText = "Grace is everywhere.",
            sourceAnchorKind = "pdf_page",
            sourceAnchorValue = "12",
            anchorVerified = true,
            highlightColour = "sage",
        });
        var note = await CreateNoteAsync(book.Id, new
        {
            content = "A thought about hope. [[Attention]]",
            selectedText = (string?)null,
        });
        var passage = await CreateNoteAsync(book.Id, new
        {
            content = "Read this again.",
            selectedText = "The house was old and the light was clear.",
            kind = "passage",
            passageTitle = "The porch",
            sourceAnchorKind = "pdf_page",
            sourceAnchorValue = "20",
            passageEnd = "22",
            anchorVerified = true,
        });

        var page = await client.GetFromJsonAsync<JsonElement>("/stylos/v1/library?since=0");
        var items = page.GetProperty("items").EnumerateArray()
            .Where(i => i.GetProperty("book_id").GetGuid() == book.Id)
            .ToList();
        items.Should().HaveCount(3);

        var highlightItem = items.Single(i => i.GetProperty("id").GetGuid() == highlight.Id);
        highlightItem.GetProperty("kind").GetString().Should().Be("highlight");
        highlightItem.GetProperty("text").GetString().Should().Be("Grace is everywhere.");
        highlightItem.GetProperty("note").ValueKind.Should().Be(JsonValueKind.Null);
        highlightItem.GetProperty("location").GetString().Should().Be("Page 12");
        highlightItem.GetProperty("citation").GetProperty("markdown").GetString()
            .Should().Be("Marilynne Robinson, *Gilead*, p. 12");
        highlightItem.GetProperty("citation").GetProperty("url").GetString()
            .Should().EndWith($"/brain/notes/{highlight.Id:D}");

        var noteItem = items.Single(i => i.GetProperty("id").GetGuid() == note.Id);
        noteItem.GetProperty("kind").GetString().Should().Be("note");
        noteItem.GetProperty("text").GetString().Should().Be("A thought about hope. Attention");
        noteItem.GetProperty("note").ValueKind.Should().Be(JsonValueKind.Null);
        noteItem.GetProperty("topics").EnumerateArray().Select(t => t.GetString())
            .Should().Contain("Attention");
        noteItem.GetProperty("location").ValueKind.Should().Be(JsonValueKind.Null);
        noteItem.GetProperty("citation").GetProperty("markdown").GetString()
            .Should().Be("Marilynne Robinson, *Gilead*");

        var passageItem = items.Single(i => i.GetProperty("id").GetGuid() == passage.Id);
        passageItem.GetProperty("kind").GetString().Should().Be("passage");
        passageItem.GetProperty("text").GetString()
            .Should().Be("The house was old and the light was clear.");
        passageItem.GetProperty("location").GetString().Should().Be("Pages 20–22");
        passageItem.GetProperty("citation").GetProperty("markdown").GetString()
            .Should().Be("Marilynne Robinson, *Gilead*, “The porch”");

        var books = page.GetProperty("books").EnumerateArray()
            .Where(b => b.GetProperty("id").GetGuid() == book.Id)
            .ToList();
        books.Should().ContainSingle();
        books[0].GetProperty("title").GetString().Should().Be("Gilead");
        books[0].GetProperty("author").GetString().Should().Be("Marilynne Robinson");
    }

    [Fact]
    public async Task The_library_feed_is_incremental_and_lists_deletions()
    {
        var token = await PairedTokenAsync();
        var client = AuthedClient(token);
        var book = await CreateBookAsync("Incremental", "An Author");
        var kept = await CreateNoteAsync(book.Id, new { content = "kept" });

        var first = await client.GetFromJsonAsync<JsonElement>("/stylos/v1/library?since=0");
        var cursor = long.Parse(first.GetProperty("cursor").GetString()!);
        first.GetProperty("items").EnumerateArray()
            .Should().Contain(i => i.GetProperty("id").GetGuid() == kept.Id);

        // A new note is the only thing the next pull returns.
        var added = await CreateNoteAsync(book.Id, new { content = "added later" });
        var delta = await client.GetFromJsonAsync<JsonElement>($"/stylos/v1/library?since={cursor}");
        var deltaIds = delta.GetProperty("items").EnumerateArray()
            .Where(i => i.GetProperty("book_id").GetGuid() == book.Id)
            .Select(i => i.GetProperty("id").GetGuid())
            .ToList();
        deltaIds.Should().Equal(added.Id);
        delta.GetProperty("deleted").GetArrayLength().Should().Be(0);
        var next = long.Parse(delta.GetProperty("cursor").GetString()!);
        next.Should().BeGreaterThan(cursor);

        // Editing a note re-exports it.
        (await Client.PutAsJsonAsync($"/api/notes/{added.Id}", new
        {
            content = "edited later",
            selectedText = (string?)null,
        })).EnsureSuccessStatusCode();
        var edited = await client.GetFromJsonAsync<JsonElement>($"/stylos/v1/library?since={next}");
        var editedItem = edited.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("id").GetGuid() == added.Id);
        editedItem.GetProperty("text").GetString().Should().Be("edited later");
        var afterEdit = long.Parse(edited.GetProperty("cursor").GetString()!);

        // Deleting a note names it, so an offline cache can drop it.
        (await Client.DeleteAsync($"/api/notes/{added.Id}")).EnsureSuccessStatusCode();
        var deleted = await client.GetFromJsonAsync<JsonElement>($"/stylos/v1/library?since={afterEdit}");
        deleted.GetProperty("deleted").EnumerateArray().Select(d => d.GetGuid())
            .Should().Contain(added.Id);
        deleted.GetProperty("items").EnumerateArray()
            .Should().NotContain(i => i.GetProperty("id").GetGuid() == added.Id);
        var afterDelete = long.Parse(deleted.GetProperty("cursor").GetString()!);

        // Deleting the book cascades to the notes: they must be named too.
        (await Client.DeleteAsync($"/api/books/{book.Id}")).EnsureSuccessStatusCode();
        var cascaded = await client.GetFromJsonAsync<JsonElement>($"/stylos/v1/library?since={afterDelete}");
        cascaded.GetProperty("deleted").EnumerateArray().Select(d => d.GetGuid())
            .Should().Contain(kept.Id);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private sealed record Pairing(Guid PairId, string Code);

    private sealed record StylosPutResponse(long Rev, string Hash);

    private sealed record StylosDeleteResponse(long Rev);

    private HttpClient AuthedClient(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<Pairing> StartPairingAsync(string deviceName)
    {
        var started = await Client.PostAsJsonAsync("/stylos/v1/pair/start", new
        {
            device_name = deviceName,
            device_pubkey = new string('b', 64),
        });
        started.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await started.Content.ReadFromJsonAsync<JsonElement>();
        return new Pairing(
            Guid.Parse(body.GetProperty("pair_id").GetString()!),
            body.GetProperty("code").GetString()!);
    }

    private async Task<string> ApproveAndCollectAsync(Pairing pair)
    {
        var approved = await Client.PostAsync($"/api/stylos/pairing/{pair.Code}/approve", null);
        approved.StatusCode.Should().Be(HttpStatusCode.OK);

        var status = await Client.GetFromJsonAsync<JsonElement>($"/stylos/v1/pair/{pair.PairId}");
        status.GetProperty("status").GetString().Should().Be("approved");
        return status.GetProperty("token").GetString()!;
    }

    private async Task<string> PairedTokenAsync()
    {
        var pair = await StartPairingAsync($"Device {Guid.NewGuid():N}");
        return await ApproveAndCollectAsync(pair);
    }

    private static async Task PutNewFileAsync(
        HttpClient client, Guid fileId, string path, string content)
    {
        var response = await client.PutAsJsonAsync($"/stylos/v1/files/{fileId}", new
        {
            path,
            content,
            base_rev = (long?)null,
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            (await response.Content.ReadAsStringAsync()));
    }

    private static async Task<string> CursorAsync(HttpClient client)
    {
        var list = await client.GetFromJsonAsync<JsonElement>("/stylos/v1/files?since=0");
        return list.GetProperty("cursor").GetString()!;
    }

    private static string Sha256(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private async Task<BookDto> CreateBookAsync(string title, string author)
    {
        var response = await Client.PostAsJsonAsync("/api/books", new
        {
            type = "ebook",
            title,
            author,
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<BookDto>())!;
    }

    private async Task<NoteDto> CreateNoteAsync(Guid bookId, object body)
    {
        var response = await Client.PostAsJsonAsync($"/api/books/{bookId}/notes", body);
        response.StatusCode.Should().Be(HttpStatusCode.Created,
            (await response.Content.ReadAsStringAsync()));
        return (await response.Content.ReadFromJsonAsync<NoteDto>())!;
    }
}
