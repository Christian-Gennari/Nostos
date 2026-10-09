import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ConfirmModal } from './confirm-modal.component';

describe('ConfirmModal', () => {
  let component: ConfirmModal;
  let fixture: ComponentFixture<ConfirmModal>;

  const pressBackdrop = () => {
    const backdrop = fixture.nativeElement.querySelector('.modal-backdrop') as HTMLElement;
    for (const type of ['pointerdown', 'pointerup'] as const) {
      const event = new Event(type, { bubbles: true }) as PointerEvent;
      Object.defineProperty(event, 'pointerId', { value: 1 });
      backdrop.dispatchEvent(event);
    }
  };

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ConfirmModal],
    }).compileComponents();

    fixture = TestBed.createComponent(ConfirmModal);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('isOpen', false);
    fixture.componentRef.setInput('heading', 'Delete book?');
    fixture.detectChanges();
  });

  it('is not visible when isOpen is false', () => {
    expect(fixture.nativeElement.querySelector('.confirm-modal-card')).toBeNull();
  });

  it('keeps focus trapping opt-in for existing confirmation callers', () => {
    expect(component.trapFocus()).toBe(false);
  });

  it('renders the caller-supplied heading, description and actions when open', () => {
    fixture.componentRef.setInput('isOpen', true);
    fixture.componentRef.setInput('heading', 'Delete “Meditations”?');
    fixture.componentRef.setInput('description', 'This action cannot be undone.');
    fixture.detectChanges();

    const card = fixture.nativeElement.querySelector('.confirm-modal-card');
    expect(card).toBeTruthy();
    expect(card.querySelector('.confirm-title').textContent).toContain('Delete “Meditations”?');
    expect(card.querySelector('.confirm-description').textContent).toContain(
      'This action cannot be undone.',
    );
    const actions = card.querySelector('app-dialog-actions');
    const confirmButton = card.querySelector('.btn-confirm') as HTMLButtonElement;
    const cancelButton = card.querySelector('.btn-cancel') as HTMLButtonElement;

    expect(actions).toBeTruthy();
    expect(actions.classList).toContain('nostos-dialog-actions--inset');
    expect(actions.classList).toContain('nostos-dialog-actions--stack-narrow');
    expect(confirmButton.type).toBe('button');
    expect(cancelButton.type).toBe('button');
    expect(cancelButton.classList).toContain('nostos-button--secondary');
  });

  it('omits the description paragraph and its aria reference when none is given', () => {
    fixture.componentRef.setInput('isOpen', true);
    fixture.detectChanges();

    const card = fixture.nativeElement.querySelector('.confirm-modal-card');
    expect(card.querySelector('.confirm-description')).toBeNull();
    expect(card.getAttribute('aria-describedby')).toBeNull();
  });

  it('exposes the alertdialog role and wires the heading as its label', () => {
    fixture.componentRef.setInput('isOpen', true);
    fixture.detectChanges();

    const card = fixture.nativeElement.querySelector('.confirm-modal-card');
    expect(card.getAttribute('role')).toBe('alertdialog');
    expect(card.getAttribute('aria-modal')).toBe('true');
    expect(card.querySelector('#confirm-dialog-title').textContent).toContain('Delete book?');
    expect(card.getAttribute('aria-labelledby')).toBe('confirm-dialog-title');
  });

  it('uses the danger tone by default and the neutral tone on request', () => {
    fixture.componentRef.setInput('isOpen', true);
    fixture.detectChanges();

    const card = fixture.nativeElement.querySelector('.confirm-modal-card');
    const confirmButton = () => card.querySelector('.btn-confirm') as HTMLButtonElement;

    expect(confirmButton().classList).toContain('nostos-button--danger');
    expect(confirmButton().classList).not.toContain('nostos-button--primary');
    expect(card.querySelector('.confirm-mark')).toBeTruthy();

    fixture.componentRef.setInput('tone', 'neutral');
    fixture.detectChanges();

    expect(confirmButton().classList).toContain('nostos-button--primary');
    expect(confirmButton().classList).not.toContain('nostos-button--danger');
    expect(card.querySelector('.confirm-mark')).toBeNull();
  });

  it('emits cancel on Cancel button click', () => {
    const cancelSpy = vi.fn();
    component.cancel.subscribe(cancelSpy);

    fixture.componentRef.setInput('isOpen', true);
    fixture.detectChanges();

    const cancelBtn = fixture.nativeElement.querySelector('.btn-cancel') as HTMLButtonElement;
    cancelBtn.click();

    expect(cancelSpy).toHaveBeenCalledTimes(1);
  });

  it('emits confirm on the action button click', () => {
    const confirmSpy = vi.fn();
    component.confirm.subscribe(confirmSpy);

    fixture.componentRef.setInput('isOpen', true);
    fixture.detectChanges();

    const confirmBtn = fixture.nativeElement.querySelector('.btn-confirm') as HTMLButtonElement;
    confirmBtn.click();

    expect(confirmSpy).toHaveBeenCalledTimes(1);
  });

  it('emits cancel on Escape keydown while open', () => {
    const cancelSpy = vi.fn();
    component.cancel.subscribe(cancelSpy);

    fixture.componentRef.setInput('isOpen', true);
    fixture.detectChanges();

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    expect(cancelSpy).toHaveBeenCalledTimes(1);
  });

  it('ignores Escape while closed', () => {
    const cancelSpy = vi.fn();
    component.cancel.subscribe(cancelSpy);

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    expect(cancelSpy).not.toHaveBeenCalled();
  });

  it('locks the dialog and shows the busy label while a destructive action is in flight', () => {
    fixture.componentRef.setInput('isOpen', true);
    fixture.componentRef.setInput('confirmLabel', 'Delete Permanently');
    fixture.componentRef.setInput('busyLabel', 'Deleting…');
    fixture.componentRef.setInput('busy', true);
    fixture.detectChanges();

    const confirmBtn = fixture.nativeElement.querySelector('.btn-confirm') as HTMLButtonElement;
    const cancelBtn = fixture.nativeElement.querySelector('.btn-cancel') as HTMLButtonElement;

    expect(confirmBtn.disabled).toBe(true);
    expect(cancelBtn.disabled).toBe(true);
    expect(confirmBtn.getAttribute('aria-busy')).toBe('true');
    expect(confirmBtn.textContent).toContain('Deleting…');
  });

  it('cannot be dismissed by backdrop press or Escape while busy', () => {
    const cancelSpy = vi.fn();
    component.cancel.subscribe(cancelSpy);

    fixture.componentRef.setInput('isOpen', true);
    fixture.componentRef.setInput('busy', true);
    fixture.detectChanges();

    pressBackdrop();
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));

    expect(cancelSpy).not.toHaveBeenCalled();
  });

  it('dismisses on backdrop press when idle', () => {
    const cancelSpy = vi.fn();
    component.cancel.subscribe(cancelSpy);

    fixture.componentRef.setInput('isOpen', true);
    fixture.detectChanges();

    pressBackdrop();
    expect(cancelSpy).toHaveBeenCalledTimes(1);
  });
});
