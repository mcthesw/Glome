import { existsSync, readFileSync, readdirSync, statSync } from 'node:fs';
import { join, relative, resolve, sep } from 'node:path';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../', import.meta.url));
const docs = join(root, 'site/src/content/docs');
const dist = join(root, 'site/dist');
const addon = join(root, 'addons/glome');
const require = createRequire(new URL('../site/package.json', import.meta.url));
const { parse: parseYaml } = require('yaml');
const { parse: parseHtml, parseFragment } = require('parse5');
const markdownRequire = createRequire(require.resolve('@astrojs/markdown-remark'));
const { unified } = markdownRequire('unified');
const { default: remarkParse } = markdownRequire('remark-parse');
const markdownParser = unified().use(remarkParse);
const origin = 'https://glome.sworld.club';
const errors = [];
const skipSymbols = process.argv.includes('--skip-content-symbols');

function files(directory) {
  return readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
    const path = join(directory, entry.name);
    return entry.isDirectory() ? files(path) : [path];
  });
}

function read(path) {
  return readFileSync(path, 'utf8');
}

function fail(path, reason) {
  errors.push(`${relative(root, path)}: ${reason}`);
}

function checkReadme() {
  const path = join(root, 'README.md');
  function checkTarget(value) {
    if (!value || /^(?:[a-z][a-z\d+.-]*:|\/|#)/i.test(value)) return;
    try {
      const target = resolve(root, decodeURIComponent(value.split(/[?#]/, 1)[0]));
      if (!existsSync(target)) fail(path, `Missing local link or image target: ${value}`);
    } catch {
      fail(path, `Invalid local link or image target: ${value}`);
    }
  }
  function visitHtml(node) {
    for (const { name, value } of node.attrs ?? []) {
      if (name === 'href' || name === 'src') checkTarget(value);
    }
    for (const child of node.childNodes ?? []) visitHtml(child);
  }
  function visit(node) {
    if (['link', 'image', 'definition'].includes(node.type)) checkTarget(node.url);
    if (node.type === 'html') visitHtml(parseFragment(node.value));
    for (const child of node.children ?? []) visit(child);
  }
  visit(markdownParser.parse(read(path)));
}

function routeMap(directory, english = false) {
  const routes = new Map();
  for (const path of files(directory)) {
    const name = relative(directory, path).split(sep).join('/');
    if (!/\.mdx?$/.test(name) || (english && name.startsWith('zh-cn/'))) continue;
    const frontmatter = read(path).match(/^---\r?\n([\s\S]*?)\r?\n---/);
    if (!frontmatter) {
      fail(path, 'Missing frontmatter');
      continue;
    }
    const { slug } = parseYaml(frontmatter[1]);
    const route = slug ?? name.replace(/\.mdx?$/, '').replace(/(^|\/)index$/, '');
    if (routes.has(route)) fail(path, `Duplicate route: ${route || '/'}`);
    routes.set(route, path);
  }
  return routes;
}

const english = routeMap(docs, true);
const chinese = routeMap(join(docs, 'zh-cn'));
for (const [route, path] of english) if (!chinese.has(route)) fail(path, 'Missing Chinese counterpart');
for (const [route, path] of chinese) if (!english.has(route)) fail(path, 'Missing English counterpart');
if (!english.size) fail(docs, 'No English documentation found');

function checkLinks() {
  if (!existsSync(dist)) {
    fail(dist, 'Missing build output; run pnpm --dir site build first');
    return 0;
  }
  const llms = join(dist, 'llms.txt');
  if (!existsSync(llms) || !read(llms).trim()) fail(llms, 'Missing or empty llms.txt');

  const htmlFiles = files(dist).filter(path => path.endsWith('.html'));
  if (!htmlFiles.length) fail(dist, 'No built HTML found');
  let links = 0;
  const targets = new Map();
  for (const path of htmlFiles) {
    const html = parseHtml(read(path), { sourceCodeLocationInfo: true });
    const route = relative(dist, path).split(sep).join('/').replace(/(^|\/)index\.html$/, '$1');
    const base = new URL('/' + route, origin);
    function visit(node, inMain = false, inCode = false) {
      inMain ||= node.tagName === 'main';
      inCode ||= ['pre', 'code', 'script', 'style', 'math'].includes(node.tagName);
      if (inMain && !inCode && node.nodeName === '#text' && node.value.includes('**')) {
        const line = node.sourceCodeLocation?.startLine ?? '?';
        fail(path, `line ${line}: Unparsed Markdown bold marker in rendered prose`);
      }
      for (const { name, value } of node.attrs ?? []) {
        if (!['href', 'src'].includes(name) || !value.trim()) continue;
        let url;
        try {
          url = new URL(value, base);
        } catch {
          fail(path, `Invalid ${name}: ${value}`);
          continue;
        }
        if (url.origin !== origin) continue;
        links++;
        let target;
        try {
          const decoded = decodeURIComponent(url.pathname);
          target = resolve(dist, '.' + decoded);
        } catch {
          fail(path, `Invalid encoded path: ${value}`);
          continue;
        }
        if (target !== dist && !target.startsWith(dist + sep)) {
          fail(path, `Path escapes build directory: ${value}`);
          continue;
        }
        if (!targets.has(target)) {
          const found = [target, join(target, 'index.html'), target + '.html'].some(candidate =>
            existsSync(candidate) && statSync(candidate).isFile());
          targets.set(target, found);
        }
        if (!targets.get(target)) {
          const line = node.sourceCodeLocation?.attrs?.[name]?.startLine ?? '?';
          fail(path, `line ${line}: Broken internal ${name}: ${value}`);
        }
      }
      for (const child of node.childNodes ?? []) visit(child, inMain, inCode);
      if (node.content) visit(node.content, inMain, inCode);
    }
    visit(html);
  }
  return links;
}

const links = checkLinks();
checkReadme();

function containsSymbol(text, symbol) {
  return new RegExp(`\\b${symbol}\\b`).test(text);
}

function containsPair(text, symbol, number) {
  return text.split(/\r?\n/).some(line => containsSymbol(line, symbol) &&
    new RegExp(`(?<![\\w.])${number}(?![\\w.])`).test(line));
}

async function checkSymbols() {
  const location = {
    nodes: ['Runtime/S3Space3D.cs', 'Runtime/S3Anchor3D.cs', 'Runtime/S3Camera3D.cs',
      'Runtime/S3CharacterController.cs', 'Audio/S3AudioRig.cs'],
    'resources-and-runtime': ['Rendering/S3Environment.cs', 'Rendering/S3RenderProgram.cs'],
  };
  const exports = [];
  for (const [page, paths] of Object.entries(location)) {
    for (const name of paths) {
      const path = join(addon, name);
      const source = read(path);
      const type = name.split('/').at(-1).replace('.cs', '');
      const markers = [...source.matchAll(/\[Export(?:\([^\]]*\))?\]/g)];
      if (!markers.length) fail(path, 'No [Export] declarations found');
      for (const marker of markers) {
        const after = source.slice(marker.index + marker[0].length);
        const property = after.match(/^\s*public\s+(?:(?:new|override|virtual|required)\s+)*[\w<>,.?\[\]\s]+?\s+(\w+)\s*(?:\{|=>)/);
        if (!property) fail(path, `Cannot parse [Export] property near line ${source.slice(0, marker.index).split('\n').length}`);
        else exports.push({ page, type, property: property[1] });
      }
    }
  }

  const abi = join(addon, 'Shaders/glome_abi.glslinc');
  const bindings = [...read(abi).matchAll(/layout\([^)]*\bset\s*=\s*0\s*,\s*binding\s*=\s*(\d+)\s*\)\s*(?:(?:readonly|writeonly)\s+)?(uniform|buffer)\s+(?:(?:readonly|writeonly)\s+)?(\w+)(?:\s+(\w+))?/g)]
    .map(([, number, kind, type, symbol]) => ({ number: Number(number), symbol: kind === 'buffer' || !symbol ? type : symbol }));
  if (bindings.map(b => b.number).sort((a, b) => a - b).join(',') !== Array.from({ length: 15 }, (_, i) => i).join(',')) {
    fail(abi, 'Expected Set 0 ABI 1 bindings 0–14 exactly once');
  }

  const intersectionPath = join(addon, 'Shaders/glome_intersections.glslinc');
  const macros = [...new Set([...read(intersectionPath).matchAll(/^\s*#ifn?def\s+(\w+)/gm)].map(match => match[1]))];
  if (!macros.length) fail(intersectionPath, 'No shader macros found');

  const bufferPath = join(addon, 'Rendering/GlomeBuffers.cs');
  const bufferSource = read(bufferPath);
  const strides = [...bufferSource.matchAll(/\bCheck<(\w+)>\(\s*(\d+)\s*,([\s\d,]+)\);/g)]
    .map(([, symbol, number, offsets]) => ({
      symbol, number: Number(number), offsets: offsets.split(',').map(offset => Number(offset.trim())),
    }));
  if (!strides.length) fail(bufferPath, 'No ABI stride assertions found');
  const fieldCount = await checkAbiLayout(bufferPath, bufferSource, strides);

  for (const routes of [english, chinese]) {
    const pages = new Map();
    for (const name of [...Object.keys(location), 'compute-abi']) {
      const path = routes.get('reference/' + name);
      if (!path) fail(docs, `Missing reference route: reference/${name}`);
      else pages.set(name, { path, text: read(path) });
    }
    for (const { page, type, property } of exports) {
      const doc = pages.get(page);
      if (doc && (!containsSymbol(doc.text, type) || !containsSymbol(doc.text, property))) {
        fail(doc.path, `Missing exported property ${type}.${property}`);
      }
    }
    const doc = pages.get('compute-abi');
    if (!doc) continue;
    for (const { symbol, number } of bindings) {
      if (!containsPair(doc.text, symbol, number)) fail(doc.path, `Missing binding ${number}: ${symbol}`);
    }
    for (const { symbol, number } of strides) {
      if (!containsPair(doc.text, symbol, number)) fail(doc.path, `Missing ${symbol} size: ${number} bytes`);
    }
    for (const macro of macros) {
      if (!containsSymbol(doc.text, macro)) fail(doc.path, `Missing shader macro: ${macro}`);
    }
  }
  return `${exports.length} exported properties, ${bindings.length} bindings, ${strides.length} strides, ${fieldCount} field offsets, ${macros.length} macros`;
}

async function checkAbiLayout(bufferPath, bufferSource, strides) {
  const layoutPath = join(root, 'site/src/components/diagrams/abi-layout.ts');
  const { abi } = await import(new URL('../site/src/components/diagrams/abi-layout.ts', import.meta.url));
  const entries = abi.filter(entry => entry.slots);
  const seen = new Set();
  let fieldCount = 0;
  for (const { symbol, number, offsets } of strides) {
    // The diagram's host description ends with the corresponding C# type.
    const matches = entries.filter(entry => entry.host.split(/\s+/).at(-1) === symbol);
    if (matches.length !== 1) {
      fail(layoutPath, `Expected one layout for ${symbol}, found ${matches.length}`);
      continue;
    }
    const entry = matches[0];
    if (seen.has(entry)) {
      fail(bufferPath, `Duplicate ABI assertions for ${symbol}`);
      continue;
    }
    seen.add(entry);
    const declaration = bufferSource.match(new RegExp(`\\bstruct ${symbol}\\s*\\{([\\s\\S]*?)^\\}`, 'm'));
    const members = [...(declaration?.[1] ?? '').matchAll(/^\s*public\s+\w+\s+(\w+(?:\s*,\s*\w+)*)\s*;/gm)]
      .flatMap(match => match[1].split(/\s*,\s*/));
    if (members.length !== offsets.length) {
      fail(bufferPath, `Cannot match ${symbol} fields to its ${offsets.length} offset assertions`);
      continue;
    }

    const size = `${number} B`;
    if (entry.size !== size || entry.slots.length * 16 !== number) {
      fail(layoutPath, `${symbol} layout size does not match ${size}`);
    }
    const actual = entry.slots.flatMap((slot, row) => {
      let words = 0;
      const fields = slot.map((field, column) => {
        const offset = row * 16 + words * 4;
        if (!Number.isInteger(field.words) || field.words <= 0 || words !== column) {
          fail(layoutPath, `Invalid slot placement for ${symbol}.${field.host}`);
        }
        words += field.words;
        return { name: field.host, offset };
      });
      if (words !== 4) fail(layoutPath, `${symbol} slot ${row} must contain four words`);
      return fields;
    });
    if (actual.length !== offsets.length) {
      fail(layoutPath, `${symbol} has ${actual.length} diagram fields; expected ${offsets.length}`);
    }
    for (const [index, offset] of offsets.entries()) {
      fieldCount++;
      const field = actual[index];
      if (field?.name !== members[index] || field.offset !== offset) {
        fail(layoutPath, `${symbol}.${members[index]} must be at byte ${offset}; found ${field ? `${field.name} at byte ${field.offset}` : 'no field'}`);
      }
    }
  }
  for (const entry of entries) {
    if (!seen.has(entry)) fail(layoutPath, `No C# ABI assertions for diagram layout ${entry.host}`);
  }
  return fieldCount;
}

const symbols = skipSymbols ? 'API/ABI symbol check skipped' : await checkSymbols();
if (errors.length) {
  console.error(errors.join('\n'));
  process.exitCode = 1;
} else {
  console.log(`Docs sync passed (${english.size} bilingual routes, ${links} internal links, ${symbols}).`);
}
