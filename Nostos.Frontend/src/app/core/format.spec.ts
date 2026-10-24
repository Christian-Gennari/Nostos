import { formatAudioTime, formatFileSize, plural } from './format';

describe('format helpers', () => {
  it('pluralises by count', () => {
    expect(plural(1, 'note')).toBe('1 note');
    expect(plural(3, 'note')).toBe('3 notes');
    expect(plural(2, 'library', 'libraries')).toBe('2 libraries');
  });

  it('formats audio time as m:ss or h:mm:ss', () => {
    expect(formatAudioTime(754.9)).toBe('12:34');
    expect(formatAudioTime(3600)).toBe('1:00:00');
    expect(formatAudioTime(Number.NaN)).toBe('0:00');
  });

  it('formats file sizes on one 1024-based scale named B, KB, MB, GB', () => {
    expect(formatFileSize(0)).toBe('0 B');
    expect(formatFileSize(1536)).toBe('1.5 KB');
    expect(formatFileSize(25 * 1024 * 1024)).toBe('25 MB');
    expect(formatFileSize(2.5 * 1024 ** 3)).toBe('2.5 GB');
    expect(formatFileSize(null)).toBe('');
    expect(formatFileSize(-1)).toBe('');
  });
});
