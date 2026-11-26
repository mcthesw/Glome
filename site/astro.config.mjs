import { defineConfig } from 'astro/config';
import { copyFile } from 'node:fs/promises';
import starlight from '@astrojs/starlight';
import { unified } from '@astrojs/markdown-remark';
import remarkMath from 'remark-math';
import rehypeKatex from 'rehype-katex';
import llmsTxt from './integrations/llms-txt.mjs';
import design from './design-contract.json' with { type: 'json' };

// Shiki resolves TextMate scopes at build time, before CSS variables exist.
const codeThemes = Object.entries(design.colors).map(([type, palette]) => ({
  name: type === 'dark' ? 'gloam-dusk' : 'surveyor-paper',
  type,
  colors: {
    'editor.background': palette['--gl-surface'],
    'editor.foreground': palette['--gl-text'],
  },
  settings: [
    { scope: ['comment', 'punctuation.definition.comment'], settings: { foreground: palette['--gl-text-muted'] } },
    { scope: ['keyword', 'storage', 'entity.name.tag'], settings: { foreground: palette['--gl-accent'] } },
    { scope: ['string', 'entity.name.type'], settings: { foreground: palette['--gl-ok'] } },
    { scope: ['constant.numeric', 'constant.language'], settings: { foreground: palette['--gl-warn'] } },
  ],
}));

export default defineConfig({
  site: 'https://glome.sworld.club',
  markdown: { processor: unified({ remarkPlugins: [remarkMath], rehypePlugins: [rehypeKatex] }) },
  integrations: [
    starlight({
      title: 'Glome',
      defaultLocale: 'root',
      disable404Route: true,
      components: {
        Header: './src/components/Header.astro',
        Footer: './src/components/Footer.astro',
        LanguageSelect: './src/components/LanguageSelect.astro',
        MobileTableOfContents: './src/components/MobileTableOfContents.astro',
        MarkdownContent: './src/components/MarkdownContent.astro',
        Hero: './src/components/Hero.astro',
        ThemeProvider: './src/components/ThemeProvider.astro',
        ThemeSelect: './src/components/ThemeSelect.astro',
      },
      locales: {
        root: { label: 'English', lang: 'en' },
        'zh-cn': { label: '简体中文', lang: 'zh-CN' },
      },
      social: [{ icon: 'github', label: 'GitHub', href: 'https://github.com/mcthesw/Glome' }],
      customCss: [
        '@fontsource/ibm-plex-sans/400.css',
        '@fontsource/ibm-plex-sans/600.css',
        '@fontsource/noto-sans-sc/400.css',
        '@fontsource/noto-sans-sc/600.css',
        '@fontsource/jetbrains-mono/400.css',
        'katex/dist/katex.min.css',
        './src/styles/custom.css',
      ],
      expressiveCode: {
        themes: codeThemes,
        minSyntaxHighlightingColorContrast: 0,
        useStarlightDarkModeSwitch: true,
        useStarlightUiThemeColors: false,
        styleOverrides: {
          borderRadius: '6px',
          frames: { frameBoxShadowCssValue: 'none' },
        },
      },
      sidebar: [
        {
          label: 'Getting Started',
          translations: { 'zh-CN': '快速开始' },
          items: ['getting-started/installation', 'getting-started/quickstart', 'getting-started/minimal-walkthrough'],
        },
        {
          label: 'Authoring in S³',
          translations: { 'zh-CN': 'S³ 空间搭建' },
          items: ['authoring/s3-intuition', 'authoring/anchors-and-distortion', 'authoring/materials-lights-collision', 'authoring/editor-workflow'],
        },
        {
          label: 'Rendering Styles',
          translations: { 'zh-CN': '画风定制' },
          items: ['rendering/overview', 'rendering/layer-1-builtin', 'rendering/layer-2-post-process', 'rendering/layer-3-compute-shader'],
        },
        {
          label: 'Advanced Systems',
          translations: { 'zh-CN': '进阶系统' },
          items: ['advanced/spatial-audio', 'advanced/custom-host-and-views', 'advanced/core-library'],
        },
        {
          label: 'Reference',
          translations: { 'zh-CN': 'API 与 ABI 参考' },
          items: ['reference/nodes', 'reference/resources-and-runtime', 'reference/compute-abi', 'reference/core-math'],
        },
        {
          label: 'Prior Art & References',
          translations: { 'zh-CN': '参考文献与致谢' },
          items: ['references/prior-art'],
        },
      ],
    }),
    llmsTxt(),
    {
      name: 'localized-404',
      hooks: {
        'astro:build:done': ({ dir }) => copyFile(new URL('zh-cn/404/index.html', dir), new URL('zh-cn/404.html', dir)),
      },
    },
  ],
});
