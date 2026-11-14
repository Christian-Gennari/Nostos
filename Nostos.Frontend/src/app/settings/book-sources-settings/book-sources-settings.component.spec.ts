import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Observable, of, throwError } from 'rxjs';

import { ProviderSettingsItem, ProviderSettingsResponse } from '../../core/dtos/provider.dtos';
import { ProviderSettingsService } from '../../core/services/provider-settings.service';
import { ToastService } from '../../core/services/toast.service';
import { BookSourcesSettingsComponent } from './book-sources-settings.component';

const toastMock = { error: vi.fn(), success: vi.fn(), info: vi.fn() };

function provider(id: string, enabled: boolean): ProviderSettingsItem {
  return {
    id,
    displayName: id === 'gutenberg' ? 'Project Gutenberg' : 'Wikisource',
    description: 'A free source.',
    capabilities: ['search'],
    rightsNotice: null,
    enabled,
    enabledByDefault: true,
  };
}

const providerSettingsServiceMock = {
  list: vi.fn<() => Observable<ProviderSettingsResponse>>(),
  setEnabled: vi.fn<(id: string, enabled: boolean) => Observable<ProviderSettingsItem>>(),
};

describe('BookSourcesSettingsComponent', () => {
  let fixture: ComponentFixture<BookSourcesSettingsComponent>;
  let component: BookSourcesSettingsComponent;

  beforeEach(async () => {
    providerSettingsServiceMock.list.mockClear();
    providerSettingsServiceMock.list.mockReturnValue(
      of<ProviderSettingsResponse>({
        providers: [provider('gutenberg', true), provider('wikisource', false)],
      }),
    );
    providerSettingsServiceMock.setEnabled.mockClear();
    providerSettingsServiceMock.setEnabled.mockImplementation((id: string, enabled: boolean) =>
      of(provider(id, enabled)),
    );
    toastMock.error.mockClear();

    await TestBed.configureTestingModule({
      imports: [BookSourcesSettingsComponent],
      providers: [
        { provide: ProviderSettingsService, useValue: providerSettingsServiceMock },
        { provide: ToastService, useValue: toastMock },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(BookSourcesSettingsComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('asks the server nothing until the Library section is the open one', () => {
    expect(providerSettingsServiceMock.list).not.toHaveBeenCalled();

    fixture.componentRef.setInput('active', true);
    fixture.detectChanges();

    expect(providerSettingsServiceMock.list).toHaveBeenCalledTimes(1);
  });

  it('lists enabled and disabled sources once loaded', () => {
    fixture.componentRef.setInput('active', true);
    fixture.detectChanges();

    const rows = fixture.nativeElement.querySelectorAll('[data-testid^="provider-row-"]');
    expect(rows).toHaveLength(2);
    const toggles = Array.from(
      fixture.nativeElement.querySelectorAll('[data-testid^="provider-toggle-"]'),
    ) as HTMLInputElement[];
    expect(toggles.map((toggle) => toggle.checked)).toEqual([true, false]);
  });

  it('does not load twice while the section stays open', () => {
    fixture.componentRef.setInput('active', true);
    fixture.detectChanges();
    fixture.detectChanges();

    expect(providerSettingsServiceMock.list).toHaveBeenCalledTimes(1);
  });

  it('applies a toggle optimistically and takes the server answer', () => {
    fixture.componentRef.setInput('active', true);
    fixture.detectChanges();

    const toggle = fixture.nativeElement.querySelector(
      '[data-testid="provider-toggle-wikisource"]',
    ) as HTMLInputElement;
    toggle.click();
    fixture.detectChanges();

    expect(providerSettingsServiceMock.setEnabled).toHaveBeenCalledWith('wikisource', true);
    expect(
      component.providerSettings().find((item) => item.id === 'wikisource')?.enabled,
    ).toBe(true);
  });

  it('reverts the row and says so when the save fails', () => {
    providerSettingsServiceMock.setEnabled.mockReturnValue(throwError(() => new Error('nope')));
    fixture.componentRef.setInput('active', true);
    fixture.detectChanges();

    const toggle = fixture.nativeElement.querySelector(
      '[data-testid="provider-toggle-wikisource"]',
    ) as HTMLInputElement;
    toggle.click();
    fixture.detectChanges();

    expect(
      component.providerSettings().find((item) => item.id === 'wikisource')?.enabled,
    ).toBe(false);
    expect(fixture.nativeElement.querySelector('[data-testid="book-providers-save-failed"]')).not.toBeNull();
    expect(toastMock.error).toHaveBeenCalled();
  });

  it('retries a failed load the next time the section opens', () => {
    providerSettingsServiceMock.list.mockReturnValueOnce(throwError(() => new Error('nope')));
    fixture.componentRef.setInput('active', true);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="book-providers-load-failed"]')).not.toBeNull();

    fixture.componentRef.setInput('active', false);
    fixture.detectChanges();
    fixture.componentRef.setInput('active', true);
    fixture.detectChanges();

    expect(providerSettingsServiceMock.list).toHaveBeenCalledTimes(2);
    expect(fixture.nativeElement.querySelector('[data-testid="provider-row-gutenberg"]')).not.toBeNull();
  });
});
