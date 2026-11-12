import { ChangeDetectionStrategy, Component, OnInit, inject } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';

/**
 * `/approve`: where a Carrel device's QR points, because the device builds its
 * approval URL as `{server origin}/approve` (the protocol crate). Nostos's page
 * is `/pair`, so this keeps the code and hands over. The server also answers
 * `/approve` with this redirect; this route covers a frontend served alone.
 */
@Component({
  selector: 'app-pair-approve-redirect',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: '',
})
export class PairApproveRedirectPage implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  ngOnInit(): void {
    const code = this.route.snapshot.queryParamMap.get('code');
    void this.router.navigate(['/pair'], {
      queryParams: code ? { code } : {},
      replaceUrl: true,
    });
  }
}
