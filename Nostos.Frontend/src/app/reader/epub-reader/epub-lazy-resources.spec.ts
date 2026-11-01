import ePub, { Book } from 'epubjs';
import JSZip from 'jszip';

import { EpubLazyResources } from './epub-lazy-resources';

/** A real archive through real epub.js: this class leans on its internals. */
const CONTAINER = `<?xml version="1.0"?>
<container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
  <rootfiles><rootfile full-path="OEBPS/book.opf" media-type="application/oebps-package+xml"/></rootfiles>
</container>`;

const PACKAGE = `<?xml version="1.0"?>
<package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id">
  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
    <dc:identifier id="id">urn:test</dc:identifier><dc:title>T</dc:title><dc:language>en</dc:language>
  </metadata>
  <manifest>
    <item id="one" href="text/one.xhtml" media-type="application/xhtml+xml"/>
    <item id="two" href="text/two.xhtml" media-type="application/xhtml+xml"/>
    <item id="style" href="css/style.css" media-type="text/css"/>
    <item id="seen" href="images/seen.jpg" media-type="image/jpeg"/>
    <item id="longer" href="images/longer-seen.jpg" media-type="image/jpeg"/>
    <item id="unused" href="images/unused.jpg" media-type="image/jpeg"/>
    <item id="font" href="fonts/face.woff2" media-type="font/woff2"/>
    <item id="gone" href="images/gone.jpg" media-type="image/jpeg"/>
    <item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml"/>
  </manifest>
  <spine toc="ncx"><itemref idref="one"/><itemref idref="two"/></spine>
</package>`;

const NCX = `<?xml version="1.0"?><ncx xmlns="http://www.daisy.org/z3986/2005/ncx/" version="2005-1"><navMap/></ncx>`;

const chapter = (body: string) =>
  `<?xml version="1.0"?><html xmlns="http://www.w3.org/1999/xhtml"><head><title>t</title></head><body>${body}</body></html>`;

async function epubBytes(): Promise<ArrayBuffer> {
  const zip = new JSZip();
  zip.file('META-INF/container.xml', CONTAINER);
  zip.file('OEBPS/book.opf', PACKAGE);
  zip.file('OEBPS/toc.ncx', NCX);
  zip.file(
    'OEBPS/text/one.xhtml',
    chapter(
      '<link rel="stylesheet" href="../css/style.css"/>' +
        '<img src="../images/seen.jpg"/><img src="../images/longer-seen.jpg"/>',
    ),
  );
  zip.file('OEBPS/text/two.xhtml', chapter('<img src="../images/gone.jpg"/><p>words</p>'));
  zip.file(
    'OEBPS/css/style.css',
    '@font-face { src: url("../fonts/face.woff2"); } body { color: red; }',
  );
  zip.file('OEBPS/images/seen.jpg', 'seen-bytes');
  zip.file('OEBPS/images/longer-seen.jpg', 'longer-bytes');
  zip.file('OEBPS/images/unused.jpg', 'unused-bytes');
  zip.file('OEBPS/fonts/face.woff2', 'font-bytes');
  return zip.generateAsync({ type: 'arraybuffer' });
}

describe('EpubLazyResources (#1316)', () => {
  let book: Book;
  let lazy: EpubLazyResources;
  let unpacked: Blob[];
  let revoked: string[];

  const render = (index: number): Promise<string> => {
    const section = book.spine.get(index) as unknown as {
      render(request: unknown): Promise<string>;
    };
    return section.render(book.load.bind(book));
  };

  beforeEach(async () => {
    unpacked = [];
    revoked = [];
    vi.spyOn(URL, 'createObjectURL').mockImplementation((blob) => {
      unpacked.push(blob as Blob);
      return `blob:test/${unpacked.length}`;
    });
    vi.spyOn(URL, 'revokeObjectURL').mockImplementation((url) => void revoked.push(url));

    book = ePub({});
    lazy = new EpubLazyResources(book);
    await book.open(await epubBytes(), 'binary');
    await book.ready;
  });

  afterEach(() => {
    lazy.destroy();
    book.destroy();
    vi.restoreAllMocks();
  });

  const contents = () => Promise.all(unpacked.map((blob) => blob.text()));

  it('unpacks no asset to open the book', () => {
    expect(unpacked).toEqual([]);
  });

  it('unpacks what a chapter names, and only that', async () => {
    const html = await render(0);

    const text = await contents();
    expect(text).toContain('seen-bytes');
    expect(text).toContain('longer-bytes');
    expect(text).not.toContain('unused-bytes');
    expect(html).not.toContain('../images/');
    expect(html).toContain('blob:test/');
  });

  it('points each reference at its own file when one path ends another', async () => {
    const html = await render(0);
    const texts = await contents();
    const urlOf = (bytes: string) => `src="blob:test/${texts.indexOf(bytes) + 1}"`;

    expect(html).toContain(urlOf('seen-bytes'));
    expect(html).toContain(urlOf('longer-bytes'));
  });

  it('rewrites a stylesheet\'s own assets, relative to the stylesheet', async () => {
    await render(0);

    const css = (await contents()).find((text) => text.includes('@font-face'))!;
    expect(css).toMatch(/url\("blob:test\/\d+"\)/);
    expect(await contents()).toContain('font-bytes');
  });

  it('unpacks an asset once, however many times it is named', async () => {
    await render(0);
    const count = unpacked.length;

    await render(0);

    expect(unpacked).toHaveLength(count);
  });

  it('still renders a chapter that names an asset missing from the archive', async () => {
    const html = await render(1);

    expect(html).toContain('words');
    expect(html).toContain('../images/gone.jpg');
  });

  it('revokes every URL it made', async () => {
    await render(0);
    const made = unpacked.length;

    lazy.destroy();

    expect(revoked).toHaveLength(made);
  });
});
