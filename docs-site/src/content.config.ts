import { defineCollection } from 'astro:content';
import { z } from 'astro/zod';
import { docsLoader } from '@astrojs/starlight/loaders';
import { docsSchema } from '@astrojs/starlight/schema';

export const collections = {
  docs: defineCollection({
    loader: docsLoader(),
    schema: docsSchema({
      extend: z.object({
        // Repo-relative path of the file a generated page came from; drives the "Edit on GitHub" link.
        sourcePath: z.string().optional(),
        // True for pages generated from code (API reference, changelog) — they link to their source, not an editor.
        generated: z.boolean().optional(),
      }),
    }),
  }),
};
