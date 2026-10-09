import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { ToastService } from '../../core/services/toast.service';
import { IconButtonComponent } from '../icon-button/icon-button.component';
import { NostosIconComponent } from '../icon/nostos-icon.component';

@Component({
  selector: 'app-toast-container',
  standalone: true,
  imports: [IconButtonComponent, NostosIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="toast-container">
      @for (toast of toastService.toasts(); track toast.id) {
        <div
          class="toast nostos-accent-rail"
          [class.toast-success]="toast.type === 'success'"
          [class.toast-error]="toast.type === 'error'"
          [class.toast-info]="toast.type === 'info'"
          animate.enter="toast-enter"
          animate.leave="toast-leave"
        >
          <div
            class="toast-announcement"
            [attr.role]="toast.type === 'error' ? 'alert' : 'status'"
            [attr.aria-live]="toast.type === 'error' ? 'assertive' : 'polite'"
            aria-atomic="true"
          >
            <nostos-icon
              class="toast-symbol"
              [name]="
                toast.type === 'success'
                  ? 'check-circle'
                  : toast.type === 'error'
                    ? 'warning'
                    : 'info'
              "
              [size]="16"
              aria-hidden="true"
            />
            <span class="toast-message">{{ toast.message }}</span>
          </div>

          <button
            appIconButton
            class="toast-dismiss"
            type="button"
            icon="x"
            size="xxs"
            [glyphSize]="14"
            aria-label="Dismiss notification"
            title="Dismiss notification"
            (click)="toastService.dismiss(toast.id)"
          ></button>
        </div>
      }
    </div>
  `,
  styles: [
    `
      .toast-container {
        --toast-bottom: calc(24px + var(--dock-rail-h) + 12px);
        position: fixed;
        bottom: var(--toast-bottom);
        right: max(1rem, env(safe-area-inset-right, 0px));
        z-index: var(--layer-system);
        display: flex;
        flex-direction: column;
        gap: 0.5rem;
        width: min(
          380px,
          calc(
            100vw - 2rem - env(safe-area-inset-left, 0px) -
              env(safe-area-inset-right, 0px)
          )
        );
        max-height: max(
          0px,
          calc(
            100dvh - var(--toast-bottom) -
              env(safe-area-inset-top, 0px) - 12px
          )
        );
        overflow-y: auto;
        overscroll-behavior: contain;
        pointer-events: auto;
      }

      .toast {
        display: flex;
        align-items: center;
        gap: 0.65rem;
        min-width: 0;
        padding: 0.7rem 0.65rem 0.7rem 0.8rem;
        border: 1px solid var(--border-color);
        border-inline-start: 2px solid transparent;
        border-radius: var(--radius-lg);
        --nostos-accent-rail-width: 2px;
        --nostos-accent-rail-inline-start: -2px;
        background: var(--bg-surface);
        box-shadow: var(--shadow-md);
        color: var(--color-text-main);
        font-size: var(--text-sm);
        pointer-events: auto;
      }

      .toast-announcement {
        display: flex;
        flex: 1 1 auto;
        align-items: flex-start;
        gap: 0.625rem;
        min-width: 0;
      }

      .toast-symbol {
        flex: 0 0 auto;
        margin-top: 0.12rem;
      }

      .toast-message {
        min-width: 0;
        overflow-wrap: anywhere;
        line-height: 1.45;
      }

      .toast-dismiss {
        flex: 0 0 auto;
        border-radius: var(--radius-md);
      }

      .toast-success {
        --nostos-accent-rail-color: var(--color-success);
      }

      .toast-success .toast-symbol {
        color: var(--color-success);
      }

      .toast-error {
        --nostos-accent-rail-color: var(--color-danger);
      }

      .toast-error .toast-symbol {
        color: var(--color-danger);
      }

      .toast-info {
        --nostos-accent-rail-color: var(--color-primary);
      }

      .toast-info .toast-symbol {
        color: var(--color-primary);
      }

      .toast-enter {
        animation: toast-enter var(--motion-fast) var(--ease-out) both;
      }

      .toast-leave {
        animation: toast-leave var(--motion-fast) var(--ease-standard) both;
      }

      @keyframes toast-enter {
        from {
          opacity: 0;
          transform: translateY(6px);
        }
        to {
          opacity: 1;
          transform: translateY(0);
        }
      }

      @keyframes toast-leave {
        from {
          opacity: 1;
          transform: translateY(0);
        }
        to {
          opacity: 0;
          transform: translateY(4px);
        }
      }

      @media (max-width: 768px) {
        .toast-container {
          --toast-bottom: calc(
            var(--dock-rail-h) +
              env(safe-area-inset-bottom, 0px) + 12px
          );
          right: max(0.75rem, env(safe-area-inset-right, 0px));
          left: max(0.75rem, env(safe-area-inset-left, 0px));
          width: auto;
        }
      }

      @media (prefers-reduced-motion: reduce) {
        .toast-enter,
        .toast-leave {
          animation: none;
        }
      }
    `,
  ],
})
export class ToastContainerComponent {
  readonly toastService = inject(ToastService);
}
