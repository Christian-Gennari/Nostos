import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { CollectionsService } from './collections.service';
import type { Collection } from '../dtos/collection.dtos';

describe('CollectionsService', () => {
  let service: CollectionsService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(CollectionsService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('shares one request between callers that ask while it is in flight', () => {
    const answers: Collection[][] = [];
    service.list().subscribe((list) => answers.push(list));
    service.list().subscribe((list) => answers.push(list));

    const collections = [{ id: 'c1', name: 'Stoics' }] as Collection[];
    http.expectOne('/api/collections').flush(collections);

    expect(answers).toEqual([collections, collections]);
  });

  it('reads again once the previous read has answered', () => {
    service.list().subscribe();
    http.expectOne('/api/collections').flush([]);

    service.list().subscribe();
    http.expectOne('/api/collections').flush([]);
  });
});
