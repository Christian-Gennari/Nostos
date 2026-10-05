namespace Nostos.Backend.Middleware;

/// <summary>
/// Endpoint metadata: the route owns its maintenance admission leases, allowing
/// a shared-to-exclusive handoff. The middleware skips its ambient shared lease
/// for these routes; while an exclusive window is already active they answer
/// the migration-shaped busy response like any other library route.
/// </summary>
public sealed class LibraryMaintenanceControl;

/// <summary>
/// Endpoint metadata: the route reads only durable filesystem state (never the
/// active library or a database) and must keep answering while an exclusive
/// maintenance window is open — for example a restore status poll. The
/// middleware admits these routes without any lease.
/// </summary>
public sealed class LibraryMaintenanceSafe;
