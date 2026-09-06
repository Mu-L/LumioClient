import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const moduleHeadings = [
  '状态', '责任', '明确不负责什么', '公共入口与出口', '数据与控制流',
  '依赖', '生命周期与线程模型', '失败与恢复', '可观测性', '验证', '目录',
];

export function verifyDocumentation(root) {
  const errors = [];
  const read = (relative) => {
    try {
      const text = fs.readFileSync(path.join(root, relative), 'utf8');
      if (!text.trim()) errors.push(`${relative}: empty file`);
      return text;
    } catch {
      errors.push(`${relative}: missing or unreadable file`);
      return '';
    }
  };
  const hasHeading = (text, heading) => text.split(/\r?\n/u).includes(`## ${heading}`);
  const readme = read('README.md');
  for (const heading of ['职责', '明确不负责什么', 'Source / Compile-Time Dependencies', 'Headless Test Surface', '子模块', '当前阶段与开发节奏']) {
    if (!hasHeading(readme, heading)) errors.push(`README.md: missing heading ${heading}`);
  }
  if (!readme.includes('LumioGameEngine') || !readme.includes('.spec/knowledge/features/architecture.md'))
    errors.push('README.md: missing current architecture-source reference');
  read('LICENSE');
  const design = read('docs/specs/2026-08-27-client-module-architecture-design.md');
  for (const heading of ['7. 依赖图', '9. 模块 README 契约']) {
    if (!hasHeading(design, heading)) errors.push(`module design: missing heading ${heading}`);
  }
  let entries;
  try { entries = fs.readdirSync(path.join(root, 'modules'), { withFileTypes: true }); }
  catch { errors.push('modules/: missing directory'); return errors; }
  const modules = entries.filter(entry => entry.isDirectory()).map(entry => entry.name).sort();
  if (modules.length === 0) errors.push('modules/: no modules discovered');
  for (const name of modules) {
    const relative = `modules/${name}/README.md`;
    const text = read(relative);
    if (!text.split(/\r?\n/u).includes(`# ${name}`)) errors.push(`${relative}: incorrect title`);
    for (const heading of moduleHeadings) {
      if (!hasHeading(text, heading)) errors.push(`${relative}: missing heading ${heading}`);
    }
    if (!readme.includes(`(${relative})`)) errors.push(`README.md: missing module index link ${relative}`);
  }
  return errors;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const root = process.argv[2] ? path.resolve(process.argv[2]) : path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
  const errors = verifyDocumentation(root);
  for (const error of errors) console.error(error);
  if (errors.length) process.exitCode = 1;
  else console.log('Client documentation: current source, module boundaries and index verified.');
}
