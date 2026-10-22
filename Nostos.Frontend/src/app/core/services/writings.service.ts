import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import {
  WritingDto,
  WritingContentDto,
  CreateWritingDto,
  UpdateWritingDto,
  MoveWritingDto,
  WritingSourceDto,
  AddWritingSourceDto,
  WritingPageSetupDto,
} from '../dtos/writing.dtos';

@Injectable({
  providedIn: 'root',
})
export class WritingsService {
  private http = inject(HttpClient);
  private baseUrl = '/api/writings';

  // GET: Fetch the entire file tree
  list(): Observable<WritingDto[]> {
    return this.http.get<WritingDto[]>(this.baseUrl);
  }

  // GET: Fetch a single document content
  get(id: string): Observable<WritingContentDto> {
    return this.http.get<WritingContentDto>(`${this.baseUrl}/${id}`);
  }

  // POST: Create a Folder or Document
  create(dto: CreateWritingDto): Observable<WritingDto> {
    return this.http.post<WritingDto>(this.baseUrl, dto);
  }

  // PUT: Update Name or Content (Auto-save)
  update(id: string, dto: UpdateWritingDto): Observable<WritingContentDto> {
    return this.http.put<WritingContentDto>(`${this.baseUrl}/${id}`, dto);
  }

  // PUT: Move Item (Drag & Drop)
  move(id: string, newParentId: string | null): Observable<WritingDto> {
    const dto: MoveWritingDto = { newParentId };
    return this.http.put<WritingDto>(`${this.baseUrl}/${id}/move`, dto);
  }

  // DELETE: Remove item and its children
  delete(id: string): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}/${id}`);
  }

  // GET: Fetch kept source notes for a writing document
  listSources(writingId: string): Observable<WritingSourceDto[]> {
    return this.http.get<WritingSourceDto[]>(`${this.baseUrl}/${writingId}/notes`);
  }

  // POST: Add a kept source note to a writing document
  addSource(writingId: string, noteId: string): Observable<WritingSourceDto> {
    const dto: AddWritingSourceDto = { noteId };
    return this.http.post<WritingSourceDto>(`${this.baseUrl}/${writingId}/notes`, dto);
  }

  // DELETE: Remove a kept source note from a writing document
  removeSource(writingId: string, noteId: string): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}/${writingId}/notes/${noteId}`);
  }

  // GET: The writing's export page setup (issue #1164); null until one is saved.
  getPageSetup(id: string): Observable<WritingPageSetupDto | null> {
    return this.http.get<WritingPageSetupDto | null>(`${this.baseUrl}/${id}/page-setup`);
  }

  // PUT: Save the writing's export page setup.
  savePageSetup(id: string, dto: WritingPageSetupDto): Observable<WritingPageSetupDto> {
    return this.http.put<WritingPageSetupDto>(`${this.baseUrl}/${id}/page-setup`, dto);
  }
}
