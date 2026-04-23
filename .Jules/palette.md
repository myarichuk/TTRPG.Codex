## 2025-02-13 - [Empty State UX for Campaign List]
**Learning:** Empty states are a critical, often missed UX opportunity. When lists return empty, showing a clear, friendly call to action directly improves onboarding and accessibility compared to a blank screen or a static "No items" message.
**Action:** When working on lists or tables, check if there's a specific empty state. If not, consider adding a friendly icon, a brief explanatory message, and an action button to guide the user's next step.

## 2025-04-18 - [Icon-Only Buttons missing aria-labels]
**Learning:** Found an accessibility issue pattern across this app's components where icon-only buttons (like edit, delete, external link, or close buttons) lack `aria-label` attributes. Without these, screen readers announce nothing useful for the action.
**Action:** When creating or reviewing UI elements, always ensure buttons containing only icons include an `aria-label` attribute describing the action they perform.

## 2026-04-20 - [Destructive Action Confirmations]
**Learning:** Found a pattern where destructive actions (like deleting items from a list) lacked confirmation dialogs, leading to potentially frustrating accidental deletions.
**Action:** Always add a confirmation step (like a native browser confirm or custom dialog) for destructive actions.

## 2025-02-14 - Keyboard Inaccessible Interactive Divs
**Learning:** Found a recurring pattern in the Blazor application where `<div>` elements act as buttons (e.g., clicking to create a campaign). Screen readers and keyboard users cannot interact with these pseudo-buttons, as they don't natively receive focus or trigger via Enter/Space.
**Action:** When adding clickable components or 'cards' that behave like buttons, always use native `<button>` tags instead of `<div>`s. This ensures built-in keyboard accessibility, supports the `disabled` state for loading operations, and works natively with screen readers. Additionally, remember to add an `aria-label` and `:focus-visible` indicator.
## 2025-04-23 - Interactive Element Refactor
**Learning:** Found several clickable elements (like large cards or filter chips) built using `<div>` tags with `@onclick` events in the Blazor frontend. Converting them to native `<button>` tags significantly improves accessibility by making them naturally focusable and easily interpreted by screen readers.
**Action:** Always verify that interactive elements, especially custom "cards" or "chips," use semantic `<button>` tags rather than relying on `<div>` tags, and make sure to apply appropriate text alignment utilities (like `text-start`) to overcome default button centering styles when necessary.
