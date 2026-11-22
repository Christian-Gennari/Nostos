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
    expect(groupNames[0]).toContain('(2)');
    expect(groupNames[0]).toContain('Freire');
    // The topic did not become one more plain step line.
    expect(element.querySelectorAll('[data-testid="assistant-changeset-steps"] li')).toHaveLength(0);
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
});
