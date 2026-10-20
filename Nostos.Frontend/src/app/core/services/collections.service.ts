import { Injectable, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, finalize, share } from 'rxjs';
import {
  Collection,
  CollectionCountDto,
  CreateCollectionDto,
  UpdateCollectionDto,
} from '../dtos/collection.dtos';

@Injectable({ providedIn: 'root' })
export class CollectionsService {
  constructor(private http: HttpClient) {}

  // UI navigation state is owned by the router (URL), not this HTTP service.

  sidebarExpanded = signal(true);

  /** The read in flight, shared by everyone who asks while it runs. */
  private listInFlight: Observable<Collection[]> | null = null;

  /**
   * The collections. The Library and its sidebar both read them on mount and
   * after every library change; callers that ask while a read is in flight
   * share it instead of sending the same request twice. Nothing is cached
   * once it answers, so a later call always reads fresh.
   */
  list(): Observable<Collection[]> {
    this.listInFlight ??= this.http.get<Collection[]>('/api/collections').pipe(
      finalize(() => (this.listInFlight = null)),
      share(),
    );
    return this.listInFlight;
  }

  /** Descendant-inclusive book counts per collection (REST-only, see CollectionCountDto). */
  getCounts(): Observable<CollectionCountDto[]> {
    return this.http.get<CollectionCountDto[]>('/api/collections/counts');
  }

  create(dto: CreateCollectionDto): Observable<Collection> {
    return this.http.post<Collection>('/api/collections', dto);
  }

  update(id: string, dto: UpdateCollectionDto): Observable<Collection> {
    return this.http.put<Collection>(`/api/collections/${id}`, dto);
  }

  delete(id: string): Observable<void> {
    return this.http.delete<void>(`/api/collections/${id}`);
  }
}
