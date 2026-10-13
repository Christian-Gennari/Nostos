import { Component, OnInit, inject, signal } from '@angular/core';
import { ReadAloudService } from '../core/services/read-aloud.service';
import { NostosIconComponent } from '../ui/icon/nostos-icon.component';
import { NoticeComponent } from '../ui/notice/notice.component';

export const READ_ALOUD_GUIDE_URL =
  'https://github.com/Christian-Gennari/Nostos/blob/main/docs/read-aloud.md';

/**
 * Settings → AI connections → Read aloud voice (SelfHosted).
 *
 * Says whether this server has a natural voice connected and, when it has
 * not, how its owner connects one. The voice is set in the server's
 * configuration, so this card reports and explains; it does not edit.
 */
@Component({
  selector: 'app-read-aloud-settings',
  standalone: true,
  imports: [NostosIconComponent, NoticeComponent],
  templateUrl: './read-aloud-settings.component.html',
  styleUrl: './read-aloud-settings.component.css',
})
export class ReadAloudSettingsComponent implements OnInit {
  private readonly readAloud = inject(ReadAloudService);

  /** Null while the server has not answered. */
  readonly connected = signal<boolean | null>(null);
  readonly guideUrl = READ_ALOUD_GUIDE_URL;

  ngOnInit(): void {
    // English is asked for because every supported speech model reads it; the
    // answer is about the connection, not about a language.
    this.readAloud.status('en').subscribe((status) => this.connected.set(status.available));
  }
}
