/**
 * A hosting plan's own words for a limit it enforces, such as a Cloud trial's
 * upload limit or a full storage allowance. The host answers with a problem
 * response whose `code` ends in `_limit_reached` and whose `detail` is written
 * for the reader; showing it beats a generic "could not accept this file".
 * SelfHosted never sends one, so this returns null there.
 */
export function planLimitMessage(error: unknown): string | null {
  const body = (error as { error?: unknown } | null)?.error;
  if (!body || typeof body !== 'object') return null;
  const { code, detail } = body as { code?: unknown; detail?: unknown };
  if (typeof code !== 'string' || !code.endsWith('_limit_reached')) return null;
  if (typeof detail !== 'string') return null;
  const text = detail.trim();
  return text.length > 0 && text.length <= 400 ? text : null;
}
