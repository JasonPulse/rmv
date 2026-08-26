# Content

Markdown lives here and is read at request time, so posting does not need a
rebuild. `docker-compose.yml` mounts this directory into the container
read-only, which means on the homelab a new post is a file copy.

Nothing reads this yet. The renderer lands with the news section.

## Intended layout

```
content/
  news/
    2026-08-26-some-slug.md
```

Front matter drives the listing:

```markdown
---
title: A post title
date: 2026-08-26
author: Jason
---

Body in markdown.
```

The `YYYY-MM-DD-slug.md` filename is the URL: the file above serves at
`/news/some-slug`. Date comes from front matter, not the filename, so a typo in
one does not silently reorder the listing.
