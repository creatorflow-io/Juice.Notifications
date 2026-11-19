import { NgModule } from '@angular/core';
import { RouterModule, Routes } from '@angular/router';
import { PageComponent, PageNotfoundComponent } from '@juice-js/layout';
import { MenuModule } from '@juice-js/core';
import { AuthGuard } from '@juice-js/auth';
import { NOTIFICATIONS_ADMIN_ROUTES, notificationsAdminMenus } from '@juice-js/notifications-admin';

/**
 * The Notifications Console is the admin pages (`@juice-js/notifications-admin`) mounted at the root of a juice-layout shell, each behind
 * sign-in. The pages' own guards run beside it: their access read waits for the token sign-in brings.
 */
const routes: Routes = [
  {
    path: '',
    component: PageComponent,
    children: [
      ...NOTIFICATIONS_ADMIN_ROUTES.map(route => ({ ...route, canActivate: [AuthGuard, ...(route.canActivate ?? [])] })),
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
