import { EnvironmentInjector, NgModule, inject, runInInjectionContext } from '@angular/core';
import { CanActivateFn, GuardResult, MaybeAsync, Route, RouterModule, Routes } from '@angular/router';
import { Observable, concatMap, from, isObservable, of, take } from 'rxjs';
import { PageComponent, PageNotfoundComponent } from '@juice-js/layout';
import { MenuModule } from '@juice-js/core';
import { AuthGuard } from '@juice-js/auth';
import { NOTIFICATIONS_ADMIN_ROUTES, notificationsAdminMenus } from '@juice-js/notifications-admin';

/** A guard's answer as an observable of one value. */
function once(result: MaybeAsync<GuardResult>): Observable<GuardResult> {
  return isObservable(result) ? result.pipe(take(1)) : result instanceof Promise ? from(result) : of(result);
}

/**
 * Sign-in first, then the page's own guards, one after another: Angular runs a route's guards together, and the pages' guards ask the API
 * what the user may administer, which must wait for the token (a call made before sign-in completes is refused, and would say "nothing").
 */
function signedInThen(guards: Route['canActivate'] = []): CanActivateFn {
  return (route, state) => {
    const injector = inject(EnvironmentInjector);
    const run = (index: number): Observable<GuardResult> => index >= guards.length ? of(true)
      : once(runInInjectionContext(injector, () => (guards[index] as CanActivateFn)(route, state)))
        .pipe(concatMap(result => result === true ? run(index + 1) : of(result)));
    return once(inject(AuthGuard).canActivate(route, state)).pipe(concatMap(result => result === true ? run(0) : of(result)));
  };
}

/** The admin pages, each behind sign-in, and theirs and their children's guards after it. */
function behindSignIn(route: Route): Route {
  return {
    ...route,
    canActivate: [signedInThen(route.canActivate)],
    children: route.children?.map(child => child.canActivate ? { ...child, canActivate: [signedInThen(child.canActivate)] } : child),
  };
}

/**
 * The Notifications Console is the admin pages (`@juice-js/notifications-admin`) mounted at the root of a juice-layout shell, each behind
 * sign-in.
 */
const routes: Routes = [
  {
    path: '',
    component: PageComponent,
    children: [
      ...NOTIFICATIONS_ADMIN_ROUTES.map(behindSignIn),
      { path: 'auth', loadChildren: () => import('@juice-js/auth').then(m => m.AuthRoutingModule) },
      { path: '**', component: PageNotfoundComponent },
    ],
  },
];

@NgModule({
  imports: [
    // Pages read the tenant id (a parent's parameter) as an input.
    RouterModule.forRoot(routes, { bindToComponentInputs: true, paramsInheritanceStrategy: 'always' }),
    MenuModule.forRoot(notificationsAdminMenus('/')),
  ],
  exports: [RouterModule, MenuModule],
})
export class AppRoutingModule { }
