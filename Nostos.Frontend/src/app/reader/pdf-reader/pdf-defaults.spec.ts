import { pdfDefaultOptions } from 'ngx-extended-pdf-viewer';
import './pdf-reader.component';

describe('PDF reader defaults', () => {
  it('loads PDFs in 1 MiB ranges without background auto-fetch once the reader loads', () => {
    expect(pdfDefaultOptions.disableAutoFetch).toBe(true);
    expect(pdfDefaultOptions.rangeChunkSize).toBe(1024 * 1024);
  });
});
