/**
 * A tiny text PDF built in memory and seeded through the real API, for reader
 * specs that need selectable PDF text without a binary asset.
 */
import { apiPost } from './fixture';

export async function seedBook(
  baseUrl: string,
  title: string,
  bytes: Buffer,
  fileName: string,
  mediaType: string,
): Promise<string> {
  const created = await apiPost<any>(baseUrl, '/api/books/', {
    type: 'ebook',
    title,
    author: 'E2E',
  });
  const bookId = created?.book?.id ?? created?.bookId ?? created?.id;
  if (!bookId) throw new Error(`No book id in create response: ${JSON.stringify(created)}`);

  const form = new FormData();
  form.append('file', new Blob([bytes], { type: mediaType }), fileName);
  const response = await fetch(`${baseUrl}/api/books/${bookId}/file`, {
    method: 'POST',
    body: form,
  });
  if (!response.ok) throw new Error(`file upload -> ${response.status}: ${await response.text()}`);
  return bookId as string;
}

export function textPdf(pageTexts: string[]): Buffer {
  const objects: Array<string | null> = [
    '<< /Type /Catalog /Pages 2 0 R >>',
    null,
  ];
  const kids: number[] = [];

  for (const text of pageTexts) {
    const pageObject = objects.length + 1;
    const streamObject = pageObject + 1;
    kids.push(pageObject);
    objects.push(
      `<< /Type /Page /Parent 2 0 R /MediaBox [0 0 420 595] /Contents ${streamObject} 0 R /Resources << /Font << /F1 999 0 R >> >> >>`,
    );
    const body = text ? `BT /F1 18 Tf 60 500 Td (${text}) Tj ET` : '';
    objects.push(`<< /Length ${body.length} >>\nstream\n${body}\nendstream`);
  }

  const fontObject = objects.length + 1;
  objects.push('<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>');
  objects[1] = `<< /Type /Pages /Kids [${kids.map((n) => `${n} 0 R`).join(' ')}] /Count ${kids.length} >>`;
  // Replace the placeholder font ref after its final object number is known.
  for (let i = 2; i < objects.length - 1; i += 2) {
    objects[i] = objects[i]!.replace('999 0 R', `${fontObject} 0 R`);
  }

  let out = '%PDF-1.4\n';
  const offsets: number[] = [];
  objects.forEach((body, index) => {
    offsets.push(out.length);
    out += `${index + 1} 0 obj\n${body}\nendobj\n`;
  });
  const xref = out.length;
  out += `xref\n0 ${objects.length + 1}\n0000000000 65535 f \n`;
  for (const offset of offsets) out += `${String(offset).padStart(10, '0')} 00000 n \n`;
  out += `trailer\n<< /Size ${objects.length + 1} /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF\n`;
  return Buffer.from(out, 'latin1');
}
