import { Injectable, signal } from '@angular/core';

export type ToastType = 'success' | 'error' | 'info';

/** One button on a toast, such as Undo. Running it dismisses the toast. */
export interface ToastAction {
  label: string;
  run: () => void;
}

export interface Toast {
  id: number;
  message: string;
  type: ToastType;
  action?: ToastAction;
}

const MAX_TOASTS = 3;

@Injectable({ providedIn: 'root' })
export class ToastService {
  private nextId = 0;

  readonly toasts = signal<Toast[]>([]);

  show(message: string, type: ToastType = 'info', durationMs = 4000, action?: ToastAction): void {
    const trimmed = message.trim();
    if (!trimmed) return;

    // Identical messages replace each other, unless either carries an action:
    // two quick "Topic added" toasts each keep their own Undo.
    const existing = action
      ? undefined
      : this.toasts().find((t) => t.message === trimmed && t.type === type && !t.action);
    if (existing) {
      this.dismiss(existing.id);
    }

    const id = this.nextId++;
    const toast: Toast = action ? { id, message: trimmed, type, action } : { id, message: trimmed, type };
    this.toasts.update((list) => [...list.slice(-(MAX_TOASTS - 1)), toast]);

    setTimeout(() => this.dismiss(id), durationMs);
  }

  /** A toast with an action stays a little longer, so the action can be reached. */
  success(message: string, action?: ToastAction): void {
    this.show(message, 'success', action ? 8000 : 4000, action);
  }

  error(message: string): void {
    this.show(message, 'error', 5000);
  }

  info(message: string): void {
    this.show(message, 'info');
  }

  runAction(toast: Toast): void {
    this.dismiss(toast.id);
    toast.action?.run();
  }

  dismiss(id: number): void {
    this.toasts.update((list) => list.filter((t) => t.id !== id));
  }
}
