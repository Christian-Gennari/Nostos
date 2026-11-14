using Nostos.Backend.Data.Models;
using static Nostos.Backend.Services.Portability.Verification.PortableVerificationPrimitives;

namespace Nostos.Backend.Services.Portability.Verification;

/// <summary>Acquisitions, the assistant settings singleton and the note import
/// book links that attach imported notes to their book.</summary>
internal static class PortableSettingsComparisons
{
    internal static readonly IReadOnlyList<ComparisonEntry<AcquisitionContext>> AcquisitionComparisons =
    [
        Entry<AcquisitionContext>(
            nameof(PortableBookAcquisition.Id),
            Ef(("BookAcquisitionModel", nameof(BookAcquisitionModel.Id))),
            (context, failures) => CompareField(failures, "acquisition", context.Row.Id, nameof(PortableBookAcquisition.Id), context.Row.Id, context.Expected.Id)),
        Entry<AcquisitionContext>(
            nameof(PortableBookAcquisition.BookId),
            Ef(("BookAcquisitionModel", nameof(BookAcquisitionModel.BookId))),
            (context, failures) => CompareField(failures, "acquisition", context.Row.Id, nameof(PortableBookAcquisition.BookId), context.Row.BookId, context.Expected.BookId)),
        Entry<AcquisitionContext>(
            nameof(PortableBookAcquisition.ProviderId),
            Ef(("BookAcquisitionModel", nameof(BookAcquisitionModel.ProviderId))),
            (context, failures) => CompareField(failures, "acquisition", context.Row.Id, nameof(PortableBookAcquisition.ProviderId), context.Row.ProviderId, context.Expected.ProviderId)),
        Entry<AcquisitionContext>(
            nameof(PortableBookAcquisition.ProviderDisplayName),
            Ef(("BookAcquisitionModel", nameof(BookAcquisitionModel.ProviderDisplayName))),
            (context, failures) => CompareField(failures, "acquisition", context.Row.Id, nameof(PortableBookAcquisition.ProviderDisplayName), context.Row.ProviderDisplayName, context.Expected.ProviderDisplayName)),
        Entry<AcquisitionContext>(
            nameof(PortableBookAcquisition.ExternalId),
            Ef(("BookAcquisitionModel", nameof(BookAcquisitionModel.ExternalId))),
            (context, failures) => CompareField(failures, "acquisition", context.Row.Id, nameof(PortableBookAcquisition.ExternalId), context.Row.ExternalId, context.Expected.ExternalId)),
        Entry<AcquisitionContext>(
            nameof(PortableBookAcquisition.AssetId),
            Ef(("BookAcquisitionModel", nameof(BookAcquisitionModel.AssetId))),
            (context, failures) => CompareField(failures, "acquisition", context.Row.Id, nameof(PortableBookAcquisition.AssetId), context.Row.AssetId, context.Expected.AssetId)),
        Entry<AcquisitionContext>(
            nameof(PortableBookAcquisition.AssetFormat),
            Ef(("BookAcquisitionModel", nameof(BookAcquisitionModel.AssetFormat))),
            (context, failures) => CompareField(failures, "acquisition", context.Row.Id, nameof(PortableBookAcquisition.AssetFormat), context.Row.AssetFormat, context.Expected.AssetFormat)),
        Entry<AcquisitionContext>(
            nameof(PortableBookAcquisition.ImportedExtension),
            Ef(("BookAcquisitionModel", nameof(BookAcquisitionModel.ImportedExtension))),
            (context, failures) => CompareField(failures, "acquisition", context.Row.Id, nameof(PortableBookAcquisition.ImportedExtension), context.Row.ImportedExtension, context.Expected.ImportedExtension)),
        Entry<AcquisitionContext>(
            nameof(PortableBookAcquisition.SourceUrl),
            Ef(("BookAcquisitionModel", nameof(BookAcquisitionModel.SourceUrl))),
            (context, failures) => CompareField(failures, "acquisition", context.Row.Id, nameof(PortableBookAcquisition.SourceUrl), context.Row.SourceUrl, context.Expected.SourceUrl)),
        Entry<AcquisitionContext>(
            nameof(PortableBookAcquisition.RightsStatement),
            Ef(("BookAcquisitionModel", nameof(BookAcquisitionModel.RightsStatement))),
            (context, failures) => CompareField(failures, "acquisition", context.Row.Id, nameof(PortableBookAcquisition.RightsStatement), context.Row.RightsStatement, context.Expected.RightsStatement)),
        Entry<AcquisitionContext>(
            nameof(PortableBookAcquisition.AcquiredAt),
            Ef(("BookAcquisitionModel", nameof(BookAcquisitionModel.AcquiredAt))),
            (context, failures) => CompareField(failures, "acquisition", context.Row.Id, nameof(PortableBookAcquisition.AcquiredAt), context.Row.AcquiredAt, context.Expected.AcquiredAt)),
    ];

    internal static readonly IReadOnlyList<ComparisonEntry<AssistantContext>> AssistantComparisons =
    [
        Entry<AssistantContext>(
            nameof(PortableAssistantSettings.CaptureProcessingMode),
            Ef(("AssistantSettingsModel", nameof(AssistantSettingsModel.CaptureProcessingMode))),
            (context, failures) => CompareField(failures, "assistantSettings", context.Row.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), nameof(AssistantSettingsModel.CaptureProcessingMode), context.Row.CaptureProcessingMode, context.Expected.CaptureProcessingMode)),
        Entry<AssistantContext>(
            nameof(PortableAssistantSettings.UpdatedAtUtc),
            Ef(("AssistantSettingsModel", nameof(AssistantSettingsModel.UpdatedAtUtc))),
            (context, failures) => CompareField(failures, "assistantSettings", context.Row.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), nameof(AssistantSettingsModel.UpdatedAtUtc), context.Row.UpdatedAtUtc, context.Expected.UpdatedAtUtc)),
    ];

    internal static readonly IReadOnlyList<ComparisonEntry<ImportLinkContext>> ImportLinkComparisons =
    [
        Entry<ImportLinkContext>(
            nameof(PortableNoteImportBookLink.Id),
            Ef(("NoteImportBookLink", nameof(NoteImportBookLink.Id))),
            (context, failures) => CompareField(failures, "noteImportBookLink", context.Row.Id, nameof(PortableNoteImportBookLink.Id), context.Row.Id, context.Expected.Id)),
        Entry<ImportLinkContext>(
            nameof(PortableNoteImportBookLink.Source),
            Ef(("NoteImportBookLink", nameof(NoteImportBookLink.Source))),
            (context, failures) => CompareField(failures, "noteImportBookLink", context.Row.Id, nameof(PortableNoteImportBookLink.Source), context.Row.Source, context.Expected.Source)),
        Entry<ImportLinkContext>(
            nameof(PortableNoteImportBookLink.SourceKey),
            Ef(("NoteImportBookLink", nameof(NoteImportBookLink.SourceKey))),
            (context, failures) => CompareField(failures, "noteImportBookLink", context.Row.Id, nameof(PortableNoteImportBookLink.SourceKey), context.Row.SourceKey, context.Expected.SourceKey)),
        Entry<ImportLinkContext>(
            nameof(PortableNoteImportBookLink.BookId),
            Ef(("NoteImportBookLink", nameof(NoteImportBookLink.BookId))),
            (context, failures) => CompareField(failures, "noteImportBookLink", context.Row.Id, nameof(PortableNoteImportBookLink.BookId), context.Row.BookId, context.Expected.BookId)),
        Entry<ImportLinkContext>(
            nameof(PortableNoteImportBookLink.CreatedAtUtc),
            Ef(("NoteImportBookLink", nameof(NoteImportBookLink.CreatedAtUtc))),
            (context, failures) => CompareField(failures, "noteImportBookLink", context.Row.Id, nameof(PortableNoteImportBookLink.CreatedAtUtc), context.Row.CreatedAtUtc, context.Expected.CreatedAtUtc)),
    ];

    internal static void CompareAcquisitions(
        PortableLibraryData data,
        List<BookAcquisitionModel> acquisitions,
        FailureList failures)
    {
        var expected = data.BookAcquisitions.ToDictionary(item => item.Id);
        var actual = acquisitions.ToDictionary(item => item.Id);
        CompareIdSets("acquisition", expected.Keys, actual.Keys, failures);
        foreach (var (id, item) in expected)
        {
            if (actual.TryGetValue(id, out var row))
            {
                Run(AcquisitionComparisons, new AcquisitionContext(row, item), failures);
            }
        }
    }

    internal static void CompareNoteImportBookLinks(
        PortableLibraryData data,
        List<NoteImportBookLink> importLinks,
        FailureList failures)
    {
        var expected = (data.NoteImportBookLinks ?? []).ToDictionary(item => item.Id);
        var actual = importLinks.ToDictionary(item => item.Id);
        CompareIdSets("noteImportBookLink", expected.Keys, actual.Keys, failures);
        foreach (var (id, item) in expected)
        {
            if (actual.TryGetValue(id, out var row))
            {
                Run(ImportLinkComparisons, new ImportLinkContext(row, item), failures);
            }
        }
    }

    internal static void CompareAssistantSettings(
        PortableLibraryData data,
        List<AssistantSettingsModel> assistantSettings,
        FailureList failures)
    {
        if (data.AssistantSettings is null)
        {
            if (assistantSettings.Count != 0)
            {
                failures.Add(Failure(
                    PortableLibraryVerificationErrorCodes.SingletonMismatch,
                    "assistantSettings",
                    null,
                    nameof(AssistantSettingsModel.CaptureProcessingMode),
                    "The prepared import carries no portable assistant setting but the candidate has a row."));
            }

            return;
        }

        if (assistantSettings.Count != 1)
        {
            failures.Add(Failure(
                PortableLibraryVerificationErrorCodes.SingletonMismatch,
                "assistantSettings",
                null,
                nameof(AssistantSettingsModel.CaptureProcessingMode),
                "The prepared import carries a portable assistant setting but the candidate does not have exactly one row."));
            return;
        }

        Run(AssistantComparisons, new AssistantContext(assistantSettings[0], data.AssistantSettings), failures);
    }
}
