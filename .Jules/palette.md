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
## 2024-04-24 - Convert Clickable Divs to Native Buttons for Interactive Chips
**Learning:** Found a pattern in the Blazor app where `.filter-chip` and `.category-chip` elements were implemented as clickable `<div>` elements. This makes them inaccessible to keyboard users and screen readers, as they lack semantic meaning, focus states, and the ability to be activated via the Enter/Space keys.
**Action:** Always convert interactive "chip" or "pill" components from `<div>` to `<button type="button">`. Add `aria-pressed="true|false"` when they act as toggles, and ensure `:focus-visible` styles are implemented to support keyboard navigation.
## 2025-04-24 - Convert Clickable Divs to Native Buttons for Lists and Cards
**Learning:** Found an accessibility issue pattern where card layouts acting as actions (like "Add Player" card, "New Storyline" empty state, or character lists) were marked up as clickable `<div>` elements. This makes them inaccessible to screen readers and keyboard users as they lack semantic meaning and standard button focus/activation behaviors.
**Action:** When creating or fixing clickable card elements, ensure the main interaction area uses a native `<button>` element with appropriate `aria-label` tags and `:focus-visible` CSS outlines to preserve keyboard usability and screen reader accessibility.
