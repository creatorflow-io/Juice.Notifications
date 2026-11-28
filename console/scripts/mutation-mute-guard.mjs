// Mutation check for the mute guard (M55 slice A): the mutes spec must FAIL when the guard is gone.
//
//   node scripts/mutation-mute-guard.mjs
//
// 1. Runs src/app/forms/mutes.spec.ts as it is: it must pass (a spec that fails anyway proves nothing).
// 2. Replaces `everyNotification` in the mutes page with `() => false` (no match is ever "everything": the guard and the confirm step
//    never trigger), runs the spec again, and puts the page back whatever happens.
// 3. Exits 0 only if the second run FAILED, so the spec guards the mute-everything guard; 1 if it still passed or the first run failed.
import { spawnSync } from 'node:child_process';
import { readFileSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const console_ = fileURLToPath(new URL('..', import.meta.url));
const page = fileURLToPath(new URL('../projects/juice-js/notifications-admin/src/lib/pages/mutes.page.ts', import.meta.url));
const spec = 'src/app/forms/mutes.spec.ts';

const run = () => spawnSync('npx', ['ng', 'test', '--watch=false', '--include', spec], { cwd: console_, shell: true, encoding: 'utf8' });
const tail = result => `${result.stdout ?? ''}${result.stderr ?? ''}`.split(/\r?\n/).filter(l => l.trim()).slice(-12).join('\n');

const original = readFileSync(page, 'utf8');
const restore = () => writeFileSync(page, original);
for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => { restore(); process.exit(130); });

const baseline = run();
if (baseline.status !== 0) {
  console.error('The mutes spec fails before any mutation:\n' + tail(baseline));
  process.exit(1);
}
console.log('baseline: the mutes spec passes.');

if (!/\beveryNotification,\s*/.test(original) || !original.includes('const REASON_MAX')) {
  console.error('mutes.page.ts no longer imports everyNotification the way this script expects: update the script.');
  process.exit(1);
}
const mutant = original.replace(/\beveryNotification,\s*/, '').replace(/(const REASON_MAX[^\n]*\n)/, '$1const everyNotification = (..._args: unknown[]) => false;\n');
let result;
try {
  writeFileSync(page, mutant);
  result = run();
} finally {
  restore();
}
if (result.status === 0) {
  console.error('MUTATION SURVIVED: the mutes spec still passes with everyNotification() => false, so nothing guards the empty match.');
  process.exit(1);
}
const plain = `${result.stdout}${result.stderr}`.replace(/\u001b\[[0-9;]*m/g, '');
const failed = [...new Set([...plain.matchAll(/×\s+(\S+)/g)].map(m => m[1]))];
console.log(`mutation killed: the spec fails without the guard (${failed.length ? failed.join(', ') : 'see the run output'}).`);
process.exit(0);
