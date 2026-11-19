import { DestroyRef, ResourceRef, Signal, computed, inject, isSignal, linkedSignal, signal } from '@angular/core';
import { RxResourceOptions, rxResource, takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Observable, map } from 'rxjs';
import { Page } from '../core/models';

/** A resource whose `value()` is `undefined` while its request failed, instead of throwing. */
export type ApiResource<T> = Omit<ResourceRef<T | undefined>, 'value'> & { readonly value: Signal<T | undefined> };

/**
 * An `rxResource` for an API read, as Juice Observability's Console reads: Angular's `value()` throws while the resource is in error, which
 * would break the page's rendering; here it reads `undefined` then, and `error()` says why.
 */
export function apiResource<T, P>(options: RxResourceOptions<T, P>): ApiResource<T> {
  const ref = rxResource<T, P>(options);
  const value = computed(() => (ref.hasValue() ? ref.value() : undefined));
  return new Proxy(ref, {
    get(target, prop) {
      if (prop === 'value') return value;
      const member = Reflect.get(target, prop, target);
      return typeof member === 'function' && !isSignal(member) ? member.bind(target) : member;
    },
  }) as unknown as ApiResource<T>;
}

/**
 * A list read page by page with the API's cursor. The first page loads whenever `params` change; `more()` appends the next, asked with the
 * same params, so the list grows without shifting, skipping or repeating rows. Call it in an injection context (a field initializer).
 */
export function cursorList<P, T>(params: () => P, fetch: (params: P, cursor: string | null) => Observable<Page<T>>) {
  const destroyRef = inject(DestroyRef);
  const first = apiResource({ params, stream: ({ params }) => fetch(params, null).pipe(map(page => ({ page, params }))) });
  const rest = linkedSignal({ source: first.value, computation: () => ({ items: [] as T[], cursor: undefined as string | null | undefined }) });
  const loadingMore = signal(false);
  const moreError = signal<unknown>(null);
  const items = computed(() => [...(first.value()?.page.items ?? []), ...rest().items]);
  const cursor = computed(() => rest().cursor !== undefined ? rest().cursor! : first.value()?.page.next ?? null);

  function more() {
    const current = first.value();
    const next = cursor();
    if (!current || !next || loadingMore()) return;
    loadingMore.set(true);
    moreError.set(null);
    fetch(current.params, next).pipe(takeUntilDestroyed(destroyRef)).subscribe({
      next: page => rest.update(r => ({ items: [...r.items, ...page.items], cursor: page.next ?? null })),
      error: e => { moreError.set(e); loadingMore.set(false); },
      complete: () => loadingMore.set(false),
    });
  }

  return { first, items, hasMore: computed(() => cursor() !== null), loadingMore, moreError: moreError.asReadonly(), more };
}
