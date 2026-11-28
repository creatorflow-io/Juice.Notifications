// M55 slice B: the admin forms' layout, in a browser. For the integration, route, mute, allowlist and settings forms, at 1440, 1024 and 390 px,
// light and dark (30 shots, e2e/shots/forms-<form>-<width>-<theme>.png): the form is opened, filled with long values (300 characters, a 40-entry
// ports list) that every rule refuses, submitted, and measured:
//   - each field error lies inside its own mat-form-field, and the summary is the same size as the errors;
//   - no two fields overlap, nor a field and a button; the form and the page don't scroll sideways; no label is clipped;
//   - the first row's fields keep their top edge between pristine and errored;
//   - error text is at least 4.5:1 against its card, from the resolved colours.
// And the failure path: with the API unreachable, a form shows one sentence and no empty mat-error.
//
//   BASE=http://localhost:4300 node e2e/forms-layout.mjs        (ng serve of this console, the local stack up)
//
// Exits 0 with "skipped" when the stack (or its deploy/local/.env) isn't there, as the .NET integration tests skip.
const skip = reason => {
  console.log(`skipped: ${reason}`);
  process.exit(0);
};

let lib;
try {
  lib = await import('./lib.mjs');
} catch (e) {
  skip(`the local stack's deploy/local/.env can't be read (${e.code ?? e.message})`);
}
const { launch, signIn, close, check, shot, finish, base, keycloak, testTenant } = lib;
const reachable = async url => { try { return (await fetch(url, { signal: AbortSignal.timeout(4000) })).status < 500; } catch { return false; } };
if (!await reachable(`${keycloak}/realms/juice`) || !await reachable(base)) skip(`the local stack or the Console at ${base} isn't up`);

const WIDTHS = [1440, 1024, 390];
const THEMES = ['light', 'dark'];
const long = n => 'x'.repeat(n);
const ports = Array.from({ length: 40 }, (_, i) => (i % 5 === 3 ? String(70000 + i) : i % 7 === 6 ? `p${i}` : String(8000 + i))).join(', ');

/** Each form: how it is opened, filled with what every rule refuses, and what submits it. */
const forms = {
  integration: {
    path: 'integrations', open: 'integration-new', form: 'integration-form', save: 'integration-save',
    fill: async page => {
      await page.getByTestId('integration-key').fill(`Not A Key ${long(300)}`);
      await page.getByTestId('integration-settings').fill(`{ "bad": ${long(300)}`);
      await page.getByTestId('integration-priority').fill('-4');
      await page.getByTestId('integration-secret-ref').fill(long(300));
    },
  },
  route: {
    path: 'routes', open: 'route-new', form: 'route-form', save: 'route-save',
    fill: async page => {
      await page.getByTestId('route-key').fill(`Not A Key ${long(300)}`);
      await page.getByTestId('route-digest').fill('5000');
      await page.getByTestId('match-types').fill(`${long(300)}.*`);
      await page.getByTestId('match-add-label').click();
      await page.getByTestId('match-label-matcher').fill(long(300));
    },
  },
  mute: {
    path: 'mutes', open: 'mute-new', form: 'mute-form', save: 'mute-save',
    fill: async page => {
      await page.getByTestId('mute-starts').fill('2026-12-02T10:00');
      await page.getByTestId('mute-ends').fill('2026-12-01T10:00');
      await page.getByTestId('mute-reason').fill(long(501));
    },
  },
  allowlist: {
    path: 'allowlist', open: 'allow-new', form: 'allow-form', save: 'allow-save',
    fill: async page => {
      await page.getByTestId('allow-destination').fill(`http://${long(300)}:443/`);
      await page.getByTestId('allow-ports').fill(ports);
    },
  },
  settings: {
    path: 'settings', open: null, form: 'settings-form', save: 'settings-save',
    fill: async page => {
      await page.getByTestId('settings-storm-threshold').fill(long(300));
      await page.getByTestId('settings-request-days').fill('0');
      await page.getByTestId('settings-content-days').fill('99999');
    },
  },
};

/** Measured in the page: boxes, clipping, overlap, sizes and contrast of one form (a function body, run with the form's test id). */
const measure = formId => {
  const form = document.querySelector(`[data-testid="${formId}"]`);
  const box = el => { const r = el.getBoundingClientRect(); return { l: r.left + scrollX, t: r.top + scrollY, r: r.right + scrollX, b: r.bottom + scrollY }; };
  const visible = el => el.getClientRects().length > 0;
  const fields = [...form.querySelectorAll('mat-form-field')].filter(visible);
  const buttons = [...form.querySelectorAll('button')].filter(visible);
  const overlaps = (a, b) => a.l < b.r - 1 && b.l < a.r - 1 && a.t < b.b - 1 && b.t < a.b - 1;
  const ctx = document.createElement('canvas').getContext('2d', { willReadFrequently: true });
  const rgba = css => { ctx.clearRect(0, 0, 1, 1); ctx.fillStyle = '#000'; ctx.fillStyle = css; ctx.fillRect(0, 0, 1, 1); return [...ctx.getImageData(0, 0, 1, 1).data]; };
  const backdrop = el => {
    for (let a = el; a; a = a.parentElement) {
      const c = rgba(getComputedStyle(a).backgroundColor);
      if (c[3] === 255) return c;
    }
    return rgba(getComputedStyle(document.body).backgroundColor).map((v, i) => (i === 3 ? 255 : v));
  };
  const lum = ([r, g, b]) => { const f = v => { v /= 255; return v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4; }; return 0.2126 * f(r) + 0.7152 * f(g) + 0.0722 * f(b); };
  const contrast = el => {
    const bg = backdrop(el);
    const fg = rgba(getComputedStyle(el).color);
    const a = fg[3] / 255;
    const mixed = fg.slice(0, 3).map((v, i) => v * a + bg[i] * (1 - a));
    const [hi, lo] = [lum(mixed), lum(bg)].sort((x, y) => y - x);
    return (hi + 0.05) / (lo + 0.05);
  };
  const errors = [...form.querySelectorAll('[data-testid="field-error"]')].filter(visible);
  const summary = form.querySelector('[data-testid="form-error"]');
  const pairs = [];
  for (let i = 0; i < fields.length; i++) {
    for (let j = i + 1; j < fields.length; j++) if (overlaps(box(fields[i]), box(fields[j]))) pairs.push(`${i}/${j}`);
    buttons.forEach((b, k) => { if (!fields[i].contains(b) && overlaps(box(fields[i]), box(b))) pairs.push(`field ${i}/button ${k}`); });
  }
  const firstGrid = form.querySelector('.na-form-grid');
  return {
    tops: [...(firstGrid?.querySelectorAll('mat-form-field') ?? [])].filter(visible).map(f => Math.round(box(f).t)),
    outside: errors.filter(e => e.tagName === 'MAT-ERROR' && !(() => { const f = e.closest('mat-form-field'); if (!f) return false; const a = box(e), b = box(f);
      return a.l >= b.l - 1 && a.r <= b.r + 1 && a.t >= b.t - 1 && a.b <= b.b + 1; })()).map(e => e.textContent.trim().slice(0, 60)),
    errorCount: errors.length,
    matErrors: form.querySelectorAll('mat-error').length,
    emptyMatErrors: [...form.querySelectorAll('mat-error')].filter(e => !e.textContent.trim()).length,
    overlaps: pairs,
    formOverflow: form.scrollWidth > form.clientWidth + 1,
    pageOverflow: document.documentElement.scrollWidth > innerWidth + 1,
    clippedLabels: [...form.querySelectorAll('mat-form-field label')].filter(l => visible(l) && l.scrollWidth > l.clientWidth + 1).map(l => l.textContent.trim()),
    minContrast: errors.length ? Math.min(...errors.map(contrast)) : null,
    summaryFont: summary ? getComputedStyle(summary).fontSize : null,
    errorFonts: [...new Set(errors.map(e => getComputedStyle(e).fontSize))],
    summaryText: summary ? summary.textContent.trim() : null,
    saveBelowFold: null,
  };
};

const problems = [];
const browser = await launch();
// The local Keycloak names itself by the machine's address (PUBLIC_HOST), which the dev server's config.json (localhost) doesn't: each
// context reads the config with that issuer, so the Console under `ng serve` signs in against the same stack as the one it serves.
if (lib.env.PUBLIC_HOST) {
  const newContext = browser.newContext.bind(browser);
  browser.newContext = async (...args) => {
    const context = await newContext(...args);
    await context.route('**/config.json', async route => {
      const config = await (await route.fetch()).json();
      await route.fulfill({ json: { ...config, auth: { ...config.auth, issuer: `http://${lib.env.PUBLIC_HOST}:8080/realms/juice`, requireHttps: false } } });
    });
    return context;
  };
}
try {
  const page = await signIn(browser, 'tess', problems, { path: `/tenants/${testTenant}/settings`, onRefused: () => {} });
  await page.getByTestId('settings-form').waitFor({ timeout: 15000 });

  const setTheme = async theme => {
    await page.evaluate(t => document.body.classList.toggle('dark', t === 'dark'), theme);
    await page.waitForTimeout(150);
  };
  const openForm = async (name, width, theme) => {
    const f = forms[name];
    await page.setViewportSize({ width, height: 900 });
    await page.goto(`${base}/tenants/${testTenant}/${f.path}`);
    await setTheme(theme);
    if (f.open) {
      const button = page.getByTestId(f.open);
      await button.waitFor({ timeout: 15000 });
      await page.waitForFunction(id => !document.querySelector(`[data-testid="${id}"]`)?.disabled, f.open, { timeout: 15000 });
      await button.click();
    }
    await page.getByTestId(f.form).waitFor({ timeout: 15000 });
    await setTheme(theme);
    await page.waitForTimeout(250);
  };

  for (const name of Object.keys(forms)) {
    const f = forms[name];
    for (const width of WIDTHS) {
      for (const theme of THEMES) {
        const at = `${name} at ${width}px, ${theme}`;
        await openForm(name, width, theme);
        const pristine = await page.evaluate(measure, f.form);
        check(pristine.errorCount === 0 && pristine.matErrors === 0, `${at}: no error before a submit`, problems);
        await f.fill(page);
        // Enter in a field, as a keyboard user submits (the Save button may sit below the fold at 390 px).
        await page.getByTestId(f.form).evaluate(form => form.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })));
        await page.getByTestId('form-error').first().waitFor({ timeout: 10000 }).catch(() => {});
        await page.waitForTimeout(400);
        const m = await page.evaluate(measure, f.form);

        check(m.errorCount > 1, `${at}: shows every error at once (${m.errorCount})`, problems);
        check(m.outside.length === 0, `${at}: every field error lies inside its field${m.outside.length ? ` (outside: ${m.outside.join(' | ')})` : ''}`, problems);
        check(m.overlaps.length === 0, `${at}: no field overlaps another field or a button${m.overlaps.length ? ` (${m.overlaps.join(', ')})` : ''}`, problems);
        check(!m.formOverflow && !m.pageOverflow, `${at}: nothing scrolls sideways (form ${m.formOverflow}, page ${m.pageOverflow})`, problems);
        check(m.clippedLabels.length === 0, `${at}: no label is clipped${m.clippedLabels.length ? ` (${m.clippedLabels.join(', ')})` : ''}`, problems);
        // The first row: the fields sharing the highest top edge (one at 390 px). They start level, and an error under one moves none.
        const top = Math.min(...pristine.tops);
        const row = pristine.tops.map((t, i) => [t, m.tops[i]]).filter(([t]) => Math.abs(t - top) <= 1);
        const moved = row.filter(([a, b]) => b === undefined || Math.abs(a - b) > 1);
        check(row.length > 0 && moved.length === 0,
          `${at}: the first row's ${row.length} field(s) keep their top edge (${row.map(([a]) => a).join(',')} -> ${row.map(([, b]) => b).join(',')})`, problems);
        check(m.minContrast !== null && m.minContrast >= 4.5, `${at}: error text is at least 4.5:1 against its card (${m.minContrast?.toFixed(2)})`, problems);
        check(m.summaryFont !== null && m.errorFonts.length === 1 && m.errorFonts[0] === m.summaryFont,
          `${at}: the summary is the errors' size (${m.summaryFont} vs ${m.errorFonts.join('/')})`, problems);
        check(!/below/i.test(m.summaryText ?? ''), `${at}: the summary never says "see below"`, problems);
        await shot(page, `forms-${name}-${width}-${theme}`);
      }
    }
  }

  // The failure path: the API unreachable gives the form one sentence and no empty mat-error.
  await page.setViewportSize({ width: 1024, height: 900 });
  for (const theme of THEMES) {
    await openForm('allowlist', 1024, theme);
    await page.getByTestId('allow-destination').fill('10.20.30.40');
    await page.getByTestId('allow-ports').fill('8443');
    await page.route(/\/api\/v1\/tenants\/[^/]+\/allowlist/, route => (route.request().method() === 'PUT' ? route.abort('connectionrefused') : route.continue()));
    await page.getByTestId('allow-save').click();
    await page.getByTestId('form-error').waitFor({ timeout: 10000 }).catch(() => {});
    const down = await page.evaluate(measure, 'allow-form');
    await page.unroute(/\/api\/v1\/tenants\/[^/]+\/allowlist/);
    check(down.summaryText?.includes("couldn't be reached") && down.errorCount === 0 && down.matErrors === 0 && down.emptyMatErrors === 0,
      `API down (${theme}): one sentence (${down.summaryText}), no field error, no empty mat-error`, problems);
  }
  await close(page);
} finally {
  await browser.close();
}
await finish(problems);
