import { Injectable } from '@angular/core';
import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { catchError, map, Observable, throwError } from 'rxjs';
import {
  Book,
  CreateBookDto,
  UpdateBookDto,
  UpdateProgressDto,
  PaginatedResponse,
  LibraryStatusCountsDto,
  WorkMembershipDto,
} from '../dtos/book.dtos';
import { BookFilter, BookSort } from '../dtos/book.enums';

export interface BookListOptions {
  filter?: BookFilter | string;
  sort?: BookSort | string;
  search?: string;
  page?: number;
  pageSize?: number;
  collectionId?: string;
  groupByWork?: boolean;
  format?: string | null;
}

export interface BookLocationsDto {
  locations: string;
}

export type BookLookupErrorReason =
  | 'invalid-isbn'
  | 'not-found'
  | 'unavailable'
  | 'unexpected';

export class BookLookupError extends Error {
  constructor(
    public readonly reason: BookLookupErrorReason,
    public readonly status: number,
  ) {
    super(`Book lookup failed: ${reason}`);
    this.name = 'BookLookupError';
  }
}

@Injectable({ providedIn: 'root' })
export class BooksService {
  constructor(private http: HttpClient) {}

  list(options: BookListOptions = {}): Observable<PaginatedResponse<Book>> {
    let params = new HttpParams();

    if (options.filter) params = params.set('filter', options.filter);
    if (options.sort) params = params.set('sort', options.sort);
    if (options.search) params = params.set('search', options.search);
    if (options.collectionId) params = params.set('collectionId', options.collectionId);
    if (options.format) params = params.set('format', options.format);
    if (options.groupByWork !== undefined) {
      params = params.set('groupByWork', options.groupByWork.toString());
    }

    params = params.set('page', options.page ?? 1);
    params = params.set('pageSize', options.pageSize ?? 20);

    return this.http.get<PaginatedResponse<Book>>('/api/books', { params });
  }

  getStatusCounts(): Observable<LibraryStatusCountsDto> {
    return this.http.get<LibraryStatusCountsDto>('/api/books/status-counts');
  }

  get(id: string): Observable<Book> {
    return this.http.get<Book>(`/api/books/${id}`);
  }

  create(dto: CreateBookDto): Observable<Book> {
    return this.http.post<Book>('/api/books', dto);
  }

  /**
   * Creates a book, or returns the one already in the library that the server
   * recognised as the same edition. `created` is false for the latter, so the
   * caller can tell "added" from "already here" before touching its file.
   */
  createOrMatch(dto: CreateBookDto): Observable<{ book: Book; created: boolean }> {
    return this.http
      .post<Book>('/api/books', dto, { observe: 'response' })
      .pipe(map((response) => ({ book: response.body as Book, created: response.status === 201 })));
  }

  update(id: string, dto: UpdateBookDto): Observable<Book> {
    return this.http.put<Book>(`/api/books/${id}`, dto);
  }

  delete(id: string): Observable<void> {
    return this.http.delete<void>(`/api/books/${id}`);
  }

  updateProgress(id: string, location: string, percentage: number): Observable<any> {
    const dto: UpdateProgressDto = { location, percentage };
    return this.http.put(`/api/books/${id}/progress`, dto);
  }

  resetProgress(id: string, allEditions = false): Observable<any> {
    return this.http.post(`/api/books/${id}/progress/reset`, null, allEditions ? { params: { allEditions: true } } : {});
  }

  /**
   * Merge this book into another book's work, so both become editions of one
   * work. Deliberately metadata-blind — differing title/author is exactly the
   * case this exists for.
   */
  linkWork(id: string, targetBookId: string): Observable<WorkMembershipDto> {
    return this.http.post<WorkMembershipDto>(`/api/books/${id}/work/link`, { targetBookId });
  }

  /** Split this book out into its own new work. */
  unlinkWork(id: string): Observable<WorkMembershipDto> {
    return this.http.post<WorkMembershipDto>(`/api/books/${id}/work/unlink`, null);
  }

  // --- Cached Locations Management (instant epub progress state calculation) ---
  getLocations(id: string): Observable<BookLocationsDto> {
    return this.http.get<BookLocationsDto>(`/api/books/${id}/locations`);
  }

  saveLocations(id: string, locations: string): Observable<void> {
    return this.http.post<void>(`/api/books/${id}/locations`, { locations });
  }

  uploadFile(bookId: string, file: File): Observable<any> {
    const formData = new FormData();
    formData.append('file', file);

    return this.http.post(`/api/books/${bookId}/file`, formData, {
      reportProgress: true,
      observe: 'events',
    });
  }

  uploadCover(bookId: string, file: File): Observable<any> {
    const formData = new FormData();
    formData.append('file', file);

    return this.http.post(`/api/books/${bookId}/cover`, formData, {
      reportProgress: true,
      observe: 'events',
    });
  }

  deleteCover(bookId: string): Observable<void> {
    return this.http.delete<void>(`/api/books/${bookId}/cover`);
  }

  lookup(isbn: string): Observable<CreateBookDto> {
    return this.http.get<CreateBookDto>(`/api/books/lookup/${encodeURIComponent(isbn)}`).pipe(
      catchError((error: unknown) => {
        if (!(error instanceof HttpErrorResponse)) {
          return throwError(() => error);
        }

        const reason: BookLookupErrorReason =
          error.status === 400
            ? 'invalid-isbn'
            : error.status === 404
              ? 'not-found'
              : error.status === 0 || error.status >= 500
                ? 'unavailable'
                : 'unexpected';

        return throwError(() => new BookLookupError(reason, error.status));
      }),
    );
  }
}

export type { Book };
