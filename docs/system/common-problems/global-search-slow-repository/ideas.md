# Ideas

- If one repository stays slow while warm, consider a per-repository window
  override rather than lowering `Search:CommitWindow` globally.
- A pre-warm on push would move the first-search cost off the request path
  entirely; only worth it if the cold build is observed to hurt in practice.
