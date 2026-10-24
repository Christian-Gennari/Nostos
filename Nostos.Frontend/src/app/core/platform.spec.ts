import { isApplePlatform, modifierKeyLabel } from './platform';

describe('platform', () => {
  const mac = 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15';
  const windows = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120';
  const iphone = 'Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X)';

  it('names ⌘ on Apple devices and Ctrl elsewhere', () => {
    expect(modifierKeyLabel(mac)).toBe('⌘');
    expect(modifierKeyLabel(iphone)).toBe('⌘');
    expect(modifierKeyLabel(windows)).toBe('Ctrl');
  });

  it('falls back to Ctrl when there is no user agent', () => {
    expect(isApplePlatform('')).toBe(false);
  });
});
