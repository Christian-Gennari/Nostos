namespace Nostos.Backend.Middleware;

/// <summary>
/// Marks a route that owns its own library admission and stays answerable while
/// exclusive maintenance has the live database closed (for example the
/// activation request/status routes, which serve from an in-memory activation
/// snapshot in that window). The maintenance middleware never takes the ambient
/// shared lease for such a route and never answers it with maintenance busy;
/// the handler must take its own short operation leases for database work and
/// must not open the live database while maintenance is active.
/// </summary>
public sealed class LibraryMaintenanceMemorySafe;
