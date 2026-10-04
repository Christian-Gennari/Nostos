/**
 * Minimal Vite `import.meta.glob` typing for the test-only raw-source scans in
 * `library-transfer-transport.spec.ts` and
 * `migration-http.contract.spec.ts`. Vite performs the transform; this
 * declaration only teaches TypeScript the call shape.
 */
interface ImportMeta {
  glob(
    pattern: string | string[],
    options?: {
      query?: string;
      import?: string;
      eager?: boolean;
    },
  ): Record<string, string>;
}
