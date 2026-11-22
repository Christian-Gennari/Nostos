import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';

import type { TopicDto, TopicKindDto } from '../../core/services/topics.service';
import { TopicsService } from '../../core/services/topics.service';
import { ToastService } from '../../core/services/toast.service';
import { ButtonComponent } from '../../ui/button/button.component';
import { InputDirective } from '../../ui/form-control/form-control.directive';
import { NostosIconComponent } from '../../ui/icon/nostos-icon.component';
import {
  SettingsPopoverComponent,
  type SettingsPopoverDismissal,
} from '../../ui/settings-popover/settings-popover.component';
import { BrainStateService } from '../brain-state.service';
import { KindChoiceListComponent } from './kind-choice-list.component';
import { MAX_TOPIC_KINDS, firstFreeKindColor, normalizeName } from './kinds.helpers';
import { TopicKindPillComponent } from './topic-kind-pill.component';

/**
 * Set a topic's kind from its own page, or from the map card's pill (which
 * hands the page a topic and an anchor). The pill shows the current kind, or a
 * quiet "Add a kind" line; the panel lists every kind with its colour, "No
 * kind", "New kind…" and "Manage kinds".
 *
 * Every choice here is the reader's: `PUT /api/topics/kind` stores source
 * `reader`, so later AI runs leave it alone. A new kind is offered only while
 * the library has room for one (twelve, one per palette colour).
 */
@Component({
  selector: 'app-topic-kind-picker',
  standalone: true,
  imports: [
    RouterLink,
    ButtonComponent,
    InputDirective,
    NostosIconComponent,
    SettingsPopoverComponent,
    KindChoiceListComponent,
    TopicKindPillComponent,
  ],
  templateUrl: './topic-kind-picker.component.html',
  styleUrl: './topic-kind-picker.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TopicKindPickerComponent {
  private readonly topics = inject(TopicsService);
  private readonly toast = inject(ToastService);
  private readonly state = inject(BrainStateService);

  readonly topic = input.required<TopicDto>();
  /** `false` lets a page own the panel: the map card's pill is the trigger. */
  readonly showTrigger = input(true);
  /** Opens the panel when the trigger lives elsewhere. */
  readonly open = input(false);
  /** The element the panel hangs from when `showTrigger` is false. */
  readonly origin = input<HTMLElement | null>(null);

  readonly closed = output<void>();

  private readonly localOpen = signal(false);
  private readonly trigger = viewChild<ElementRef<HTMLButtonElement>>('trigger');

  readonly kinds = this.state.topicKinds;
  readonly kindsLoaded = computed(() => this.state.kindsLoaded());
  readonly kindsFailed = computed(() => this.state.kindsFailed());
  readonly currentKind = computed<TopicKindDto | null>(() => {
    const id = this.topic().kindId;
    return id ? this.kinds().find((kind) => kind.id === id) ?? null : null;
  });
  readonly isOpen = computed(() => (this.showTrigger() ? this.localOpen() : this.open()));
  readonly anchor = computed(() => this.origin() ?? this.trigger()?.nativeElement ?? null);
  /**
   * The topic has a kind, but the list that could name it did not arrive. The
   * trigger must say that rather than "Add a kind": the topic is not untyped,
   * and the invitation would overwrite a kind the reader already has.
   */
  readonly kindUnavailable = computed(
    () => !!this.topic().kindId && !this.kindsLoaded() && !this.currentKind(),
  );
  readonly triggerLabel = computed(() => {
    const kind = this.currentKind();
    if (kind) return `Change kind for ${this.topic().name}: ${kind.name}`;
    if (this.kindUnavailable()) return `Kind for ${this.topic().name} is unavailable`;
    return `Add a kind to ${this.topic().name}`;
  });
  readonly maxKinds = MAX_TOPIC_KINDS;
  readonly atKindLimit = computed(() => this.kinds().length >= MAX_TOPIC_KINDS);
  /** Why "New kind…" cannot be offered, or null. */
  readonly createBlocked = computed(() => {
    if (!this.kindsLoaded()) return 'Your kinds have not loaded yet. Try again first.';
    if (this.atKindLimit()) return `All ${MAX_TOPIC_KINDS} kinds are in use. Delete one to add another.`;
    return null;
  });
  readonly nextColor = computed(() => firstFreeKindColor(this.kinds()));

  readonly creating = signal(false);
  readonly draftName = signal('');
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);

  // `#createTrigger` sits on a `button[appButton]`, which is a component: the
  // plain query would hand back the ButtonComponent, whose `nativeElement` is
  // undefined. Reading the ElementRef explicitly is what makes it focusable.
  private readonly createTrigger = viewChild<string, ElementRef<HTMLElement>>('createTrigger', {
    read: ElementRef,
  });
  private readonly createField = viewChild<ElementRef<HTMLInputElement>>('createField');
  private panelWasOpen = false;
  /** Why the panel just closed; a press elsewhere must keep focus where it went. */
  private dismissal: SettingsPopoverDismissal | null = null;

  constructor() {
    this.state.ensureKindsLoaded();
    // A panel opened for another topic starts with no half-typed kind.
    effect(() => {
      this.topic().id;
      untracked(() => {
        this.creating.set(false);
        this.draftName.set('');
        this.error.set(null);
      });
    });
    // The panel closed under the reader (a choice, Escape): the field it was in
    // is gone, so put focus back on the pill instead of the page body. A press
    // outside is the exception: the reader chose where focus should go, and
    // pulling it back to the pill would steal the control they just pressed.
    effect(() => {
      const open = this.isOpen();
      untracked(() => {
        if (open) {
          this.panelWasOpen = true;
          return;
        }
        if (!this.panelWasOpen) return;
        this.panelWasOpen = false;
        const reason = this.dismissal;
        this.dismissal = null;
        if (reason === 'outside') return;
        if (this.showTrigger() && this.trigger()?.nativeElement.isConnected) {
          this.trigger()?.nativeElement.focus();
        }
      });
    });
  }

  toggle(): void {
    if (this.busy()) return;
    this.localOpen.update((open) => !open);
    if (!this.localOpen()) {
      this.dismissal = 'close';
      this.resetDraft();
    }
  }

  /**
   * Closes the panel and says why. `outside` is the one reason focus must not
   * come back here: a press elsewhere is the reader moving focus themselves.
   */
  close(reason: SettingsPopoverDismissal = 'close'): void {
    this.dismissal = reason;
    this.localOpen.set(false);
    this.resetDraft();
    this.closed.emit();
  }

  startCreating(): void {
    if (this.createBlocked() || this.busy()) return;
    this.creating.set(true);
    this.draftName.set('');
    this.error.set(null);
    this.focusCreateField();
  }

  cancelCreating(): void {
    this.creating.set(false);
    this.draftName.set('');
    this.error.set(null);
    // The button that opened the form only exists once the form closes.
    window.requestAnimationFrame(() => this.createTrigger()?.nativeElement.focus());
  }

  /** The kind list failed to load: ask for it again. */
  retryKinds(): void {
    this.state.reloadKinds();
  }

  private focusCreateField(): void {
    window.requestAnimationFrame(() => this.createField()?.nativeElement.focus());
  }

  choose(kindId: string | null): void {
    if (this.busy()) return;
    const topic = this.topic();
    this.busy.set(true);
    this.error.set(null);
    this.topics.setTopicsKind([topic.id], kindId).subscribe({
      next: (result) => {
        this.busy.set(false);
        this.state.replaceTopic({ ...topic, kindId: result.kindId, kindSource: result.kindSource });
        this.close();
      },
      error: (error: unknown) => {
        this.busy.set(false);
        this.error.set(serverMessage(error) ?? 'Could not set the kind. Try again.');
      },
    });
  }

  createKind(): void {
    if (this.busy() || this.createBlocked()) return;
    const name = this.draftName().trim();
    if (!name) {
      this.error.set('A kind needs a name.');
      return;
    }
    if (this.kinds().some((kind) => normalizeName(kind.name) === normalizeName(name))) {
      this.error.set('A kind with this name already exists.');
      return;
    }
    const colorKey = this.nextColor();
    if (!colorKey) {
      this.error.set(`All ${MAX_TOPIC_KINDS} kinds are in use. Delete one first.`);
      return;
    }
    const topic = this.topic();
    this.busy.set(true);
    this.error.set(null);
    // Two writes: the kind exists first, then it is set on this topic. If the
    // second fails, the kind is still real and must be visible to choose.
    this.topics.createKind({ name, colorKey }).subscribe({
      next: (created) => {
        this.state.reloadKinds();
        this.topics.setTopicsKind([topic.id], created.id).subscribe({
          next: (result) => {
            this.busy.set(false);
            this.state.replaceTopic({ ...topic, kindId: result.kindId, kindSource: result.kindSource });
            this.toast.success(`Added “${created.name}”`);
            this.close();
          },
          error: () => {
            this.busy.set(false);
            this.creating.set(false);
            this.error.set(`Added “${created.name}”, but it could not be set on this topic. Choose it above.`);
          },
        });
      },
      error: (error: unknown) => {
        this.busy.set(false);
        this.error.set(serverMessage(error) ?? 'Could not add the kind. Try again.');
      },
    });
  }

  private resetDraft(): void {
    this.creating.set(false);
    this.draftName.set('');
    this.error.set(null);
  }
}

function serverMessage(error: unknown): string | null {
  if (!(error instanceof HttpErrorResponse)) return null;
  const body = error.error as { error?: unknown } | null;
  return typeof body?.error === 'string' ? body.error : null;
}
