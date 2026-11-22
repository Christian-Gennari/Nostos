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
import { SettingsPopoverComponent } from '../../ui/settings-popover/settings-popover.component';
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
  readonly currentKind = computed<TopicKindDto | null>(() => {
    const id = this.topic().kindId;
    return id ? this.kinds().find((kind) => kind.id === id) ?? null : null;
  });
  readonly isOpen = computed(() => (this.showTrigger() ? this.localOpen() : this.open()));
  readonly anchor = computed(() => this.origin() ?? this.trigger()?.nativeElement ?? null);
  readonly triggerLabel = computed(() => {
    const kind = this.currentKind();
    return kind
      ? `Change kind for ${this.topic().name}: ${kind.name}`
      : `Add a kind to ${this.topic().name}`;
  });
  readonly maxKinds = MAX_TOPIC_KINDS;
  readonly atKindLimit = computed(() => this.kinds().length >= MAX_TOPIC_KINDS);
  readonly nextColor = computed(() => firstFreeKindColor(this.kinds()));

  readonly creating = signal(false);
  readonly draftName = signal('');
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);

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
  }

  toggle(): void {
    if (this.busy()) return;
    this.localOpen.update((open) => !open);
    if (!this.localOpen()) this.resetDraft();
  }

  close(): void {
    this.localOpen.set(false);
    this.resetDraft();
    this.closed.emit();
  }

  startCreating(): void {
    if (this.atKindLimit() || this.busy()) return;
    this.creating.set(true);
    this.draftName.set('');
    this.error.set(null);
  }

  cancelCreating(): void {
    this.creating.set(false);
    this.draftName.set('');
    this.error.set(null);
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
    if (this.busy()) return;
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
