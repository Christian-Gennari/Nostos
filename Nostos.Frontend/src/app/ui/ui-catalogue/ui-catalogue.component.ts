/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: GPL-3.0-or-later
 */
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { Theme, ThemeService } from '../../core/services/theme.service';
import { ToastService, type ToastType } from '../../core/services/toast.service';
import { BadgeComponent } from '../badge/badge.component';
import { ButtonComponent } from '../button/button.component';
import { ChipComponent } from '../chip/chip.component';
import { DialogActionsComponent } from '../dialog-actions/dialog-actions.component';
import { InputDirective, TextareaDirective } from '../form-control/form-control.directive';
import { DropdownComponent, type DropdownOption } from '../dropdown/dropdown.component';
import { FormFieldComponent } from '../form-field/form-field.component';
import { IconButtonComponent } from '../icon-button/icon-button.component';
import { ModalShell } from '../modal-shell/modal-shell.component';
import { LoadingIndicatorComponent } from '../loading-indicator/loading-indicator.component';
import { SwitchComponent } from '../switch/switch.component';
import { ViewToggleComponent, type ViewToggleOption } from '../view-toggle/view-toggle.component';

/**
 * Inspectable reference fixture for Nostos UI v1.
 *
 * This is deliberately a lightweight application route rather than Storybook:
 * every example runs inside the same Angular app, token graph and theme service
 * as production surfaces, and Playwright exercises it at the app's desktop and
 * mobile widths.
 */
@Component({
  selector: 'app-ui-catalogue',
  standalone: true,
  imports: [
    BadgeComponent,
    ButtonComponent,
    ChipComponent,
    DialogActionsComponent,
    FormFieldComponent,
    IconButtonComponent,
    InputDirective,
    ModalShell,
    LoadingIndicatorComponent,
    DropdownComponent,
    SwitchComponent,
    ViewToggleComponent,
    TextareaDirective,
  ],
  templateUrl: './ui-catalogue.component.html',
  styleUrl: './ui-catalogue.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class UiCatalogueComponent {
  private readonly themeService = inject(ThemeService);
  private readonly toastService = inject(ToastService);

  readonly theme = this.themeService.theme;
  readonly selectedTab = signal<'primitives' | 'patterns'>('primitives');
  /** Deliberately a plain string: the component is not generic, so the demo does not
      pretend to narrow the value it receives. */
  readonly selectedView = signal('list');
  readonly viewToggleOptions = [
    { value: 'list', icon: 'list-bullets', label: 'List view' },
    { value: 'grid', icon: 'squares-four', label: 'Grid view' },
  ] satisfies readonly ViewToggleOption[];
  readonly modalOpen = signal(false);
  readonly catalogueDropdownValue = signal('epub');
  readonly dropdownOptions = [
    { value: 'epub', label: 'EPUB' },
    { value: 'pdf', label: 'PDF' },
    { value: 'physical', label: 'Physical' },
    { value: 'unavailable', label: 'Unavailable format', disabled: true },
  ] satisfies readonly DropdownOption[];
  readonly longDropdownOptions = Array.from({ length: 18 }, (_, index) => ({
    value: `chapter-${index + 1}`,
    label: `Chapter ${index + 1}`,
  })) satisfies readonly DropdownOption[];

  showCatalogueToast(type: ToastType): void {
    const messages: Record<ToastType, string> = {
      success: 'Book details saved.',
      error: 'Could not save book details.',
      info: 'Metadata refresh started.',
    };
    this.toastService.show(messages[type], type, 30_000);
  }

  showLongCatalogueToast(): void {
    this.toastService.show(
      'A long notification wraps cleanly without pushing the dismiss control outside the stable toast width.',
      'info',
      30_000,
    );
  }

  showStackedCatalogueToasts(): void {
    this.toastService.show('Book details saved.', 'success', 30_000);
    this.toastService.show('Metadata refresh started.', 'info', 30_000);
    this.toastService.show('Could not save book details.', 'error', 30_000);
  }

  setTheme(theme: Theme): void {
    this.themeService.setTheme(theme);
  }
}
