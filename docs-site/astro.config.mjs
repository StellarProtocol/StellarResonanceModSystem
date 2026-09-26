// docs.stellarresonance.app — Starlight site for the Stellar framework.
// Content is GENERATED into src/content/docs/ by scripts/gen-all.mjs (from ../docs, ../CONTRIBUTING.md,
// ../CHANGELOG.md and the Stellar.Abstractions XML docs); only src/content/docs/index.mdx is hand-written here.
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';

const REPO = 'https://github.com/StellarProtocol/StellarResonanceModSystem';

export default defineConfig({
  site: 'https://docs.stellarresonance.app',
  integrations: [
    starlight({
      title: 'Stellar Docs',
      description: 'Build plugins for Blue Protocol: Star Resonance with the Stellar framework.',
      logo: { src: './src/assets/logo.svg', replacesTitle: false },
      favicon: '/favicon.svg',
      social: [
        { icon: 'github', label: 'GitHub', href: REPO },
        { icon: 'discord', label: 'Discord', href: 'https://discord.gg/8T8gYQhgtu' },
      ],
      editLink: { baseUrl: `${REPO}/edit/main/` },
      lastUpdated: false,
      customCss: ['./src/styles/stellar.css'],
      components: {
        ThemeProvider: './src/components/ThemeProvider.astro',
        ThemeSelect: './src/components/ThemeSelect.astro',
        EditLink: './src/components/EditLink.astro',
        Hero: './src/components/Hero.astro',
      },
      sidebar: [
        {
          label: 'Get started',
          items: [
            { label: 'Install and run', slug: 'guides/getting-started' },
          ],
        },
        {
          label: 'Write a plugin',
          items: [
            { label: 'Plugin guide', slug: 'guides/plugin-development' },
          ],
        },
        {
          label: 'Contribute',
          items: [
            { label: 'Contributing', slug: 'contribute' },
          ],
        },
        {
          label: 'Architecture',
          items: [
            { label: 'Overview', slug: 'architecture' },
            { label: 'Interactive diagrams', slug: 'architecture/diagrams' },
          ],
        },
        {
          label: 'Reference',
          items: [
            { label: 'API reference', slug: 'api', badge: { text: 'gen', variant: 'tip' } },
            { label: 'Changelog', slug: 'changelog', badge: { text: 'gen', variant: 'tip' } },
          ],
        },
      ],
    }),
  ],
});
