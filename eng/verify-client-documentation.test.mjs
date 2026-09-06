import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import assert from 'node:assert/strict';
import { moduleHeadings, verifyDocumentation } from './verify-client-documentation.mjs';

function fixture(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'lumio-client-docs-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  function write(relative, text) {
    fs.mkdirSync(path.dirname(path.join(root, relative)), { recursive: true });
    fs.writeFileSync(path.join(root, relative), text);
  }
  const headings = ['职责', '明确不负责什么', 'Source / Compile-Time Dependencies', 'Headless Test Surface', '子模块', '当前阶段与开发节奏'];
  write('README.md', '# Client\nLumioGameEngine .spec/knowledge/features/architecture.md\n'
    + headings.map(h => `## ${h}\n`).join('') + '[session](modules/session/README.md)\n');
  write('LICENSE', 'Test fixture license');
  write('docs/specs/2026-08-27-client-module-architecture-design.md', '## 7. 依赖图\n## 9. 模块 README 契约\n');
  write('modules/session/README.md', '# session\n' + moduleHeadings.map(h => `## ${h}\n`).join(''));
  return { root, write };
}

test('living architecture requires no deleted frozen-baseline mirror', t => {
  const { root } = fixture(t);
  assert.deepEqual(verifyDocumentation(root), []);
});
test('new modules require their own contract and index without a fixed module count', t => {
  const { root, write } = fixture(t);
  write('modules/new-module/README.md', '# new-module\n' + moduleHeadings.map(h => `## ${h}\n`).join(''));
  assert.ok(verifyDocumentation(root).some(e => e.includes('missing module index link')));
  fs.appendFileSync(path.join(root, 'README.md'), '[new](modules/new-module/README.md)\n');
  assert.deepEqual(verifyDocumentation(root), []);
});
test('missing failure semantics still fails', t => {
  const { root, write } = fixture(t);
  write('modules/session/README.md', '# session\n' + moduleHeadings.filter(h => h !== '失败与恢复').map(h => `## ${h}\n`).join(''));
  assert.ok(verifyDocumentation(root).some(e => e.includes('失败与恢复')));
});
test('missing module README fails instead of skipping that module', t => {
  const { root } = fixture(t);
  fs.mkdirSync(path.join(root, 'modules', 'empty'));
  assert.ok(verifyDocumentation(root).some(e => e.includes('modules/empty/README.md: missing')));
});
test('missing architecture-source reference fails', t => {
  const { root } = fixture(t);
  const file = path.join(root, 'README.md');
  fs.writeFileSync(file, fs.readFileSync(file, 'utf8').replace('.spec/knowledge/features/architecture.md', 'old-baseline'));
  assert.ok(verifyDocumentation(root).some(e => e.includes('architecture-source reference')));
});
