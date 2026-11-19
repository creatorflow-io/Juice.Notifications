import { ChangeDetectionStrategy, Component, NgModule, inject } from '@angular/core';
import { provideHttpClient, withInterceptorsFromDi } from '@angular/common/http';
import { BrowserModule } from '@angular/platform-browser';
import { BrowserAnimationsModule } from '@angular/platform-browser/animations';
import { RouterOutlet } from '@angular/router';
import { MissingTranslationHandler, TranslatePipe, provideTranslateService } from '@ngx-translate/core';
import { OAuthModule, OAuthService } from 'angular-oauth2-oidc';
import { Observable, defer, filter, map, of, take } from 'rxjs';
import { LAYOUT_TOOLBAR_ITEMS, LayoutModule, UserProfileDialogModule } from '@juice-js/layout';
import { LocalizeModule } from '@juice-js/localize';
import { AuthModule } from '@juice-js/auth';
import { NotificationBellComponent, NotificationsModule } from '@juice-js/notifications';
import { provideNotificationsAdmin } from '@juice-js/notifications-admin';
import { AppRoutingModule } from './app-routing.module';
import { authOptions, settings } from './core/settings';
import { ShowKeyHandler } from './core/show-key.handler';

/**
 * Who is signed in, for the admin pages: the subject once there is a valid access token, checked on each subscription, else when sign-in
 * or a refresh brings one.
 */
function signedIn(): Observable<string> {
  const oauth = inject(OAuthService);
  const user = () => String((oauth.getIdentityClaims() as { sub?: string } | null)?.sub ?? tokenSubject(oauth.getAccessToken()));
  return defer(() => oauth.hasValidAccessToken()
    ? of(user())
    : oauth.events.pipe(filter(() => oauth.hasValidAccessToken()), take(1), map(user)));
}

/** The access token's own subject (never the token itself: it changes on each refresh, and isn't kept as an id); '' if unreadable. */
function tokenSubject(token: string): string {
  try {
    const payload = token.split('.')[1].replace(/-/g, '+').replace(/_/g, '/');
    return String((JSON.parse(atob(payload.padEnd(Math.ceil(payload.length / 4) * 4, '='))) as { sub?: unknown }).sub ?? '');
  } catch {
    return '';
  }
}

@Component({
  selector: 'app-root',
  template: '<router-outlet />',
  imports: [RouterOutlet],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AppComponent { }

/**
 * The Notifications Console (phase-4c-plan): the juice-layout shell (toolbar, menu, theme, user profile) with sign-in through the
 * deployment's identity provider, the admin pages, and the signed-in admin's own bell (`@juice-js/notifications`) in the toolbar.
 */
@NgModule({
  imports: [
    BrowserModule,
    BrowserAnimationsModule,
    LayoutModule.forRoot(true, { brand: 'Juice Notifications', brandLogo: 'logo.svg', defaultMenuOpen: true }),
    AuthModule.forRoot(authOptions as never),
    UserProfileDialogModule,
    OAuthModule.forRoot(),
    // No remote translations: the Console's text is English as written.
    LocalizeModule.forRoot({ localizeApi: '', cultureApi: '' }),
    // The admin's own inbox, from the same API: their tenant is their token's platform_tenant claim.
    NotificationsModule.forRoot({ apiUrl: settings.apiBase || window.location.origin }),
    TranslatePipe,
    AppRoutingModule,
  ],
  providers: [
    provideHttpClient(withInterceptorsFromDi()),
    provideNotificationsAdmin({ apiBase: settings.apiBase, basePath: '/', signedIn }),
    { provide: LAYOUT_TOOLBAR_ITEMS, useValue: NotificationBellComponent, multi: true },
    provideTranslateService({ missingTranslationHandler: { provide: MissingTranslationHandler, useClass: ShowKeyHandler } }),
  ],
  bootstrap: [AppComponent],
})
export class AppModule { }
