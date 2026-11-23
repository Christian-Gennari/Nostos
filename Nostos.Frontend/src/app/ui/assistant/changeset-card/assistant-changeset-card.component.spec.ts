import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { AssistantChangesetCardComponent } from './assistant-changeset-card.component';
import type { AssistantChangesetDto } from '../assistant.service';

@Component({
  standalone: true,
  imports: [AssistantChangesetCardComponent],
  template: `
    <app-assistant-changeset-card
      [changeset]="changeset"
      (stepToggled)="step = $event"
      (stepsToggled)="steps = $event"
    />
  `,
})
class HostComponent {
  changeset = reviewWithKinds();
  step: { index: number; included: boolean } | null = null;
  steps: { indexes: number[]; included: boolean } | null = null;
}

function reviewWithKinds(): AssistantChangesetDto {
  return {
    changesetId: 'c1',
    status: 'review',
    steps: [
      {
        index: 0,
        summary: 'Create the topic kind “School of thought”',
        included: true,
        dependsOn: [],
        topicKind: {
          kindKey: 'kind-school',
          kindName: 'School of thought',
          colorKey: 'kind-7',
          createsKind: true,
          isNewKind: true,
          topicId: null,
          topicName: null,
          previousKindName: null,
        },
      },
      {
        index: 1,
        summary: 'Set “Freire” to “School of thought”',
        included: true,
        dependsOn: [0],
        topicKind: {
          kindKey: 'kind-school',
          kindName: 'School of thought',
          colorKey: 'kind-7',
          createsKind: false,
          isNewKind: true,
          topicId: 't1',
          topicName: 'Freire',
          previousKindName: null,
        },
      },
      {
        index: 2,
        summary: 'Set “Montessori” to “School of thought”',
        included: true,
        dependsOn: [0],
        topicKind: {
          kindKey: 'kind-school',
          kindName: 'School of thought',
          colorKey: 'kind-7',
          createsKind: false,
          isNewKind: true,
          topicId: 't2',
          topicName: 'Montessori',
          previousKindName: null,
        },
      },
      {
        index: 3,
        summary: 'Set “Paulo Freire” to “Person”',
        included: true,
        dependsOn: [],
        topicKind: {
          kindKey: '11111111-0000-0000-0000-000000000001',
          kindName: 'Person',
          colorKey: 'kind-1',
          createsKind: false,
          isNewKind: false,
          topicId: 't3',
          topicName: 'Paulo Freire',
          previousKindName: null,
        },
      },
    ],
    diff: {
      collections: [],
      memberships: [],
      bookEdits: [],
      counts: {
        collectionsCreated: 0,
        collectionsDeleted: 0,
        collectionsUpdated: 0,
        booksTouched: 0,
        membershipsAdded: 0,
        membershipsRemoved: 0,
        booksLosingMembership: 0,
        bookEdits: 0,
        topicKindsCreated: 1,
        topicKindChanges: 3,
        topicKindsGiven: 3,
        topicKindsChanged: 0,
        topicKindsCleared: 0,
      },
      topicKinds: [
        {
          id: 'kind-school',
          kind: 'created',
          name: 'School of thought',
          previousName: null,
          colorKey: 'kind-7',
          topicCount: 2,
          stepIndex: 0,
        },
      ],
      topicKindAssignments: [
        {
          topicId: 't1',
          topicName: 'Freire',
          kindId: 'kind-school',
          kindName: 'School of thought',
          previousKindName: null,
          source: 'assistant',
          stepIndex: 1,
          kindColorKey: 'kind-7',
        },
        {
          topicId: 't2',
          topicName: 'Montessori',
          kindId: 'kind-school',
          kindName: 'School of thought',
          previousKindName: null,
          source: 'assistant',
          stepIndex: 2,
          kindColorKey: 'kind-7',
        },
        {
          topicId: 't3',
          topicName: 'Paulo Freire',
          kindId: '11111111-0000-0000-0000-000000000001',
          kindName: 'Person',
          previousKindName: null,
          source: 'assistant',
          stepIndex: 3,
          kindColorKey: 'kind-1',
        },
      ],
    },
    diffHash: 'hash',
    actionToken: 'token',
    reviewReasons: [],
    canUndo: false,
  };
}

/** The same review with some steps ticked or unticked, as a preview returns it. */
function withSteps(
  changeset: AssistantChangesetDto,
  included: Record<number, boolean>,
): AssistantChangesetDto {
  return {
    ...changeset,
    steps: changeset.steps.map((step) =>
      included[step.index] === undefined ? step : { ...step, included: included[step.index] }),
  };
}

describe('AssistantChangesetCardComponent topic kinds', () => {
  function setup(changeset: AssistantChangesetDto = reviewWithKinds()) {
    const fixture = TestBed.createComponent(HostComponent);
    fixture.componentInstance.changeset = changeset;
    fixture.detectChanges();
    return fixture;
  }

  it('groups the changes by kind, with a new kind first and every kind’s colour', () => {
    const fixture = setup();
    const element = fixture.nativeElement as HTMLElement;
    const groups = [...element.querySelectorAll('.changeset__kind-group')];

    expect(groups).toHaveLength(2);
    expect(groups[0].textContent).toContain('School of thought');
    expect(groups[0].textContent).toContain('(2)');
    expect(groups[0].textContent).toContain('New');
    expect(groups[0].querySelector('.changeset__kind-swatch')?.getAttribute('style'))
      .toContain('--map-kind-7');
    // Person is an existing kind: it still shows its palette colour.
    expect(groups[1].querySelector('.changeset__kind-swatch')?.getAttribute('style'))
      .toContain('--map-kind-1');
  });

  it('one group checkbox ticks or unticks every topic in the group', () => {
    const fixture = setup();
    const element = fixture.nativeElement as HTMLElement;
    const first = element.querySelector('.changeset__kind-group input') as HTMLInputElement;

    first.checked = false;
    first.dispatchEvent(new Event('change'));

    expect(fixture.componentInstance.steps).toEqual({ indexes: [1, 2], included: false });
  });

  it('an unticked topic stays in its group instead of moving to the plain step list', () => {
    const fixture = setup(withSteps(reviewWithKinds(), { 1: false }));
    const element = fixture.nativeElement as HTMLElement;
    (element.querySelector('[data-testid="assistant-changeset-details-toggle"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    const topics = [...element.querySelectorAll('[data-testid="assistant-changeset-topic-toggle"]')]
      .map((input) => (input as HTMLInputElement).checked);
    expect(topics).toEqual([false, true, true]);

    const groupNames = [...element.querySelectorAll('.changeset__kind-group')].map((group) => group.textContent);
    expect(groupNames[0]).toContain('(1/2)');
    // The active preview only names what Apply would do.
    expect(groupNames[0]).toContain('Montessori');
    expect(groupNames[0]).not.toContain('Freire');
    // The topic did not become one more plain step line.
    expect(element.querySelectorAll('[data-testid="assistant-changeset-steps"] li')).toHaveLength(0);
  });

  it('shows an indeterminate group box when only part of the group is ticked', () => {
    const fixture = setup(withSteps(reviewWithKinds(), { 1: false }));
    const element = fixture.nativeElement as HTMLElement;
    const group = element.querySelector('.changeset__kind-group') as HTMLElement;
    const box = group.querySelector('input') as HTMLInputElement;

    expect(box.indeterminate).toBe(true);
    expect(box.checked).toBe(false);
    expect(group.textContent).toContain('(1/2)');
  });

  it('lists only the applied topics, as plain rows, once the card is applied', () => {
    const changeset = partialClaimReview();
    const applied: AssistantChangesetDto = {
      ...changeset,
      status: 'applied',
      canUndo: true,
    };
    const fixture = setup(applied);
    const element = fixture.nativeElement as HTMLElement;
    (element.querySelector('[data-testid="assistant-changeset-details-toggle"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    const details = element.querySelector('[data-testid="assistant-changeset-details"]') as HTMLElement;
    // No checkbox can offer a change the applied card can no longer make.
    expect(details.querySelectorAll('input')).toHaveLength(0);

    const topics = [...details.querySelectorAll('.changeset__topic-name')]
      .map((topic) => topic.textContent?.trim());
    expect(topics).toContain('Montessori');
    expect(topics).toContain('Paulo Freire');
    expect(topics).not.toContain('Freire');
    expect(details.querySelectorAll('li.changeset__step--off')).toHaveLength(0);
  });

  it('an unticked group stays in place, unticked, and keeps Apply available for the rest', () => {
    const fixture = setup(withSteps(reviewWithKinds(), { 0: false, 1: false, 2: false }));
    const element = fixture.nativeElement as HTMLElement;

    const groups = [...element.querySelectorAll('.changeset__kind-group')];
    expect(groups).toHaveLength(2);
    expect(groups[0].classList).toContain('changeset__kind-group--off');
    expect((groups[0].querySelector('input') as HTMLInputElement).checked).toBe(false);

    const apply = element.querySelector('[data-testid="assistant-changeset-apply"]') as HTMLButtonElement;
    expect(apply.disabled).toBe(false);
  });

  it('disables Apply and says every step is left out when a created kind loses all its topics', () => {
    const fixture = setup(withSteps(reviewWithKinds(), { 1: false, 2: false, 3: false }));
    const element = fixture.nativeElement as HTMLElement;

    const headline = element.querySelector('[data-testid="assistant-changeset-headline"]')!.textContent;
    expect(headline).toContain('Every step is left out');

    const apply = element.querySelector('[data-testid="assistant-changeset-apply"]') as HTMLButtonElement;
    expect(apply.disabled).toBe(true);
  });

  it('counts the kinds in the headline instead of saying nothing changed', () => {
    const fixture = setup();
    const headline = (fixture.nativeElement as HTMLElement)
      .querySelector('[data-testid="assistant-changeset-headline"]')!.textContent;

    expect(headline).toContain('3 topics a kind');
    expect(headline).toContain('1 topic kind');
  });

  it('says removing the kind is a removal, not a gift', () => {
    const changeset = reviewWithKinds();
    changeset.diff.counts = {
      ...changeset.diff.counts,
      topicKindsCreated: 0,
      topicKindChanges: 2,
      topicKindsGiven: 0,
      topicKindsChanged: 0,
      topicKindsCleared: 2,
    };
    const fixture = setup(changeset);
    const headline = (fixture.nativeElement as HTMLElement)
      .querySelector('[data-testid="assistant-changeset-headline"]')!.textContent;

    expect(headline).toContain('remove the kind from 2 topics');
    expect(headline).not.toContain('a kind');
  });

  it('describes a mixed run in each kind’s own words', () => {
    const changeset = reviewWithKinds();
    changeset.diff.counts = {
      ...changeset.diff.counts,
      topicKindsCreated: 0,
      topicKindChanges: 5,
      topicKindsGiven: 2,
      topicKindsChanged: 2,
      topicKindsCleared: 1,
    };
    const fixture = setup(changeset);
    const headline = (fixture.nativeElement as HTMLElement)
      .querySelector('[data-testid="assistant-changeset-headline"]')!.textContent;

    expect(headline).toContain('give 2 topics a kind, change the kind of 2 topics and remove the kind from 1 topic');
  });

  it('says nothing is left to apply, and disables Apply, when the server kept every step', () => {
    // Every staged topic was claimed before the apply: the server keeps them,
    // says why, and the diff is empty (issue #1770 review, round 3).
    const fixture = setup(allKeptReview());
    const element = fixture.nativeElement as HTMLElement;

    const headline = element.querySelector('[data-testid="assistant-changeset-headline"]')!.textContent;
    expect(headline).toBe('Nothing left to apply: these changes are already in the library.');
    // Never "This will ." — the empty sentence the reader used to see.
    expect(headline).not.toMatch(/will\s*\./);

    const apply = element.querySelector('[data-testid="assistant-changeset-apply"]') as HTMLButtonElement;
    expect(apply.disabled).toBe(true);
  });

  it('shows every kept topic unticked, with the reason, instead of ticking it', () => {
    const fixture = setup(allKeptReview());
    const element = fixture.nativeElement as HTMLElement;
    (element.querySelector('[data-testid="assistant-changeset-details-toggle"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    const details = element.querySelector('[data-testid="assistant-changeset-details"]') as HTMLElement;
    const ticks = [...details.querySelectorAll('[data-testid="assistant-changeset-topic-toggle"]')]
      .map((input) => (input as HTMLInputElement).checked);
    expect(ticks).toEqual([false, false, false]);

    const reasons = [...details.querySelectorAll('[data-testid="assistant-changeset-topic-skipped"]')]
      .map((note) => note.textContent?.trim());
    expect(reasons).toEqual([
      'kept: you set this kind yourself',
      'kept: you set this kind yourself',
      'kept: you set this kind yourself',
    ]);
  });

  it('counts only the topics that are still left after a partial claim', () => {
    const fixture = setup(partialClaimReview());
    const element = fixture.nativeElement as HTMLElement;

    const headline = element.querySelector('[data-testid="assistant-changeset-headline"]')!.textContent;
    expect(headline).toContain('2 topics a kind');
    expect(headline).not.toContain('3 topics a kind');

    // The group still shows both topics: the kept one unticked, with its reason.
    const group = element.querySelector('.changeset__kind-group') as HTMLElement;
    expect(group.textContent).toContain('(1/2)');

    const apply = element.querySelector('[data-testid="assistant-changeset-apply"]') as HTMLButtonElement;
    expect(apply.disabled).toBe(false);

    (element.querySelector('[data-testid="assistant-changeset-details-toggle"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    const details = element.querySelector('[data-testid="assistant-changeset-details"]') as HTMLElement;
    const freire = [...details.querySelectorAll('li.changeset__topic--off')]
      .find((row) => row.textContent?.includes('Freire')) as HTMLElement;
    expect((freire.querySelector('input') as HTMLInputElement).checked).toBe(false);
    expect(freire.querySelector('[data-testid="assistant-changeset-topic-skipped"]')?.textContent)
      .toContain('kept: you set this kind yourself');
  });

  it('lists only the applied topics once the card is applied, even if a step is still ticked', () => {
    // The diff is the truth after an apply. A step the server kept (the reader
    // set that kind themselves) must not be listed as applied.
    const review = partialClaimReview(true);
    const fixture = setup({ ...review, status: 'applied', canUndo: true });
    const element = fixture.nativeElement as HTMLElement;
    (element.querySelector('[data-testid="assistant-changeset-details-toggle"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    const details = element.querySelector('[data-testid="assistant-changeset-details"]') as HTMLElement;
    const topics = [...details.querySelectorAll('.changeset__topic-name')]
      .map((topic) => topic.textContent?.trim());
    expect(topics).toContain('Montessori');
    expect(topics).toContain('Paulo Freire');
    expect(topics).not.toContain('Freire');
    expect(details.querySelectorAll('input')).toHaveLength(0);
  });

  it('still says a sentence when the counts are all zero', () => {
    // A server whose counts and lists disagree must not produce "This will .".
    const changeset = reviewWithKinds();
    changeset.diff = {
      collections: [],
      memberships: [],
      bookEdits: [],
      counts: {
        collectionsCreated: 0,
        collectionsDeleted: 0,
        collectionsUpdated: 0,
        booksTouched: 0,
        membershipsAdded: 0,
        membershipsRemoved: 0,
        booksLosingMembership: 0,
        bookEdits: 0,
        topicKindsCreated: 0,
        topicKindChanges: 0,
        topicKindsGiven: 0,
        topicKindsChanged: 0,
        topicKindsCleared: 0,
      },
      topicKinds: [],
      topicKindAssignments: [
        {
          topicId: 't1',
          topicName: 'Freire',
          kindId: 'kind-school',
          kindName: 'School of thought',
          previousKindName: null,
          source: 'assistant',
          stepIndex: 1,
          kindColorKey: 'kind-7',
        },
      ],
    };
    const fixture = setup(changeset);
    const headline = (fixture.nativeElement as HTMLElement)
      .querySelector('[data-testid="assistant-changeset-headline"]')!.textContent;

    expect(headline).toBe('This will change nothing.');
  });

  it('shows the reason on a kept step that has no kind group of its own', () => {
    const changeset = reviewWithKinds();
    const kept: AssistantChangesetDto = {
      ...changeset,
      steps: [
        {
          index: 0,
          summary: 'Create the topic kind “School of thought”',
          included: false,
          dependsOn: [],
          skippedReason: 'kept: no topic still needs this kind',
          topicKind: {
            kindKey: 'kind-school',
            kindName: 'School of thought',
            colorKey: 'kind-7',
            createsKind: true,
            isNewKind: true,
            topicId: null,
            topicName: null,
            previousKindName: null,
          },
        },
      ],
      diff: {
        collections: [],
        memberships: [],
        bookEdits: [],
        counts: {
          collectionsCreated: 0,
          collectionsDeleted: 0,
          collectionsUpdated: 0,
          booksTouched: 0,
          membershipsAdded: 0,
          membershipsRemoved: 0,
          booksLosingMembership: 0,
          bookEdits: 0,
          topicKindsCreated: 0,
          topicKindChanges: 0,
          topicKindsGiven: 0,
          topicKindsChanged: 0,
          topicKindsCleared: 0,
        },
        topicKinds: [],
        topicKindAssignments: [],
      },
    };
    const fixture = setup(kept);
    const element = fixture.nativeElement as HTMLElement;

    const notes = [...element.querySelectorAll('[data-testid="assistant-changeset-step-skipped"]')]
      .map((note) => note.textContent?.trim());
    expect(notes).toEqual(['kept: no topic still needs this kind']);
    const apply = element.querySelector('[data-testid="assistant-changeset-apply"]') as HTMLButtonElement;
    expect(apply.disabled).toBe(true);
  });
});

const ZERO_COUNTS = {
  collectionsCreated: 0,
  collectionsDeleted: 0,
  collectionsUpdated: 0,
  booksTouched: 0,
  membershipsAdded: 0,
  membershipsRemoved: 0,
  booksLosingMembership: 0,
  bookEdits: 0,
  topicKindsCreated: 0,
  topicKindChanges: 0,
  topicKindsGiven: 0,
  topicKindsChanged: 0,
  topicKindsCleared: 0,
};

/** The review with every step kept by the server: nothing left to apply. */
function allKeptReview(): AssistantChangesetDto {
  const base = reviewWithKinds();
  return {
    ...base,
    steps: base.steps.map((step) => ({
      ...step,
      included: false,
      skippedReason: 'kept: you set this kind yourself',
    })),
    diff: {
      collections: [],
      memberships: [],
      bookEdits: [],
      counts: ZERO_COUNTS,
      topicKinds: [],
      topicKindAssignments: [],
    },
  };
}

/**
 * The review after the reader claimed Freire's kind: the server kept that step
 * and the diff no longer contains it. `stepsStillTicked` keeps the step ticked,
 * as a server that has not learned to mark it would leave it.
 */
function partialClaimReview(stepsStillTicked = false): AssistantChangesetDto {
  const base = reviewWithKinds();
  return {
    ...base,
    steps: base.steps.map((step) =>
      step.index === 1 && !stepsStillTicked
        ? { ...step, included: false, skippedReason: 'kept: you set this kind yourself' }
        : step,
    ),
    diff: {
      ...base.diff,
      counts: {
        ...base.diff.counts,
        topicKindChanges: 2,
        topicKindsGiven: 2,
      },
      topicKinds: base.diff.topicKinds!.map((kind) => ({ ...kind, topicCount: 1 })),
      topicKindAssignments: base.diff.topicKindAssignments!.filter(
        (assignment) => assignment.topicId !== 't1',
      ),
    },
  };
}
