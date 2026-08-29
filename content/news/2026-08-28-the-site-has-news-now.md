---
title: The site has news now
date: 2026-08-28
author: Jason
---

Twenty-five years in and we have a news page. It reads markdown out of
`content/news`, so posting is a file copy rather than a deploy.

## What that means

A post is one file named `YYYY-MM-DD-slug.md`. The slug half is the URL, so this
one lives at `/news/the-site-has-news-now`. The date comes from the front matter
rather than the filename, which means a typo in one cannot quietly reorder the
listing.

Three keys at the top and then write:

```markdown
---
title: A post title
date: 2026-08-28
author: Jason
---

Body in markdown.
```

Lists work, `inline code` works, [links](https://www.resultsmayvary.org) work, and
so do quotes:

> We are not currently accepting anyone of any game/class/level.

Raw HTML does not work, on purpose. The renderer has it switched off, so a stray
script tag in a post file is text rather than a problem.
