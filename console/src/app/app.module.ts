import { ChangeDetectionStrategy, Component, NgModule } from '@angular/core';
import { provideHttpClient, withInterceptorsFromDi } from '@angular/common/http';
import { BrowserModule } from '@angular/platform-browser';
import { BrowserAnimationsModule } from '@angular/platform-browser/animations';
import { RouterOutlet } from '@angular/router';
import { MissingTranslationHandler, TranslatePipe, provideTranslateService } from '@ngx-translate/core';
import { OAuthModule, OAuthService } from 'angular-oauth2-oidc';
import { defer, filter, of, take } from 'rxjs';
import { LAYOUT_TOOLBAR_ITEMS, LayoutModule, UserProfileDialogModule } from '@juice-js/layout';
import { LocalizeModule } from '@juice-js/localize';
import { AuthModule } from '@juice-js/auth';
import { NotificationBellComponent, NotificationsModule } from '@juice-js/notifications';
import { NOTIFICATIONS_ADMIN_SIGNED_IN, provideNotificationsAdmin } from '@juice-js/notifications-admin';
import { AppRoutingModule } from './app-routing.module';
import { authOptions, settings } from './core/settings';
import { ShowKeyHandler } from './core/show-key.handler';

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
    provideNotificationsAdmin({ apiBase: settings.apiBase, basePath: '/' }),
    // Signed in once there is a valid access token: checked when a page subscribes, else when sign-in or a refresh brings one.
    {
      provide: NOTIFICATIONS_ADMIN_SIGNED_IN,
      deps: [OAuthService],
      useFactory: (oauth: OAuthService) => defer(() => oauth.hasValidAccessToken()
        ? of(true)
        : oauth.events.pipe(filter(() => oauth.hasValidAccessToken()), take(1))),
    },
    { provide: LAYOUT_TOOLBAR_ITEMS, useValue: NotificationBellComponent, multi: true },
    provideTranslateService({ missingTranslationHandler: { provide: MissingTranslationHandler, useClass: ShowKeyHandler } }),
  ],
  bootstrap: [AppComponent],
})
export class AppModule { }
