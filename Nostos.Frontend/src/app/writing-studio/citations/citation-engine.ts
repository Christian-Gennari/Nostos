import { CitationStyle } from './citation-styles';
import { CslItem } from './csl-item';

/** The slice of citeproc-js this module uses. */
interface CiteprocEngine {
  setOutputFormat(format: 'html' | 'text'): void;
  updateItems(ids: string[]): void;
  makeCitationCluster(items: { id: string; locator?: string; label?: string; position?: number }[]): string;
  makeBibliography(): [{ entry_ids?: string[][] }, string[]] | false;
}

interface CiteprocModule {
  Engine: new (
    sys: { retrieveLocale(lang: string): string; retrieveItem(id: string): CslItem },
    style: string,
    lang?: string,
  ) => CiteprocEngine;
}

/** A page in the source, when Nostos knows it for certain. Never invented. */
export interface CitationLocator {
  page: number;
}

const files = new Map<string, Promise<string>>();

function fetchText(url: string): Promise<string> {
  let pending = files.get(url);
  if (!pending) {
    pending = fetch(url).then((response) => {
      if (!response.ok) throw new Error(`Could not load ${url}`);
      return response.text();
    });
    pending.catch(() => files.delete(url));
    files.set(url, pending);
  }
  return pending;
}

let citeproc: Promise<CiteprocModule> | null = null;

/** citeproc-js is about 190 KB gzipped, so it loads only when a style is used. */
function loadCiteproc(): Promise<CiteprocModule> {
  citeproc ??= import('citeproc').then((module) => ((module as { default?: CiteprocModule }).default ?? module) as CiteprocModule);
  return citeproc;
}

/**
 * Formats citations and bibliographies in one CSL style. Its output is
 * Markdown: CSL's HTML (italic titles, small caps) is mapped to the little
 * Markdown the Studio stores, and everything else is plain text.
 */
export class CitationEngine {
  private constructor(
    readonly style: CitationStyle,
    private readonly engine: CiteprocEngine,
    private readonly items: Map<string, CslItem>,
  ) {}

  static async create(style: CitationStyle): Promise<CitationEngine> {
    if (!style.file) throw new Error('The house format needs no engine.');
    const [module, xml] = await Promise.all([loadCiteproc(), fetchText(`/csl/styles/${style.file}`)]);
    const locales = new Map<string, string>();
    await Promise.all(
      (['en-US', 'en-GB'] as const).map(async (lang) => locales.set(lang, await fetchText(`/csl/locales/locales-${lang}.xml`))),
    );
    const items = new Map<string, CslItem>();
    const engine = new module.Engine(
      {
        retrieveLocale: (lang) => locales.get(lang) ?? locales.get('en-US')!,
        retrieveItem: (id) => items.get(id)!,
      },
      xml,
      style.locale,
    );
    engine.setOutputFormat('html');
    return new CitationEngine(style, engine, items);
  }

  /** One citation: "(Schopenhauer, 1851, p. 42)", or a full or short footnote text. */
  cite(item: CslItem, locator: CitationLocator | null, subsequent = false): string {
    this.register([item]);
    const html = this.engine.makeCitationCluster([
      {
        id: item.id,
        ...(locator ? { locator: String(locator.page), label: 'page' } : {}),
        ...(subsequent ? { position: 1 } : {}),
      },
    ]);
    return cslHtmlToMarkdown(html);
  }

  /** The reference list for these items, sorted as the style sorts it. */
  bibliography(items: readonly CslItem[]): string[] {
    return this.bibliographyEntries(items).map((entry) => entry.text);
  }

  /** The reference list with the item each entry is for (issue #1157). */
  bibliographyEntries(items: readonly CslItem[]): { id: string; text: string }[] {
    if (!items.length) return [];
    this.register(items, true);
    const result = this.engine.makeBibliography();
    if (!result) return [];
    const ids = result[0].entry_ids ?? [];
    return result[1]
      .map((entry, index) => ({ id: String(ids[index]?.[0] ?? items[index]?.id ?? ''), text: cslHtmlToMarkdown(entry) }))
      .filter((entry) => entry.id && entry.text);
  }

  private register(items: readonly CslItem[], only = false): void {
    if (only) this.items.clear();
    for (const item of items) this.items.set(item.id, item);
    this.engine.updateItems([...this.items.keys()]);
  }
}

function escapeMarkdown(text: string): string {
  return text.replace(/([\\*_`[\]])/g, '\\$1');
}

function decodeEntities(text: string): string {
  return text
    .replace(/&#38;|&amp;/g, '&')
    .replace(/&#60;|&lt;/g, '<')
    .replace(/&#62;|&gt;/g, '>')
    .replace(/&#34;|&quot;/g, '"')
    .replace(/&#39;|&apos;/g, "'")
    .replace(/&#(\d+);/g, (_, code) => String.fromCodePoint(Number(code)))
    .replace(/&nbsp;|&#160;/g, ' ');
}

/** CSL's HTML to Studio Markdown: italics stay italic, every other tag goes. */
export function cslHtmlToMarkdown(html: string): string {
  const out: string[] = [];
  const pattern = /<(\/?)(i|em|b|strong)\b[^>]*>|<[^>]+>|([^<]+)/g;
  let match: RegExpExecArray | null;
  while ((match = pattern.exec(html))) {
    if (match[3] !== undefined) out.push(escapeMarkdown(decodeEntities(match[3])));
    else if (match[2] === 'i' || match[2] === 'em') out.push('*');
    else if (match[2] === 'b' || match[2] === 'strong') out.push('**');
  }
  return out.join('').replace(/\s+/g, ' ').trim();
}
