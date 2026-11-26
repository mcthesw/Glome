import { readdir, readFile, writeFile } from 'node:fs/promises';
import { join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';
import { parse } from 'yaml';

const docsDir = fileURLToPath(new URL('../src/content/docs/', import.meta.url));

async function englishDocs(directory = docsDir) {
  const result = [];
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) {
      if (directory !== docsDir || entry.name !== 'zh-cn') result.push(...await englishDocs(path));
    } else if (/\.mdx?$/.test(entry.name)) {
      const source = await readFile(path, 'utf8');
      const frontmatter = source.match(/^---\r?\n([\s\S]*?)\r?\n---/);
      if (!frontmatter) throw new Error(`Missing frontmatter: ${path}`);
      const { title, description, slug, draft } = parse(frontmatter[1]);
      const route = slug ?? relative(docsDir, path).replaceAll('\\', '/').replace(/\.mdx?$/, '').replace(/(^|\/)index$/, '');
      if (route === '404' || draft) continue;
      result.push({ title, description, route });
    }
  }
  return result.sort((a, b) => a.route.localeCompare(b.route, 'en'));
}

export default function llmsTxt() {
  let site;
  return {
    name: 'glome-llms-txt',
    hooks: {
      'astro:config:done': ({ config }) => { site = config.site; },
      'astro:build:done': async ({ dir, pages, logger }) => {
        const lines = [
          '# Glome',
          '',
          '> S³ non-Euclidean geometry engine for Godot 4.7 (.NET, Vulkan compute).',
          '',
          '## Architectural invariants',
          '',
          '- Set 0 ABI 1: bindings 0–14 and a 96-byte Camera push constant; custom programs own Set 1.',
          '- Split charts at the 10% gnomonic radial-compression threshold (tangent offset R/3).',
          '- Glome.Core is a double-precision, zero-Godot library.',
          '',
          '## Documentation',
          '',
        ];
        const builtPaths = new Set(pages.map(({ pathname }) => pathname.replace(/^\/+|\/+$/g, '')));
        for (const { title, description, route } of await englishDocs()) {
          if (!builtPaths.has(route)) throw new Error(`Document not present in built routes: ${route || '/'}`);
          const url = new URL(route ? `${route}/` : '/', site);
          lines.push(`- [${title}](${url})${description ? `: ${description.replace(/\s+/g, ' ').trim()}` : ''}`);
        }
        await writeFile(new URL('llms.txt', dir), lines.join('\n') + '\n');
        logger.info('Generated llms.txt');
      },
    },
  };
}
