// Quality bed fixture identities (issue #566).
//
// Every GUID below is deterministic so gold evidence can be referenced from
// scenario expectations as constants, and so repeated runs seed byte-identical
// libraries. Nothing here is maintainer data: all titles, names and passages
// are invented for this harness.
namespace Nostos.Backend.Tests.Assistant.QualityBed;

internal static class QualityFixtureIds
{
    // --- Collections (>=3) ---
    public static readonly Guid CollectionFiction = Guid.Parse("c1111111-1111-4111-8111-111111111111");
    public static readonly Guid CollectionEssays = Guid.Parse("c2222222-2222-4222-8222-222222222222");
    public static readonly Guid CollectionCraft = Guid.Parse("c3333333-3333-4333-8333-333333333333");

    // --- Books (>=5 across >=3 collections; B6 is deliberately Unsorted) ---
    // B1: EBook, PDF-indexed text. B2: EBook, EPUB-indexed text.
    public static readonly Guid BookSaltMeridian = Guid.Parse("a1111111-1111-4111-8111-111111111111");
    public static readonly Guid BookCartographer = Guid.Parse("a2222222-2222-4222-8222-222222222222");
    public static readonly Guid BookQuietMornings = Guid.Parse("a3333333-3333-4333-8333-333333333333");
    public static readonly Guid BookWoodenBoats = Guid.Parse("a4444444-4444-4444-8444-444444444444");
    public static readonly Guid BookSleeperCar = Guid.Parse("a5555555-5555-4555-8555-555555555555");
    public static readonly Guid BookMarginalia = Guid.Parse("a6666666-6666-4666-8666-666666666666");
    // B7: indexed-book shell whose ingestion is scheduled but never completed
    // (Pending state, for the C15 indexing-pending turn).
    public static readonly Guid BookGranaryLedger = Guid.Parse("a7777777-7777-4777-8777-777777777777");

    public const string TitleSaltMeridian = "The Salt Meridian";
    public const string TitleCartographer = "The Cartographer's Daughter";
    public const string TitleQuietMornings = "A Field Guide to Quiet Mornings";
    public const string TitleWoodenBoats = "On Repairing Wooden Boats";
    public const string TitleSleeperCar = "Sleeper Car North";
    public const string TitleMarginalia = "Commonplace Book";
    public const string TitleGranaryLedger = "The Granary Ledger";

    // --- Concepts (>=6, all linked) ---
    public static readonly Guid ConceptAttention = Guid.Parse("d1111111-1111-4111-8111-111111111111");
    public static readonly Guid ConceptSeamanship = Guid.Parse("d2222222-2222-4222-8222-222222222222");
    public static readonly Guid ConceptBoatRepair = Guid.Parse("d3333333-3333-4333-8333-333333333333");
    public static readonly Guid ConceptMorningHours = Guid.Parse("d4444444-4444-4444-8444-444444444444");
    public static readonly Guid ConceptRiverMaps = Guid.Parse("d5555555-5555-4555-8555-555555555555");
    public static readonly Guid ConceptHarborRules = Guid.Parse("d6666666-6666-4666-8666-666666666666");
    public static readonly Guid ConceptGalleryWatch = Guid.Parse("d7777777-7777-4777-8777-777777777777");

    // --- Notes (>=20, all anchored) ---
    public static readonly Guid NoteRopeCoil = Guid.Parse("e0000001-0001-4001-8001-000000000001"); // C1/C3 gold
    public static readonly Guid NoteGalleryEssay = Guid.Parse("e0000002-0002-4002-8002-000000000002"); // C2 long note
    public static readonly Guid NoteSaltRain = Guid.Parse("e0000003-0003-4003-8003-000000000003");
    public static readonly Guid NoteStoveLit = Guid.Parse("e0000004-0004-4004-8004-000000000004");
    public static readonly Guid NoteSleeperBerth = Guid.Parse("e0000005-0005-4005-8005-000000000005");
    public static readonly Guid NoteApprenticeInk = Guid.Parse("e0000006-0006-4006-8006-000000000006");
    public static readonly Guid NoteKeeper = Guid.Parse("e0000007-0007-4007-8007-000000000007"); // C7 low-overlap gold
    public static readonly Guid NoteDistHarborDues = Guid.Parse("e0000008-0008-4008-8008-000000000008"); // C8 distractor
    public static readonly Guid NoteDistLanternGift = Guid.Parse("e0000009-0009-4009-8009-000000000009"); // C8 distractor
    public static readonly Guid NoteDistEveningTrain = Guid.Parse("e0000010-0010-4010-8010-000000000010"); // C8 distractor
    public static readonly Guid NoteLanternWalk = Guid.Parse("e0000011-0011-4011-8011-000000000011"); // C8 gold
    public static readonly Guid NoteMendingMornings = Guid.Parse("e0000012-0012-4012-8012-000000000012"); // C9a
    public static readonly Guid NoteWalkingMornings = Guid.Parse("e0000013-0013-4013-8013-000000000013"); // C9b
    public static readonly Guid NoteMarginChapterTwo = Guid.Parse("e0000014-0014-4014-8014-000000000014"); // C11 gold
    public static readonly Guid NoteInjection = Guid.Parse("e0000018-0018-4018-8018-000000000018"); // C18 injection
    public static readonly Guid NoteRepairPhilosophy = Guid.Parse("e0000019-0019-4019-8019-000000000019"); // C19 gold
    public static readonly Guid NoteInkApprentice = Guid.Parse("e0000020-0020-4020-8020-000000000020");
    public static readonly Guid NotePorridge = Guid.Parse("e0000021-0021-4021-8021-000000000021");
    public static readonly Guid NoteOdds = Guid.Parse("e0000022-0022-4022-8022-000000000022");
    public static readonly Guid NoteLedgerHand = Guid.Parse("e0000023-0023-4023-8023-000000000023");
    public static readonly Guid NoteUpperBerth = Guid.Parse("e0000024-0024-4024-8024-000000000024");

    // --- Book-text revisions (64-hex SHAs; extractor version pinned) ---
    public const string ExtractorVersion = "nostos-book-text-v2";

    public const string ShaSaltMeridian =
        "b1b1b1b1b1b1b1b1b1b1b1b1b1b1b1b1b1b1b1b1b1b1b1b1b1b1b1b1b1b1b1b1";
    public const string ShaCartographer =
        "b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2";
}
