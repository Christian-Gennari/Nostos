import { CommonModule, DOCUMENT } from '@angular/common';
import {
  AfterViewInit,
  Component,
  ElementRef,
  HostListener,
  Injector,
  afterNextRender,
  OnInit,
  computed,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { Router } from '@angular/router';
import { catchError, forkJoin, map, of } from 'rxjs';

import { WritingDto } from '../../core/dtos/writing.dtos';
import { ToastService } from '../../core/services/toast.service';
import { WritingsService } from '../../core/services/writings.service';
import { ButtonComponent } from '../../ui/button/button.component';
import { DialogActionsComponent } from '../../ui/dialog-actions/dialog-actions.component';
import { InputDirective } from '../../ui/form-control/form-control.directive';
import { IconButtonComponent } from '../../ui/icon-button/icon-button.component';
import { NostosIconComponent } from '../../ui/icon/nostos-icon.component';
import { ModalShell } from '../../ui/modal-shell/modal-shell.component';

export interface BrainWritingHandoffResult {
  succeededNoteIds: string[];
  failedNoteIds: string[];
}

@Component({
  standalone: true,
  selector: 'app-brain-writing-handoff',
  imports: [
    CommonModule,
    ModalShell,
    DialogActionsComponent,
    ButtonComponent,
    InputDirective,
    IconButtonComponent,
    NostosIconComponent,
  ],
  templateUrl: './brain-writing-handoff.component.html',
  styleUrls: ['./brain-writing-handoff.component.css'],
})
export class BrainWritingHandoffComponent implements OnInit, AfterViewInit {
  readonly noteIds = input.required<readonly string[]>();

  readonly cancelled = output<void>();
  readonly completed = output<BrainWritingHandoffResult>();

  private readonly writingsService = inject(WritingsService);
  private readonly toast = inject(ToastService);
  private readonly router = inject(Router);

  readonly destinations = signal<WritingDto[]>([]);
  readonly destinationFilter = signal('');
  readonly loadingDestinations = signal(true);
  readonly destinationError = signal(false);
  readonly selectedWritingId = signal<string | null>(null);
  readonly newWritingMode = signal(false);
  readonly newWritingTitle = signal('');
  readonly openWritingAfterAdd = signal(false);
  readonly saving = signal(false);

  readonly documentDestinations = computed(() =>
    this.destinations().filter((writing) => writing.type === 'Document')
  );

  readonly filteredDestinations = computed(() => {
    const query = this.destinationFilter().trim().toLocaleLowerCase();
    if (!query) return this.documentDestinations();
    return this.documentDestinations().filter((writing) =>
      writing.name.toLocaleLowerCase().includes(query)
    );
  });

  readonly selectedWriting = computed(() => {
    const selectedId = this.selectedWritingId();
    return selectedId
      ? this.documentDestinations().find((writing) => writing.id === selectedId) ?? null
      : null;
  });

  readonly sourceCount = computed(() => this.uniqueNoteIds().length);

  readonly canConfirm = computed(
    () =>
      !this.loadingDestinations() &&
      !this.saving() &&
      (this.newWritingMode()
        ? this.newWritingTitle().trim().length > 0
        : this.selectedWriting() !== null)
  );

  private readonly host = inject(ElementRef<HTMLElement>);
  private readonly document = inject(DOCUMENT);
  private readonly injector = inject(Injector);
  private returnFocusElement: HTMLElement | null = null;

  ngOnInit(): void {
    this.loadDestinations();
  }

  ngAfterViewInit(): void {
    const activeElement = this.document.activeElement;
    this.returnFocusElement = activeElement instanceof HTMLElement ? activeElement : null;
    setTimeout(() => {
      const initialFocus = this.host.nativeElement.querySelector('.handoff-close') as HTMLButtonElement | null;
      initialFocus?.focus({ preventScroll: true });
    });
  }

  loadDestinations(): void {
    this.loadingDestinations.set(true);
    this.destinationError.set(false);

    this.writingsService.list().subscribe({
      next: (items) => {
        this.destinations.set(items);
        this.loadingDestinations.set(false);
        if (!this.documentDestinations().length) this.chooseNew();
      },
      error: () => {
        this.loadingDestinations.set(false);
        this.destinationError.set(true);
      },
    });
  }

  chooseExisting(writingId: string): void {
    if (this.saving()) return;
    if (!this.documentDestinations().some((writing) => writing.id === writingId)) return;
    this.newWritingMode.set(false);
    this.selectedWritingId.set(writingId);
  }

  chooseNew(): void {
    if (this.saving()) return;
    this.selectedWritingId.set(null);
    this.newWritingMode.set(true);
    afterNextRender(
      () =>
        setTimeout(() => {
          if (this.newWritingMode() && !this.saving()) {
            const titleInput = this.host.nativeElement.querySelector(
              '.title-field input'
            ) as HTMLInputElement | null;
            titleInput?.focus({ preventScroll: true });
          }
        }),
      { injector: this.injector }
    );
  }

  cancel(): void {
    if (this.saving()) return;
    this.cancelled.emit();
    this.restoreFocusAfterClose();
  }

  @HostListener('document:keydown', ['$event'])
  onDocumentKeydown(event: KeyboardEvent): void {
    if (event.key !== 'Tab') return;

    const dialog = this.host.nativeElement.querySelector('.handoff-dialog') as HTMLElement | null;
    if (!dialog) return;

    const focusable = Array.from(
      dialog.querySelectorAll(
        'button:not(:disabled), input:not(:disabled), select:not(:disabled), textarea:not(:disabled), a[href], [tabindex]:not([tabindex="-1"])'
      )
    ) as HTMLElement[];
    const visibleFocusable = focusable.filter(
      (element) => !element.hasAttribute('hidden') && element.getAttribute('aria-hidden') !== 'true'
    );

    if (!visibleFocusable.length) {
      event.preventDefault();
      const focusFallback = dialog.querySelector('.handoff-heading h2') as HTMLElement | null;
      focusFallback?.focus({ preventScroll: true });
      return;
    }

    const first = visibleFocusable[0];
    const last = visibleFocusable[visibleFocusable.length - 1];
    const active = this.document.activeElement;
    if (!(active instanceof Node) || !dialog.contains(active)) {
      event.preventDefault();
      (event.shiftKey ? last : first).focus({ preventScroll: true });
    } else if (event.shiftKey && active === first) {
      event.preventDefault();
      last.focus({ preventScroll: true });
    } else if (!event.shiftKey && active === last) {
      event.preventDefault();
      first.focus({ preventScroll: true });
    }
  }

  confirmFromTitle(event: Event): void {
    event.preventDefault();
    if (this.canConfirm()) this.confirm();
  }

  confirm(): void {
    if (!this.canConfirm()) return;

    const noteIds = this.uniqueNoteIds();
    if (!noteIds.length) {
      this.completed.emit({ succeededNoteIds: [], failedNoteIds: [] });
      return;
    }

    this.saving.set(true);

    if (this.newWritingMode()) {
      const name = this.newWritingTitle().trim() || 'Untitled writing';
      this.writingsService
        .create({
          name,
          type: 'Document',
          parentId: null,
        })
        .subscribe({
          next: (writing) => {
            // A partial source failure must retry against this same document,
            // never create a second writing on the next click.
            this.destinations.update((items) => [
              ...items.filter((item) => item.id !== writing.id),
              writing,
            ]);
            this.newWritingMode.set(false);
            this.selectedWritingId.set(writing.id);
            this.addToWriting(writing, noteIds);
          },
          error: () => {
            this.saving.set(false);
            this.toast.error('Writing could not be created');
          },
        });
      return;
    }

    const writing = this.selectedWriting();
    if (!writing) {
      this.saving.set(false);
      return;
    }

    this.addToWriting(writing, noteIds);
  }

  private addToWriting(writing: WritingDto, noteIds: string[]): void {
    this.writingsService.listSources(writing.id).subscribe({
      next: (existingSources) => {
        const existingIds = new Set(existingSources.map((source) => source.id));
        const alreadyKept = noteIds.filter((noteId) => existingIds.has(noteId));
        const missing = noteIds.filter((noteId) => !existingIds.has(noteId));

        if (!missing.length) {
          this.finishAttempt(writing, alreadyKept, [], [], alreadyKept.length);
          return;
        }

        forkJoin(
          missing.map((noteId) =>
            this.writingsService.addSource(writing.id, noteId).pipe(
              map(() => ({ noteId, ok: true as const })),
              catchError(() => of({ noteId, ok: false as const }))
            )
          )
        ).subscribe((results) => {
          const added = results.filter((result) => result.ok).map((result) => result.noteId);
          const failed = results.filter((result) => !result.ok).map((result) => result.noteId);
          this.finishAttempt(writing, alreadyKept, added, failed, alreadyKept.length);
        });
      },
      error: () => {
        this.saving.set(false);
        this.toast.error('Kept sources could not be checked');
      },
    });
  }

  private finishAttempt(
    writing: WritingDto,
    alreadyKept: string[],
    added: string[],
    failed: string[],
    alreadyCount: number
  ): void {
    this.saving.set(false);

    const succeeded = [...alreadyKept, ...added];
    const label = succeeded.length === 1 ? 'source' : 'sources';

    if (failed.length) {
      const failedLabel = failed.length === 1 ? 'source' : 'sources';
      const alreadySuffix = alreadyCount ? ` · ${alreadyCount} already there` : '';
      this.toast.error(
        `Kept ${succeeded.length} ${label} with “${writing.name}”; ${failed.length} ${failedLabel} could not be added${alreadySuffix}`
      );
      this.completed.emit({ succeededNoteIds: succeeded, failedNoteIds: failed });
      return;
    }

    if (added.length === 0 && alreadyCount > 0) {
      this.dismissSuccessToastForWriting(writing);
      this.toast.info(
        `${alreadyCount === 1 ? 'This source is' : 'These sources are'} already kept with “${writing.name}”`
      );
    } else {
      const alreadySuffix = alreadyCount ? ` · ${alreadyCount} already there` : '';
      this.toast.success(
        `Kept ${succeeded.length} ${label} with “${writing.name}”${alreadySuffix}`
      );
    }

    if (this.openWritingAfterAdd()) {
      void this.router.navigateByUrl(`/studio?writingId=${encodeURIComponent(writing.id)}`);
    }

    this.completed.emit({ succeededNoteIds: succeeded, failedNoteIds: [] });
    this.restoreFocusAfterClose();
  }

  private dismissSuccessToastForWriting(writing: WritingDto): void {
    const writingLabel = `with “${writing.name}”`;
    for (const toast of this.toast.toasts()) {
      if (toast.type === 'success' && toast.message.includes(writingLabel)) {
        this.toast.dismiss(toast.id);
      }
    }
  }

  private restoreFocusAfterClose(): void {
    const target = this.returnFocusElement;
    if (!target) return;

    setTimeout(() => {
      if (target.isConnected && !this.host.nativeElement.contains(target)) {
        target.focus({ preventScroll: true });
      }
    });
  }

  private uniqueNoteIds(): string[] {
    return [...new Set(this.noteIds().filter((noteId) => !!noteId))];
  }
}
