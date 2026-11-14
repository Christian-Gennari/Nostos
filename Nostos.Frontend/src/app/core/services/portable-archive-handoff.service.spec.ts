import { TestBed } from '@angular/core/testing';

import { beforeEach, describe, expect, it } from 'vitest';

import { PortableArchiveHandoffService } from './portable-archive-handoff.service';

describe('PortableArchiveHandoffService', () => {
  let service: PortableArchiveHandoffService;

  beforeEach(() => {
    TestBed.configureTestingModule({});
    service = TestBed.inject(PortableArchiveHandoffService);
  });

  it('hands one archive to the import flow', () => {
    const file = new File(['archive'], 'library.nostos');

    service.hand(file);

    expect(service.pending()).toBe(file);
    expect(service.take()).toBe(file);
  });

  it('hands it over only once, so a reload cannot replay it', () => {
    const file = new File(['archive'], 'library.nostos');
    service.hand(file);

    expect(service.take()).toBe(file);
    expect(service.pending()).toBeNull();
    expect(service.take()).toBeNull();
  });

  it('takes nothing when nothing was handed over', () => {
    expect(service.take()).toBeNull();
  });

  it('keeps the newest archive when two are dropped', () => {
    const first = new File(['one'], 'one.nostos');
    const second = new File(['two'], 'two.nostos');

    service.hand(first);
    service.hand(second);

    expect(service.take()).toBe(second);
  });
});
