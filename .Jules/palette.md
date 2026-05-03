## 2024-05-24 - Added Global Focus-Visible Outline
**Learning:** Some custom buttons like `.btn-accent` and `.btn-surface` were lacking `focus-visible` styles, which hampered keyboard navigation visibility.
**Action:** Applied a global `button:focus-visible, a:focus-visible` rule using `--accent-color` in `app.css` to ensure consistent and accessible focus rings across all interactive elements.
