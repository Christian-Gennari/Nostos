/** Text folded for matching: accents removed, lower case ("Café" finds "cafe"). */
export function normalizeSearchText(value: string): string {
  return value.normalize('NFD').replace(/[\u0300-\u036f]/g, '').toLowerCase();
}
