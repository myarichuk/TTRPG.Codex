## 2025-02-13 - [Empty State UX for Campaign List]
**Learning:** Empty states are a critical, often missed UX opportunity. When lists return empty, showing a clear, friendly call to action directly improves onboarding and accessibility compared to a blank screen or a static "No items" message.
**Action:** When working on lists or tables, check if there's a specific empty state. If not, consider adding a friendly icon, a brief explanatory message, and an action button to guide the user's next step.

## 2025-04-18 - [Icon-Only Buttons missing aria-labels]
**Learning:** Found an accessibility issue pattern across this app's components where icon-only buttons (like edit, delete, external link, or close buttons) lack `aria-label` attributes. Without these, screen readers announce nothing useful for the action.
**Action:** When creating or reviewing UI elements, always ensure buttons containing only icons include an `aria-label` attribute describing the action they perform.

## 2026-04-20 - [Destructive Action Confirmations]
**Learning:** Found a pattern where destructive actions (like deleting items from a list) lacked confirmation dialogs, leading to potentially frustrating accidental deletions.
**Action:** Always add a confirmation step (like a native browser confirm or custom dialog) for destructive actions.
