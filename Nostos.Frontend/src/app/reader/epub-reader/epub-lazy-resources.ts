import type { Book } from 'epubjs';

/** What is used of epub.js's Section: it is rewritten in place of its markup. */
interface SerializedSection {
  url: string;
  output: string;
}

/** The parts of epub.js this reaches into, none of which its types declare. */
interface EpubInternals {
  archive?: {
    getBlob(url: string): Promise<Blob> | undefined;
    getText(url: string): Promise<string> | undefined;
  };
  resources?: {
    urls: string[];
    cssUrls: string[];
    settings: { resolver(href: string): string };
    relativeTo(absolute: string): string[];
  };
  replacements(): Promise<unknown>;
  spine: {
    hooks: {
      serialize: { register(hook: (output: string, section: SerializedSection) => unknown): void };
    };
  };
}

/**
 * Unpacks a book's images, fonts and stylesheets when a chapter asks for them.
 *
 * An opening epub.js inflates every asset in the book before it shows anything.
 * That is what opening a book costs: on a 68 MB book of 306 photographs it was
 * 8.6 of the 9.8 seconds at 4x CPU throttle, and the reader looked at one of them.
 * Here the eager step is switched off and each chapter's markup is rewritten as
 * it is serialized, for the assets that markup names.
 *
 * The substitution matches epub.js's own: a plain search for the asset's path
 * relative to the chapter, in the chapter's text.
 */
export class EpubLazyResources {
  private readonly book: EpubInternals;
  private readonly assets = new Map<string, Promise<string | null>>();
  private readonly stylesheets = new Map<string, Promise<string | null>>();
  private readonly objectUrls = new Set<string>();

  /** Call before the book is opened. */
  constructor(book: Book) {
    this.book = book as unknown as EpubInternals;
    this.book.replacements = () => Promise.resolve();
    this.book.spine.hooks.serialize.register((output, section) =>
      this.substituteSection(output, section),
    );
  }

  /**
   * Revokes every URL made for the book. epub.js cannot: its own clean-up revokes
   * the keys of its URL table rather than the URLs, so they outlive the book.
   */
  destroy(): void {
    for (const url of this.objectUrls) URL.revokeObjectURL(url);
    this.objectUrls.clear();
    this.assets.clear();
    this.stylesheets.clear();
  }

  private async substituteSection(output: string, section: SerializedSection): Promise<void> {
    const resources = this.book.resources;
    if (!resources) return;
    try {
      section.output = await this.substitute(
        output,
        resources.relativeTo(section.url),
        (href) => (resources.cssUrls.includes(href) ? this.stylesheet(href) : this.asset(href)),
      );
    } catch (error) {
      // A chapter with a missing picture beats a chapter that never opens.
      console.warn('Could not unpack the assets of a section:', error);
    }
  }

  /** `relativeUrls` is aligned with `resources.urls`. */
  private async substitute(
    text: string,
    relativeUrls: string[],
    urlFor: (href: string) => Promise<string | null>,
  ): Promise<string> {
    const hrefs = this.book.resources?.urls ?? [];
    const named = hrefs
      .map((href, index) => ({ href, relative: relativeUrls[index] }))
      .filter(({ relative }) => !!relative && text.includes(relative))
      // Longest first: `a.png` must not rewrite the tail of `aa.png`.
      .sort((a, b) => b.relative.length - a.relative.length);

    const replacements = await Promise.all(named.map(({ href }) => urlFor(href)));
    named.forEach(({ relative }, index) => {
      const replacement = replacements[index];
      if (replacement) text = text.split(relative).join(replacement);
    });
    return text;
  }

  private asset(href: string): Promise<string | null> {
    return memoized(this.assets, href, async () => {
      const blob = await this.book.archive?.getBlob(this.absolute(href));
      return blob ? this.objectUrl(blob) : null;
    });
  }

  /** A stylesheet names assets of its own (fonts, backgrounds), relative to itself. */
  private stylesheet(href: string): Promise<string | null> {
    return memoized(this.stylesheets, href, async () => {
      const absolute = this.absolute(href);
      const css = await this.book.archive?.getText(absolute);
      const resources = this.book.resources;
      if (css === undefined || !resources) return null;

      const rewritten = await this.substitute(css, resources.relativeTo(absolute), (asset) =>
        this.asset(asset),
      );
      return this.objectUrl(new Blob([rewritten], { type: 'text/css' }));
    });
  }

  private absolute(href: string): string {
    return this.book.resources!.settings.resolver(href);
  }

  private objectUrl(blob: Blob): string {
    const url = URL.createObjectURL(blob);
    this.objectUrls.add(url);
    return url;
  }
}

function memoized(
  urls: Map<string, Promise<string | null>>,
  href: string,
  create: () => Promise<string | null>,
): Promise<string | null> {
  let url = urls.get(href);
  if (!url) {
    // One bad asset costs its own picture, not the chapter that names it.
    url = create().catch(() => null);
    urls.set(href, url);
  }
  return url;
}
