import { TestBed } from '@angular/core/testing';

import { ToastService } from './toast.service';

describe('ToastService', () => {
  let service: ToastService;

  beforeEach(async () => {
    await TestBed.configureTestingModule({}).compileComponents();
    service = TestBed.inject(ToastService);
  });

  it('shows toasts', () => {
    service.success('Saved');

    expect(service.toasts().length).toBe(1);
    expect(service.toasts()[0]).toMatchObject({ message: 'Saved', type: 'success' });
  });

  it('ignores blank messages', () => {
    service.info('   ');

    expect(service.toasts()).toEqual([]);
  });

  it('dedupes identical toasts instead of stacking', () => {
    service.error('Failed');
    service.error('Failed');

    expect(service.toasts().length).toBe(1);
  });

  it('caps visible toasts at three', () => {
    service.info('one');
    service.info('two');
    service.info('three');
    service.info('four');

    expect(service.toasts().map((t) => t.message)).toEqual(['two', 'three', 'four']);
  });

  it('dismisses by id', () => {
    service.info('one');
    const id = service.toasts()[0].id;

    service.dismiss(id);

    expect(service.toasts()).toEqual([]);
  });

  it('runs a toast’s action once and dismisses it', () => {
    const run = vi.fn();
    service.success('Added virtue to 3 notes', { label: 'Undo', run });
    const toast = service.toasts()[0];

    expect(toast.action?.label).toBe('Undo');
    service.runAction(toast);

    expect(run).toHaveBeenCalledTimes(1);
    expect(service.toasts()).toEqual([]);
  });
});
