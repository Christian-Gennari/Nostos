import { HttpClient, HttpEvent, HttpHeaders, HttpResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

@Injectable({ providedIn: 'root' })
export class PortableLibraryService {
  private readonly http = inject(HttpClient);

  exportArchive(): Observable<HttpEvent<Blob>> {
    return this.http.get('/api/portability/export', {
      observe: 'events',
      responseType: 'blob',
      reportProgress: true,
    });
  }

  /**
   * Notes, highlights, passages and topics as a .zip of Markdown files
   * (issue #1221), with the Writing Studio's documents unless left out.
   * For reading elsewhere; it is never imported back.
   */
  exportNotesMarkdown(includeWritings: boolean): Observable<HttpResponse<Blob>> {
    return this.http.get('/api/portability/markdown', {
      observe: 'response',
      responseType: 'blob',
      params: { writings: includeWritings },
    });
  }

  importArchive(file: File): Observable<unknown> {
    return this.http.post('/api/portability/import', file, {
      headers: new HttpHeaders({
        'Content-Type': 'application/vnd.nostos.portable+zip',
      }),
    });
  }
}
