namespace Nostos.Backend.Data.Models;

// Singleton library state row. Exactly one row is enforced by the unique
// index on SingletonSlot plus the CK_LibraryStates_SingletonSlot check; the
// fixed Id lets the service address the singleton directly. StateVersion
// bumps on every committed library mutation (mirror of ReadingProgramme).
public class LibraryState
{
    public static readonly Guid WellKnownId = new("D3C2B1A0-9F8E-4D7C-8B6A-5F4E3D2C1B0A");
    public const int SingletonSentinel = 1;

    public Guid Id { get; set; } = WellKnownId;
    public int SingletonSlot { get; set; } = SingletonSentinel;

    public string StateVersion { get; set; } = "0";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
