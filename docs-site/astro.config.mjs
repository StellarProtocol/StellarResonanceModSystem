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
      favicon: '/favicon.svg',
      social: [
        { icon: 'github', label: 'GitHub', href: REPO },
        { icon: 'discord', label: 'Discord', href: 'https://discord.gg/8T8gYQhgtu' },
      ],
      editLink: { baseUrl: `${REPO}/edit/main/` },
      lastUpdated: false,
      customCss: ['./src/styles/stellar.css'],
      head: [{ tag: 'script', attrs: { src: '/js/diagram-lightbox.js', defer: true } }],
      components: {
        ThemeProvider: './src/components/ThemeProvider.astro',
        ThemeSelect: './src/components/ThemeSelect.astro',
        EditLink: './src/components/EditLink.astro',
        Hero: './src/components/Hero.astro',
        Header: './src/components/Header.astro',
        SiteTitle: './src/components/SiteTitle.astro',
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
            { label: 'Overview', slug: 'contribute' },
            { label: 'Dev environment', link: '/contribute/dev-environment/' },
            { label: 'Coding standards', link: '/contribute/coding-standards/' },
            { label: 'Expose a wire field', link: '/contribute/expose-a-wire-field/' },
            { label: 'Testing', link: '/contribute/testing/' },
            { label: 'Changing the public API', link: '/contribute/api-changes/' },
          ],
        },
        {
          label: 'Architecture',
          items: [
            { label: 'Overview', slug: 'architecture' },
            { label: 'Interactive diagrams', slug: 'architecture/diagrams' },
            { label: 'Game phases (design)', slug: 'architecture/game-phases' },
          ],
        },
        {
          label: 'Reference',
          items: [
            { label: 'API reference', slug: 'api', badge: { text: 'gen', variant: 'tip' } },
            { label: 'Wire coverage', link: '/wire/', badge: { text: 'gen', variant: 'tip' } },
            { label: 'Plugin gallery', link: '/plugins/', badge: { text: 'gen', variant: 'tip' } },
            { label: 'Changelog', slug: 'changelog', badge: { text: 'gen', variant: 'tip' } },
          ],
        },
      ],
    }),
  ],
});
