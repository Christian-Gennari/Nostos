namespace Nostos.Backend.Middleware;

/// <summary>
/// Endpoint metadata: the route never takes the maintenance middleware's
/// ambient shared lease and is served even while an exclusive window is open.
///
/// <para>A route carrying this marker MUST guarantee:</para>
/// <list type="bullet">
/// <item>it answers without the ambient shared lease (the middleware neither
/// grants nor blocks it);</item>
/// <item>it never opens the live database while exclusive maintenance is
/// active — either it serves from memory (the activation run snapshot) or it
/// reads only durable host files (recovery manifests);</item>
/// <item>for database work outside maintenance it takes its own short shared
/// operation lease.</item>
/// </list>
/// Used by the activation request/status routes and the recovery list/status
/// routes.
/// </summary>
public sealed class LibraryMaintenanceMemorySafe;

/// <summary>
/// Endpoint metadata: the route owns its maintenance admission leases and must
/// not hold the middleware's ambient shared lease across the request.
///
/// <para>A route carrying this marker MUST guarantee:</para>
/// <list type="bullet">
/// <item>it takes its own short shared operation lease for durable reads and
/// releases it before any later exclusive request (the middleware does not wrap
/// it);</item>
/// <item>while exclusive maintenance is already active it answers the
/// migration-shaped busy response instead of entering (the middleware refuses
/// it before the handler runs).</item>
/// </list>
/// Used by the backup restore route and the recovery restore request route.
/// </summary>
public sealed class LibraryMaintenanceControl;
