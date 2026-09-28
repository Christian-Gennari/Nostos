// Shared catalogue helpers: contexts, tool-argument builders, DB verifies.
namespace Nostos.Backend.Tests.Assistant.QualityBed;

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nostos.Backend.Data;
using Nostos.Shared.Dtos;

internal static class QualityContexts
{
    public static AssistantContextDto ReaderEbook(Guid bookId, string title, string cfi) => new(
        Surface: "reader",
        Route: "/read",
        BookId: bookId.ToString(),
        BookTitle: title,
        BookFormat: "ebook",
        ReaderType: "epub",
        EpubCfi: cfi);

    public static AssistantContextDto SecondBrain() => new(
        Surface: "second-brain",
        Route: "/second-brain");

    public static AssistantContextDto PhysicalBook(Guid bookId, string title) => new(
        Surface: "reader",
        Route: "/read",
        BookId: bookId.ToString(),
        BookTitle: title,
        BookFormat: "physical");

    public static AssistantContextDto ExternalAudio(Guid bookId, string title) => new(
        Surface: "reader",
        Route: "/read",
        BookId: bookId.ToString(),
        BookTitle: title,
        BookFormat: "audiobook",
        ReaderType: "car-stereo");

    /// <summary>
    /// In-app audiobook playback: the reader surface the frontend's audio reader
    /// reports (BookFormat audiobook, ReaderType "audio"). Distinct from
    /// <see cref="ExternalAudio"/> so scenario anchors stay explicit.
    /// </summary>
    public static AssistantContextDto ReaderAudiobook(Guid bookId, string title) => new(
        Surface: "reader",
        Route: "/read",
        BookId: bookId.ToString(),
        BookTitle: title,
        BookFormat: "audiobook",
        ReaderType: "audio");
}

internal static class QualityArgs
{
    public static string Search(string query, params Guid[] bookIds)
    {
        var root = new JsonObject { ["query"] = query };
        if (bookIds.Length > 0)
        {
            var array = new JsonArray();
            foreach (var id in bookIds) array.Add(id.ToString());
            root["bookIds"] = array;
        }

        return root.ToJsonString();
    }

    public static string Capture(string content, Guid bookId) =>
        new JsonObject { ["content"] = content, ["bookId"] = bookId.ToString() }.ToJsonString();

    public static string CaptureVoice(string content, Guid bookId) =>
        new JsonObject
        {
            ["content"] = content,
            ["bookId"] = bookId.ToString(),
            ["captureSource"] = "voice",
        }.ToJsonString();

    public static string ReadNote(Guid noteId) =>
        new JsonObject { ["kind"] = "note", ["noteId"] = noteId.ToString() }.ToJsonString();

    public static string DeleteCollection(Guid collectionId) =>
        new JsonObject { ["collectionId"] = collectionId.ToString() }.ToJsonString();

    public static string GetCollection(Guid collectionId) =>
        new JsonObject { ["collectionId"] = collectionId.ToString() }.ToJsonString();
}

internal static class QualityVerify
{
    public static async Task<NostosDbContext> Db(IServiceProvider services)
    {
        var factory = services.GetRequiredService<IDbContextFactory<NostosDbContext>>();
        return await factory.CreateDbContextAsync();
    }

    public static string? NoProviderCall(QualityTurnRecord turn) =>
        turn.ProviderRequestCount == 0
            ? null
            : $"continuation resume must be deterministic: expected no provider call, observed {turn.ProviderRequestCount}";

    public static string? NoExecuted(QualityTurnRecord turn)
    {
        var executed = turn.Response?.ExecutedCapabilities ?? [];
        return executed.Count == 0
            ? null
            : $"expected nothing executed, observed [{string.Join(",", executed)}]";
    }

    public static string? NoPendingPlan(QualityTurnRecord turn) =>
        turn.Response?.PendingPlan is null ? null : "expected no pending plan, observed one";

    public static async Task<string?> NoteAnchor(
        IServiceProvider services, Guid noteId, string kind, string? value, bool verified)
    {
        await using var db = await Db(services);
        var note = await db.Notes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == noteId);
        if (note is null) return $"note {noteId} not found";
        if (!string.Equals(note.SourceAnchorKind, kind, StringComparison.Ordinal))
            return $"anchor kind '{note.SourceAnchorKind}', expected '{kind}'";
        if (value is not null && !string.Equals(note.SourceAnchorValue, value, StringComparison.Ordinal))
            return $"anchor value '{note.SourceAnchorValue}', expected '{value}'";
        if (note.AnchorVerified != verified)
            return $"anchor verified={note.AnchorVerified}, expected {verified}";
        return null;
    }

    public static async Task<string?> CapturedNoteAnchor(
        QualityTurnVerifyContext context, string kind, string? value, bool verified)
    {
        if (!Guid.TryParse(context.Turn.Response?.CapturedNoteId, out var noteId))
            return "no captured note id on the turn";
        return await NoteAnchor(context.Services, noteId, kind, value, verified);
    }

    public static async Task<string?> CapturedNoteBook(
        QualityTurnVerifyContext context, Guid bookId)
    {
        if (!Guid.TryParse(context.Turn.Response?.CapturedNoteId, out var noteId))
            return "no captured note id on the turn";
        await using var db = await Db(context.Services);
        var note = await db.Notes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == noteId);
        if (note is null) return $"captured note {noteId} not found";
        return note.BookId == bookId ? null : $"captured note filed under {note.BookId}, expected {bookId}";
    }

    public static async Task<string?> CollectionExists(IServiceProvider services, Guid id, bool shouldExist)
    {
        await using var db = await Db(services);
        var exists = await db.Collections.AsNoTracking().AnyAsync(c => c.Id == id);
        return exists == shouldExist
            ? null
            : shouldExist ? $"collection {id} is gone, expected it to exist" : $"collection {id} exists, expected it gone";
    }

    public static async Task<string?> CapturedNoteSource(
        QualityTurnVerifyContext context, string source)
    {
        if (!Guid.TryParse(context.Turn.Response?.CapturedNoteId, out var noteId))
            return "no captured note id on the turn";
        await using var db = await Db(context.Services);
        var note = await db.Notes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == noteId);
        if (note is null) return $"captured note {noteId} not found";
        return string.Equals(note.CaptureSource, source, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"capture source '{note.CaptureSource}', expected '{source}'";
    }
}
