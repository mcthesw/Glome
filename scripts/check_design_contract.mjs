import { readFileSync, readdirSync } from 'node:fs';
import { createRequire } from 'node:module';
import { join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../', import.meta.url));
// Resolve the same MDX parser Astro uses through its declared dependency.
const require = createRequire(new URL('../site/package.json', import.meta.url));
const markdownRequire = createRequire(require.resolve('@astrojs/markdown-remark'));
const { createProcessor } = markdownRequire('@mdx-js/mdx');
const mdxParser = createProcessor({ remarkPlugins: [require('remark-math').default] });
const contract = JSON.parse(readFileSync(join(root, 'site/design-contract.json'), 'utf8'));
const approvedColors = new Set(Object.values(contract.colors).flatMap(Object.values).map(color => color.toLowerCase()));
const approvedFonts = new Set([...contract.fonts.sans, ...contract.fonts.mono].map(font => font.toLowerCase()));
const forbiddenText = contract.forbiddenTextPatterns ?? [];
const allowedColor = /^(?:transparent|currentColor|none|inherit|var\(\s*--(?:gl|sl|ec)-[\w-]+\s*\))$/i;
const colorFunction = /\b(?:rgba?|hsla?|hwb|oklch|oklab|lab|lch|color|color-mix|light-dark|device-cmyk)\s*\(/gi;
const errors = [];
const mask = value => value.replace(/[^\r\n]/g, ' ');

function sourceFiles(directory) {
  return readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) return sourceFiles(path);
    const isDocMdx = path.endsWith('.mdx') &&
      !relative(join(root, 'site/src/content/docs'), path).startsWith('..');
    return /\.(?:css|astro|tsx?|svg)$/.test(path) || isDocMdx ? [path] : [];
  });
}

// Keep only actual style-bearing markup, not Markdown prose, inline code,
// fenced examples, frontmatter or SVG text labels. Preserve source positions.
function mdxStyles(source) {
  const body = source.replace(/^---\r?\n[\s\S]*?\r?\n---(?=\r?\n|$)/, mask);
  const tree = mdxParser.parse(body);
  const ranges = [];
  function keep(node) {
    ranges.push([node.position.start.offset, node.position.end.offset]);
  }
  function visit(node, inSvg = false) {
    if (/^mdxJsx(?:Flow|Text)Element$/.test(node.type)) {
      const name = node.name?.toLowerCase();
      if (name === 'style') {
        keep(node);
        return;
      }
      inSvg ||= name === 'svg';
      for (const attribute of node.attributes) {
        if (inSvg || attribute.name?.toLowerCase() === 'style') keep(attribute);
      }
    }
    for (const child of node.children ?? []) visit(child, inSvg);
  }
  visit(tree);
  let result = '';
  let end = 0;
  for (const [start, stop] of ranges.sort((a, b) => a[0] - b[0])) {
    result += mask(source.slice(end, start)) + source.slice(start, stop);
    end = stop;
  }
  return result + mask(source.slice(end));
}

function checkForbiddenText(path, original) {
  for (const token of forbiddenText) {
    let from = 0;
    while (true) {
      const idx = original.indexOf(token, from);
      if (idx === -1) break;
      const line = original.slice(0, idx).split('\n').length;
      errors.push(`${relative(root, path)}:${line}: Forbidden punctuation/text token: ${JSON.stringify(token)}`);
      from = idx + token.length;
    }
  }
}

function checkFile(path) {
  const original = readFileSync(path, 'utf8');
  function report(index, reason) {
    const line = original.slice(0, index).split('\n').length;
    errors.push(`${relative(root, path)}:${line}: ${reason}`);
  }
  checkForbiddenText(path, original);
  if (path.endsWith('.md')) return;

  let source = original;
  if (path.endsWith('.mdx')) {
    try {
      source = mdxStyles(original);
    } catch (error) {
      report(error.place?.start?.offset ?? error.place?.offset ?? 0, `Cannot parse MDX: ${error.message}`);
      return;
    }
  }

  for (const pattern of contract.forbiddenCssPatterns) {
    const escaped = pattern.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    const expression = escaped.endsWith('\\(') ? escaped.slice(0, -2) + '\\s*\\(' : escaped;
    for (const match of source.matchAll(new RegExp(expression, 'gi'))) {
      report(match.index, `Forbidden CSS pattern: ${pattern}`);
    }
  }

  // Cover stylesheets, inline styles, SVG paints, and colors embedded in component code.
  // Fragment URLs and element IDs are identifiers, not color literals.
  const fragment = /\b(?:href|id)\s*=\s*["']#[\w-]+["']|url\(\s*["']?#[\w-]+["']?\s*\)/gi;
  const colorSource = source.replace(fragment, value => value.replace(/[^\r\n]/g, ' '));
  for (const match of colorSource.matchAll(/#[\da-f]{3,8}(?![\w-])/gi)) {
    if (!approvedColors.has(match[0].toLowerCase())) report(match.index, `Unregistered color: ${match[0]}`);
  }
  for (const match of colorSource.matchAll(colorFunction)) {
    report(match.index, `Color function is not permitted: ${match[0]}`);
  }

  // Match both CSS and JS style object spelling; SVG presentation attributes use '='.
  function checkPaint(styles, offset = 0) {
    const paint = /\b(?:color|background(?:-color|Color)?|fill|stroke|stop-color|stopColor|flood-color|floodColor|lighting-color|lightingColor|border-color|borderColor|outline-color|outlineColor|text-decoration-color|textDecorationColor)\s*["']?\s*[:=]\s*(?:"([^"\r\n]*)"|'([^'\r\n]*)'|([^;{}\r\n,]+))/gi;
    for (const match of styles.matchAll(paint)) {
      const value = (match[1] ?? match[2] ?? match[3]).trim().replace(/\s*!important$/, '');
      // Dynamic expressions and non-paint background values are not literal colors.
      if (!value || /^[{]|^url\(/i.test(value)) continue;
      if (allowedColor.test(value) || approvedColors.has(value.toLowerCase())) continue;
      if (/^var\(/i.test(value)) {
        report(offset + match.index, `Unapproved paint variable: ${value}`);
      } else if (/^[a-z]+$/i.test(value) && !/^(?:initial|unset|revert|revert-layer)$/i.test(value)) {
        report(offset + match.index, `Unregistered named paint: ${value}`);
      }
    }
  }

  function checkFont(value, index) {
    // Split CSS lists outside var() fallback parentheses.
    value = value.trim().replace(/\s*!important$/, '');
    if (/^["'].*["']$/.test(value) && !/['"]\s*,\s*['"]/.test(value)) {
      const quote = value[0];
      if (value.slice(1, -1).includes(',') && !value.slice(1, -1).includes(quote)) value = value.slice(1, -1);
    }
    for (const part of value.split(/,(?![^()]*\))/)) {
      const font = part.trim().replace(/^(['"])(.*?)\1$/, '$2');
      if (approvedFonts.has(font.toLowerCase()) || /^(?:inherit|KaTeX_[\w-]+|var\(.+\))$/i.test(font)) continue;
      report(index, `Unapproved font family: ${font}`);
    }
  }

  function checkShadow(value, index) {
    if (contract.forbiddenBoxShadow && !/^none\s*(?:!important)?$/i.test(value.trim())) {
      report(index, 'box-shadow must be none');
    }
  }

  // CSS declarations can span lines; inline style attributes need their own
  // boundary so trailing markup is not mistaken for part of a declaration.
  function checkDeclarations(styles, offset = 0) {
    const declarations = /(?:\b(font-family|box-shadow)|(--sl-font(?:-mono)?))\s*:\s*([^;{}]+)/gi;
    for (const match of styles.matchAll(declarations)) {
      const check = match[1]?.toLowerCase() === 'box-shadow' ? checkShadow : checkFont;
      check(match[3], offset + match.index);
    }
  }
  const styles = source.replace(/\bstyle\s*=\s*(["'])([\s\S]*?)\1/gi, (match, quote, value, index) => {
    const offset = index + match.indexOf(quote) + 1;
    checkDeclarations(value, offset);
    checkPaint(value, offset);
    return mask(match);
  });
  checkPaint(styles);
  const attributes = /\bfont-family\s*=\s*(["'])([\s\S]*?)\1/gi;
  for (const match of source.matchAll(attributes)) checkFont(match[2], match.index);

  // JS style objects use camelCase or quoted CSS keys. Their string literal
  // ends before the next object member, not at a CSS semicolon.
  const objectStyles = /(?:\b(fontFamily|boxShadow)|["'](font-family|box-shadow)["'])\s*:\s*(["'`])([^\r\n]*?)\3/g;
  const css = styles.replace(objectStyles, (match, camel, kebab, quote, value, index) => {
    const check = /box/i.test(camel ?? kebab) ? checkShadow : checkFont;
    check(value, index);
    return mask(match);
  });
  checkDeclarations(css);
}

const files = [
  ...sourceFiles(join(root, 'site/src')),
  ...sourceFiles(join(root, 'site/public')),
  join(root, 'site/astro.config.mjs'),
  join(root, 'README.md'),
];
for (const file of files) checkFile(file);
if (errors.length) {
  console.error(errors.join('\n'));
  process.exitCode = 1;
} else {
  console.log(`Design contract passed (${files.length} files).`);
}
