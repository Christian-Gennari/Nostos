import JSZip from 'jszip';

const CONTAINER = `<?xml version="1.0"?>
<container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
  <rootfiles><rootfile full-path="OEBPS/book.opf" media-type="application/oebps-package+xml"/></rootfiles>
</container>`;

function packageDocument(metadata: string): string {
  return `<?xml version="1.0"?>
<package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id">
  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
    <dc:identifier id="id">urn:test</dc:identifier><dc:language>en</dc:language>${metadata}
  </metadata>
  <manifest><item id="one" href="one.xhtml" media-type="application/xhtml+xml"/></manifest>
  <spine><itemref idref="one"/></spine>
</package>`;
}

/** A real EPUB archive, opened by the real epub.js. */
export async function epubFile(name: string, metadata: string): Promise<File> {
  const zip = new JSZip();
  zip.file('mimetype', 'application/epub+zip');
  zip.file('META-INF/container.xml', CONTAINER);
  zip.file('OEBPS/book.opf', packageDocument(metadata));
  zip.file(
    'OEBPS/one.xhtml',
    '<?xml version="1.0"?><html xmlns="http://www.w3.org/1999/xhtml"><head><title>t</title></head><body><p>x</p></body></html>',
  );
  const bytes = await zip.generateAsync({ type: 'arraybuffer' });
  return new File([bytes], name, { type: 'application/epub+zip' });
}
