import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';

import { ImportDropZoneComponent } from '../../ui/import-drop-zone/import-drop-zone.component';
import { ButtonComponent } from '../../ui/button/button.component';
import { SettingsSection } from '../settings-sections';

/**
 * Settings → Import: every way into Nostos from somewhere else, in one place.
 *
 * The app-specific steps (Apple Books, Readwise, Calibre, the Nostos archive)
 * are the shared drop zone's own help lines; this component only frames them
 * and offers the one jump into the server's media folders.
 */
@Component({
  selector: 'app-import-settings',
  standalone: true,
  imports: [ButtonComponent, ImportDropZoneComponent],
  templateUrl: './import-settings.component.html',
  styleUrls: ['../settings-section.host.css', '../settings-section.shared.css', './import-settings.component.css', '../settings-section.responsive.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ImportSettingsComponent {
  /** A SelfHosted server that reads a folder of books (see External media). */
  readonly mediaFolders = input(false);

  /** Ask the page to open another settings section. */
  readonly openSection = output<SettingsSection>();
}
