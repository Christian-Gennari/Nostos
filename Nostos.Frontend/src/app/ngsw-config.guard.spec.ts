/**
 * Service worker navigationUrls guard (issue #1641).
 *
 * `ngsw-config.json` must exclude every non-SPA backend prefix (both the bare
 * path and `/**`) so the Angular service worker lets browser navigations reach
 * the backend instead of hijacking them with cached `index.html`.
 */
// vitest resolves the node built-ins at runtime.
import { existsSync, readFileSync } from 'node:fs';
import path from 'node:path';
import process from 'node:process';

import { describe, expect, it } from 'vitest';

interface NgswConfig {
  navigationUrls?: unknown;
}

const configPath = path.resolve(process.cwd(), 'ngsw-config.json');
// The SPA fallback moved out of Program.cs into the composition modules (#1678).
const backendFallbackPath = path.resolve(
  process.cwd(),
  '../Nostos.Backend/Composition/SelfHostedHttpPipeline.cs',
);

const REQUIRED_DEFAULTS = ['/**', '!/**/*.*', '!/**/*__*', '!/**/*__*/**'];
const BACKEND_NON_SPA_PREFIXES = ['/api', '/opds', '/mcp', '/oauth', '/.well-known'];

function readNavigationUrls(): string[] {
  const parsed = JSON.parse(readFileSync(configPath, 'utf8')) as NgswConfig;
  expect(Array.isArray(parsed.navigationUrls)).toBe(true);
  return parsed.navigationUrls as string[];
}

describe('ngsw-config.json navigationUrls', () => {
  it('retains the Angular service worker navigation defaults', () => {
    const urls = readNavigationUrls();
    for (const entry of REQUIRED_DEFAULTS) {
      expect(urls).toContain(entry);
    }
  });

  it.each(BACKEND_NON_SPA_PREFIXES)('excludes backend prefix %s and its subpaths', (prefix) => {
    const urls = readNavigationUrls();
    expect(urls).toContain(`!${prefix}`);
    expect(urls).toContain(`!${prefix}/**`);
  });

  it('stays in sync with literal SPA fallback exclusions in the backend ServeClientRoute', () => {
    if (!existsSync(backendFallbackPath)) return;
    const pipeline = readFileSync(backendFallbackPath, 'utf8');
    const fallbackMatch = pipeline.match(/Task\s+ServeClientRoute\([\s\S]*?index\.html/);
    expect(fallbackMatch).not.toBeNull();

    const literalPrefixes = Array.from(
      fallbackMatch![0].matchAll(/StartsWithSegments\("(\/[^"]+)"\)/g),
      (m) => m[1],
    );
    expect(literalPrefixes.length).toBeGreaterThanOrEqual(4);

    const urls = readNavigationUrls();
    for (const prefix of literalPrefixes) {
      expect(urls).toContain(`!${prefix}`);
      expect(urls).toContain(`!${prefix}/**`);
    }
  });
});
